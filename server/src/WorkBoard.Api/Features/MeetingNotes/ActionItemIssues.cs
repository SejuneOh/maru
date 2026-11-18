using System.Text.Json;
using Microsoft.Extensions.Options;
using WorkBoard.Api.Features.Issues;
using WorkBoard.Api.Features.Overview;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.MeetingNotes;

public sealed record ActionItemIssueRequest(Guid? RepositoryId);

// Created is false when the item already had its issue before this request.
public sealed record ActionItemIssueResult(ActionItemDto ActionItem, bool Created, IReadOnlyList<long> DroppedAssignees);

// 202 body: GitHub has the issue but the mirror does not yet; the item shows it once a sync brings it.
// Repository is empty and HtmlUrl null when the repository is no longer in the mirror.
public sealed record PendingActionItemIssue(Guid ActionItemId, string Repository, int Number, string? HtmlUrl, bool MirrorPending);

public sealed class WebOptions
{
    public const string Section = "Web";

    public static readonly string[] DefaultAllowedOrigins = ["http://localhost:3000"];

    // Public address of the web app (scheme and host, optional path), used for links written into GitHub.
    public string? BaseUrl { get; set; }

    // Origins the web app is served from: the CORS policy, and the fallback for links when BaseUrl is not set.
    public string[]? AllowedOrigins { get; set; }

    public string[] Origins => AllowedOrigins is { Length: > 0 } list ? list : DefaultAllowedOrigins;
}

