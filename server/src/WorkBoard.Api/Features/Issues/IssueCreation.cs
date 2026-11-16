using System.Security.Cryptography;
using System.Text;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Issues;

public sealed record CreateIssueRequest(
    Guid? RepositoryId, string? Title, string? Body, string[]? Labels, long[]? AssigneeGithubUserIds,
    string? Status, string? Priority, string? Size);

// The new issue plus the assignees GitHub did not apply (by GitHub user id).
public sealed record CreatedIssue : IssueDetail
{
    public CreatedIssue(IssueDetail issue, IReadOnlyList<long> droppedAssignees) : base(issue) =>
        DroppedAssignees = droppedAssignees;

    public IReadOnlyList<long> DroppedAssignees { get; init; }
}

// 202 body: the issue exists on GitHub but is not in the mirror yet.
public sealed record PendingIssue(string Repository, int Number, string HtmlUrl, bool MirrorPending);

// A request that passed validation: assignees are already resolved to tenant members.
public sealed record NewIssue(
    GitHubWriteTarget Target, string Title, string? Body, IReadOnlyList<string> Labels,
    IReadOnlyList<(long UserId, string Login)> Assignees, string? Status, string? Priority, string? Size);

public sealed class IssueCreation(
    ICurrentUser user, TenantDb db, IGitHubWriter writer, MirrorWriter mirror, IAuditLog audit, IIssueEvents events)
{
    public const string RepositoryArchived = "repository_archived";

    private sealed record LabelName(string Name);

    // Window before the reservation in which an earlier attempt's issue may have been created (clock skew).
    public static readonly TimeSpan LookupMargin = TimeSpan.FromMinutes(5);

    // Identifies one create request: hashed so the client's raw key never lands in a public issue body, and bound
    // to the request hash so the same key reused for a different body is not mistaken for a retry.
    public static string MarkerId(Guid tenantId, string key, string requestHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}\n{key}\n{requestHash}")))[..32].ToLowerInvariant();

    // Order matters: the GitHub call comes first, then the mirror, then board fields and the audit row. Nothing is
    // written locally when GitHub refuses.
    public Task<IdempotentResponse> CreateAsync(
        NewIssue issue, string key, string requestHash, ILogger log, CancellationToken ct) =>
        Idempotency.RunExternalAsync(db, user, key, requestHash, async (callCt, attempt) =>
        {
            var marker = GitHubWriter.CreateMarker(MarkerId(user.Identity.TenantId, key, requestHash));
            SourceIssue? created = null;
            if (attempt.TakenOverFrom is { } since)
            {
                try
                {
                    created = await writer.FindCreatedIssueAsync(issue.Target.Repo, marker, since - LookupMargin, callCt);
                }
                catch (GitHubWriteException e)
                {
                    // Unknown whether the earlier attempt created the issue: creating now could duplicate it.
                    throw new GitHubWriteException(GitHubWriteErrorKind.Unavailable, "Lookup of an earlier create failed.",
                        noResponse: true, inner: e);
                }
            }

            if (created is null)
                try
                {
                    created = await writer.CreateIssueAsync(issue.Target.Repo, user.Identity.Member.GithubLogin, issue.Title,
                        issue.Body, issue.Labels, issue.Assignees.Select(a => a.Login).ToList(), callCt, marker);
                }
                catch (GitHubWriteException e) when (!e.NoResponse)
                {
                    return GitHubWriteErrors.ToOutcome(e);
                }

            // GitHub has the issue from here on. A local failure must not leave the key pending (a retry would
            // create a second issue), so it completes with a 202 and the next sync fills the rows.
            var pending = new ExternalOutcome(new WriteResult(StatusCodes.Status202Accepted,
                new PendingIssue($"{issue.Target.Repo.Owner}/{issue.Target.Repo.Name}", created.Number, created.HtmlUrl, true)), true);
            try
            {
                var mirrored = await mirror.ApplyWrittenIssueAsync(
                    user.Identity.TenantId, issue.Target.RepositoryId, created, null, null, CancellationToken.None);
                if (mirrored is null)
                {
                    log.LogWarning("Issue {Number} was created on GitHub but the repository is gone from the mirror", created.Number);
                    return pending;
                }

                var dropped = issue.Assignees
                    .Where(a => !created.Assignees.Any(c => string.Equals(c.Login, a.Login, StringComparison.OrdinalIgnoreCase)))
                    .Select(a => a.UserId).ToList();
                var detail = await db.WriteAsync(s => FinishAsync(s, mirrored.IssueId, issue, created), CancellationToken.None);
                return new ExternalOutcome(new WriteResult(StatusCodes.Status201Created, new CreatedIssue(detail, dropped)), true);
            }
            catch (Exception e)
            {
                log.LogError(e, "Issue {Number} was created on GitHub but saving it locally failed", created.Number);
                return pending;
            }
        }, ct, log);

    private async Task<IssueDetail> FinishAsync(TenantSession s, Guid issueId, NewIssue issue, SourceIssue created)
    {
        if (issue.Status is not null || issue.Priority is not null || issue.Size is not null)
        {
            // Same lock as every other board_fields writer.
            await s.ExecuteAsync("select pg_advisory_xact_lock(hashtextextended(@scope, 0))",
                new { scope = $"sprint-assign:{user.Identity.TenantId:N}" });
            await s.ExecuteAsync("""
                insert into board_fields (issue_id, tenant_id, status_override, priority, size)
                values (@issueId, @tenantId, @status, @priority, @size)
                on conflict (issue_id) do update
                   set status_override = coalesce(excluded.status_override, board_fields.status_override),
                       priority = coalesce(excluded.priority, board_fields.priority),
                       size = coalesce(excluded.size, board_fields.size), updated_at = now()
                """, new { issueId, tenantId = user.Identity.TenantId, status = issue.Status, priority = issue.Priority, size = issue.Size });
            if (issue.Status is not null)
                await events.RecordAsync(s, new IssueEvent(issueId, IssueEventKind.StatusSet, null, issue.Status));
            if (issue.Priority is not null)
                await events.RecordAsync(s, new IssueEvent(issueId, IssueEventKind.PrioritySet, null, issue.Priority));
            if (issue.Size is not null)
                await events.RecordAsync(s, new IssueEvent(issueId, IssueEventKind.SizeSet, null, issue.Size));
        }

        await audit.WriteAsync(s, "issue.create", "issue", issueId, null,
            new { repository = $"{issue.Target.Repo.Owner}/{issue.Target.Repo.Name}", number = created.Number });
        return (await IssueDetailQueries.GetAsync(s, issueId))!;
    }

    // Stored names of the repository's mirrored labels (case-insensitive match), and the requested names that match none.
    public Task<(List<string> Stored, List<string> Unknown)> ResolveLabelsAsync(
        Guid repositoryId, IReadOnlyList<string> requested, CancellationToken ct) =>
        db.ReadAsync(async s =>
        {
            if (requested.Count == 0)
                return (new List<string>(), new List<string>());
            var rows = await s.QueryAsync<LabelName>(
                "select name from labels where repository_id = @repositoryId and lower(name) = any(@lower)",
                new { repositoryId, lower = requested.Select(l => l.ToLowerInvariant()).ToArray() });
            var byLower = rows.GroupBy(r => r.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Name);
            var stored = new List<string>();
            var unknown = new List<string>();
            foreach (var l in requested)
                if (byLower.TryGetValue(l.ToLowerInvariant(), out var name))
                    stored.Add(name);
                else
                    unknown.Add(l);
            return (stored, unknown);
        }, ct);

    // Null when any id is not a member of the caller's tenant.
    public Task<List<(long UserId, string Login)>?> ResolveAssigneesAsync(long[] ids, CancellationToken ct) =>
        TenantAssignees.ResolveAsync(db, ids, ct);
}