// Creates the GitHub issue for one meeting action item, at most once per item. The item row holds a claim while a
// create runs (see db/migrations/0017): a request with another key waits for the claim to finish or go stale, and a
// request that takes over a stale claim first looks for the issue by a marker derived from the item alone, so neither
// a second click nor a retry under a new key creates a second issue.
public sealed class ActionItemIssues(
    ICurrentUser user, TenantDb db, IssueCreation creation, IGitHubWriteTargets targets, IAuditLog audit,
    IOptions<WebOptions> web)
{
    public const int TitleMaxLength = NewIssueEndpoints.TitleMaxLength;
    public const string EarlierRepositoryUnavailable = "earlier_repository_unavailable";

    // SQL on meeting_action_items aliased `a`: a create is running now (claimed, no number yet, not stale).
    public static readonly string ActiveClaim =
        "(a.issue_claim_key is not null and a.issue_claim_number is null and a.issue_claim_at > now() - "
        + $"make_interval(secs => {(int)Idempotency.PendingTimeout.TotalSeconds}))";

    private sealed class ItemRow
    {
        public Guid Id { get; set; }
        public Guid NoteId { get; set; }
        public string Text { get; set; } = "";
        public string? DueDate { get; set; }
        public Guid? IssueId { get; set; }
        public string? ClaimKey { get; set; }
        public DateTime? ClaimFirstAt { get; set; }
        public Guid? ClaimRepositoryId { get; set; }
        public Guid[]? ClaimRepositoryIds { get; set; }
        public int? ClaimNumber { get; set; }
        public bool ClaimActive { get; set; }
        public string NoteTitle { get; set; } = "";
        public string HeldOn { get; set; } = "";
        public long? AssigneeUserId { get; set; }
        public string? AssigneeLogin { get; set; }
    }

    private sealed record Claimed(IReadOnlyList<EarlierAttempt> Lookups);

    private sealed record AlreadyLinked;

    private sealed record NumberKnown;

    private sealed record PendingRow(Guid? IssueId, string FullName);

    public static string MarkerId(Guid tenantId, Guid itemId) =>
        IssueCreation.MarkerId(tenantId, $"action-item:{itemId:N}", "");

    public async Task<IResult> CreateAsync(
        Guid id, Guid repositoryId, string key, string requestHash, string? origin, ILogger log, CancellationToken ct)
    {
        var item = await db.ReadAsync(s => LoadAsync(s, id, forUpdate: false), ct);
        if (item is null)
            return ApiErrors.NotFoundResult();
        if (item.IssueId is not null)
            return Json(await AnswerAsync(id, created: false, [], ct));
        if (item.ClaimNumber is not null)
            return Json(await LinkPendingAsync(id, ct));

        var target = await targets.ResolveAsync(repositoryId, ct);
        if (target is null)
            return ApiErrors.NotFoundResult();
        if (target.Archived)
            return ApiErrors.ConflictResult(IssueCreation.RepositoryArchived);

        var issue = BuildIssue(item, target, NoteLink(item.NoteId, origin));
        var marker = GitHubWriter.CreateMarker(MarkerId(user.Identity.TenantId, id));
        return await Idempotency.RunExternalAsync(db, user, key, requestHash, async (callCt, _) =>
        {
            var claim = await db.WriteAsync(s => ClaimAsync(s, id, key, target.RepositoryId), callCt);
            switch (claim)
            {
                case WriteResult early:
                    return new ExternalOutcome(early, false);
                case AlreadyLinked:
                    return new ExternalOutcome(await AnswerAsync(id, created: false, [], CancellationToken.None), false);
                case NumberKnown:
                    return new ExternalOutcome(await LinkPendingAsync(id, CancellationToken.None), false);
            }

            // An earlier repository that cannot be reached any more may hold the issue: creating now could duplicate it.
            var lookups = new List<EarlierAttempt>();
            foreach (var earlier in ((Claimed)claim).Lookups)
                if (earlier.Target.RepositoryId == target.RepositoryId)
                    lookups.Add(earlier with { Target = target });
                else if (await targets.ResolveAsync(earlier.Target.RepositoryId, callCt) is { } other)
                    lookups.Insert(0, earlier with { Target = other });
                else
                    return new ExternalOutcome(new WriteResult(StatusCodes.Status409Conflict,
                        new ErrorBody(EarlierRepositoryUnavailable)), false);

            FoundOrCreated result;
            try
            {
                result = await creation.FindOrCreateAsync(issue, marker, lookups, callCt);
            }
            catch (GitHubWriteException e) when (!e.NoResponse)
            {
                if (!e.SideEffectPossible)
                    await ReleaseAsync(id, key);
                return GitHubWriteErrors.ToOutcome(e);
            }

            var dropped = IssueCreation.DroppedAssignees(issue, result.Issue);
            var detail = await creation.SaveAsync(issue, result, log, (s, issueId) => LinkAsync(s, id, issueId, result));
            if (detail is null)
            {
                await RememberNumberAsync(id, key, result, log);
                return new ExternalOutcome(new WriteResult(StatusCodes.Status202Accepted, new PendingActionItemIssue(
                    id, $"{result.Target.Repo.Owner}/{result.Target.Repo.Name}", result.Issue.Number, result.Issue.HtmlUrl, true)), true);
            }
            return new ExternalOutcome(await AnswerAsync(id, created: true, dropped, CancellationToken.None), true);
        }, ct, log);
    }

    private static IResult Json(WriteResult r) => Results.Json(r.Body, JsonSerializerOptions.Web, statusCode: r.StatusCode);

    // An answer that needs no GitHub call (WriteResult, AlreadyLinked, NumberKnown), or Claimed with the lookups to
    // run before creating.
    private async Task<object> ClaimAsync(TenantSession s, Guid id, string key, Guid repositoryId)
    {
        var item = await LoadAsync(s, id, forUpdate: true);
        if (item is null)
            return new WriteResult(StatusCodes.Status404NotFound, new ErrorBody(ApiErrors.NotFound));
        if (item.IssueId is not null)
            return new AlreadyLinked();
        if (item.ClaimNumber is not null)
            return new NumberKnown();
        if (item.ClaimActive && item.ClaimKey != key)
            return new WriteResult(StatusCodes.Status409Conflict, new ErrorBody(ApiErrors.RequestInProgress));

        // A claim still here (stale, or this key's own earlier attempt) may have reached GitHub in any repository it
        // was sent to. The requested one joins that list before the call and none leaves it until the claim ends.
        List<EarlierAttempt> lookups = [];
        if (item.ClaimKey is not null)
        {
            var since = new DateTimeOffset(DateTime.SpecifyKind(item.ClaimFirstAt!.Value, DateTimeKind.Utc));
            foreach (var repo in (item.ClaimRepositoryIds ?? []).Append(repositoryId).Distinct())
                lookups.Add(new EarlierAttempt(Placeholder(repo), since));
        }
        await s.ExecuteAsync("""
            update meeting_action_items
               set issue_claim_key = @key, issue_claim_at = now(),
                   issue_claim_first_at = coalesce(issue_claim_first_at, now()), issue_claim_repository_id = @repositoryId,
                   issue_claim_repository_ids = case when @repositoryId = any(coalesce(issue_claim_repository_ids, '{}'))
                                                     then issue_claim_repository_ids
                                                     else array_append(coalesce(issue_claim_repository_ids, '{}'), @repositoryId) end
             where id = @id
            """, new { id, key, repositoryId });
        return new Claimed(lookups);
    }

    private static GitHubWriteTarget Placeholder(Guid repositoryId) => new(repositoryId, new GitHubRepoRef(new GitHubConnectionRef(Guid.Empty, Guid.Empty, GitHubAuthKind.App, null), "", ""), false);

    private async Task LinkAsync(TenantSession s, Guid id, Guid issueId, FoundOrCreated result)
    {
        var linked = await s.ExecuteAsync("""
            update meeting_action_items
               set issue_id = @issueId, issue_claim_key = null, issue_claim_at = null, issue_claim_first_at = null,
                   issue_claim_repository_id = null, issue_claim_repository_ids = null, issue_claim_number = null
             where id = @id and issue_id is null
            """, new { id, issueId });
        if (linked == 1)
            await audit.WriteAsync(s, "meeting_action_item.issue", "meeting_action_item", id, null, new
            {
                issueId,
                repository = $"{result.Target.Repo.Owner}/{result.Target.Repo.Name}",
                number = result.Issue.Number,
                foundEarlier = result.Found,
            });
    }

    // GitHub refused before creating anything: the item is free again.
    private Task ReleaseAsync(Guid id, string key) =>
        db.WriteAsync(s => s.ExecuteAsync("""
            update meeting_action_items
               set issue_claim_key = null, issue_claim_at = null, issue_claim_first_at = null, issue_claim_repository_id = null,
                   issue_claim_repository_ids = null
             where id = @id and issue_claim_key = @key and issue_claim_number is null
            """, new { id, key }), CancellationToken.None);

    // If this fails too the claim stays, and the next request finds the issue by its marker.
    private async Task RememberNumberAsync(Guid id, string key, FoundOrCreated result, ILogger log)
    {
        try
        {
            await db.WriteAsync(async s =>
            {
                await s.ExecuteAsync("""
                    update meeting_action_items
                       set issue_claim_number = @number, issue_claim_repository_id = @repositoryId
                     where id = @id and issue_id is null and issue_claim_key = @key
                    """, new { id, key, number = result.Issue.Number, repositoryId = result.Target.RepositoryId });
                // A sync may have mirrored the issue between the failed save and now.
                return await s.ExecuteAsync($"""
                    update meeting_action_items a
                       set issue_id = i.id, {PendingLink.ClearClaim}
                      from issues i
                     where a.id = @id and a.issue_id is null and a.issue_claim_number is not null
                       and i.repository_id = a.issue_claim_repository_id and i.number = a.issue_claim_number
                    """, new { id });
            }, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogError(e, "Issue {Number} was created for an action item but its number could not be stored", result.Issue.Number);
        }
    }

    // The item's issue was created earlier but not mirrored then: links it once the mirror has it.
    private async Task<WriteResult> LinkPendingAsync(Guid id, CancellationToken ct)
    {
        var row = await db.WriteAsync(async s =>
        {
            var pending = await s.QuerySingleOrDefaultAsync<PendingRow>("""
                select i.id as issue_id, r.full_name
                  from meeting_action_items a
                  join repositories r on r.id = a.issue_claim_repository_id
                  left join issues i on i.repository_id = r.id and i.number = a.issue_claim_number
                 where a.id = @id and a.issue_id is null and a.issue_claim_number is not null
                 for update of a
                """, new { id });
            if (pending?.IssueId is { } issueId)
            {
                await s.ExecuteAsync($"""
                    update meeting_action_items set issue_id = @issueId, {PendingLink.ClearClaim} where id = @id
                    """, new { id, issueId });
                await audit.WriteAsync(s, "meeting_action_item.issue", "meeting_action_item", id, null,
                    new { issueId, repository = pending.FullName, mirroredLater = true });
            }
            return pending;
        }, ct);
        var item = await db.ReadAsync(s => MeetingNotesEndpoints.GetActionItemAsync(s, id), ct);
        if (item is null)
            return new WriteResult(StatusCodes.Status404NotFound, new ErrorBody(ApiErrors.NotFound));
        if (item.Issue is not null)
            return new WriteResult(StatusCodes.Status200OK, new ActionItemIssueResult(item, false, []));
        var number = item.PendingIssue?.Number ?? 0;
        var fullName = row?.FullName;
        return new WriteResult(StatusCodes.Status202Accepted, new PendingActionItemIssue(
            id, fullName ?? "", number, fullName is null ? null : $"https://github.com/{fullName}/issues/{number}", true));
    }

    private async Task<WriteResult> AnswerAsync(Guid id, bool created, IReadOnlyList<long> dropped, CancellationToken ct)
    {
        var item = await db.ReadAsync(s => MeetingNotesEndpoints.GetActionItemAsync(s, id), ct);
        // Only when the whole note was deleted while its issue was being created.
        return item is null
            ? new WriteResult(StatusCodes.Status404NotFound, new ErrorBody(ApiErrors.NotFound))
            : new WriteResult(created ? StatusCodes.Status201Created : StatusCodes.Status200OK,
                new ActionItemIssueResult(item, created, dropped));
    }

    private static Task<ItemRow?> LoadAsync(TenantSession s, Guid id, bool forUpdate) =>
        s.QuerySingleOrDefaultAsync<ItemRow>($"""
            select a.id, a.note_id, a.text, a.due_date::text as due_date, a.issue_id,
                   a.issue_claim_key as claim_key, a.issue_claim_first_at as claim_first_at,
                   a.issue_claim_repository_id as claim_repository_id, a.issue_claim_repository_ids as claim_repository_ids,
                   a.issue_claim_number as claim_number,
                   {ActiveClaim} as claim_active,
                   n.title as note_title, (n.held_at at time zone '{OverviewQueries.TimeZone}')::date::text as held_on,
                   m.github_user_id as assignee_user_id, m.github_login as assignee_login
              from meeting_action_items a
              join meeting_notes n on n.id = a.note_id
              left join members m on m.id = a.assignee_member_id
             where a.id = @id
            {(forUpdate ? "for update of a" : "")}
            """, new { id });

    // Web:BaseUrl, else the request's Origin when it is one of Web:AllowedOrigins; null when neither applies.
    private string? NoteLink(Guid noteId, string? origin)
    {
        var allowed = origin is not null
            && web.Value.Origins.Any(o => string.Equals(o.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        var baseUrl = string.IsNullOrWhiteSpace(web.Value.BaseUrl) ? (allowed ? origin : null) : web.Value.BaseUrl;
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        return $"{uri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/notes/view?id={noteId}";
    }

    private static NewIssue BuildIssue(ItemRow item, GitHubWriteTarget target, string? link)
    {
        var text = item.Text.Trim();
        var title = text;
        if (title.Length > TitleMaxLength)
        {
            var cut = TitleMaxLength - 1;
            if (char.IsHighSurrogate(title[cut - 1]))
                cut--;
            title = title[..cut] + "…";
        }

        var noteTitle = Escape(item.NoteTitle);
        var lines = new List<string>();
        if (title != text)
            lines.AddRange([Escape(text), ""]);
        lines.Add($"회의록: {(link is null ? noteTitle : $"[{noteTitle}]({link})")} ({item.HeldOn})");
        if (item.DueDate is { } due)
            lines.Add($"마감: {due}");

        IReadOnlyList<(long, string)> assignees = item is { AssigneeUserId: { } userId, AssigneeLogin: { Length: > 0 } login }
            ? [(userId, login)]
            : [];
        return new NewIssue(target, title, string.Join('\n', lines), [], assignees, null, null, null);
    }

    // Text a member typed, made inert in the issue body: no Markdown links, images or emphasis, no HTML (so no
    // comment that could pass for a create marker), and no @mention or #reference that would notify or link.
    public static string Escape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '@': sb.Append("@\u200D"); break;
                case '\\' or '[' or ']' or '(' or ')' or '*' or '_' or '`' or '#' or '!' or '|' or '~':
                    sb.Append('\\').Append(c);
                    break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

public static class ActionItemIssuesEndpoints
{
    private const int MaxBodyBytes = 4 * 1024;

    public static void MapActionItemIssues(this RouteGroupBuilder api) =>
        api.MapPost("/meeting-action-items/{id:guid}/issue", CreateAsync);

    private static async Task<IResult> CreateAsync(
        Guid id, HttpContext http, ICurrentUser user, ActionItemIssues issues, ILogger<ActionItemIssues> log, CancellationToken ct)
    {
        var key = http.Request.Headers[Idempotency.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > Idempotency.KeyMaxLength)
            return ApiErrors.BadRequest();
        var bytes = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        if (bytes is null || !http.Request.HasJsonContentType())
            return ApiErrors.BadRequest();
        ActionItemIssueRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ActionItemIssueRequest>(bytes, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return ApiErrors.BadRequest();
        }
        if (request is not { RepositoryId: { } repositoryId } || repositoryId == Guid.Empty)
            return ApiErrors.BadRequest();

        var hash = Idempotency.HashRequest(
            user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);
        var origin = http.Request.Headers.Origin.ToString();
        return await issues.CreateAsync(id, repositoryId, key, hash, string.IsNullOrEmpty(origin) ? null : origin, log, ct);
    }
}
