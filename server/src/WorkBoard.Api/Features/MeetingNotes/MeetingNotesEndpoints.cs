using System.Text.Json;
using WorkBoard.Api.Features.Members;
using WorkBoard.Api.Features.Tenants;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.MeetingNotes;

public static class MeetingNotesEndpoints
{
    public const int TitleMaxLength = 200;
    public const int BodyMaxLength = 60000;
    public const int MaxDecisions = 50;
    public const int MaxAttendees = 100;
    public const int MaxActionItems = 100;
    public const int ItemTextMaxLength = 500;
    internal const int MaxBodyBytes = 512 * 1024;

    public static void MapMeetingNotes(this RouteGroupBuilder api)
    {
        api.MapGet("/meeting-notes", async (Guid? projectId, TenantDb db, CancellationToken ct) =>
        {
            var rows = await db.ReadAsync(s => s.QueryAsync<SummaryRow>("""
                select n.id, n.title, n.held_at, n.project_id, p.name as project_name,
                       (select count(*) from meeting_action_items a where a.note_id = n.id)::int as action_item_count,
                       (select count(*) from meeting_action_items a
                         where a.note_id = n.id and a.issue_id is not null)::int as linked_action_item_count
                  from meeting_notes n
                  left join projects p on p.id = n.project_id
                 where @projectId::uuid is null or n.project_id = @projectId::uuid
                 order by n.held_at desc, n.id
                """, new { projectId }), ct);

            return rows
                .Select(r => new MeetingNoteSummary(
                    r.Id, r.Title, r.HeldAt, ToProject(r.ProjectId, r.ProjectName),
                    r.ActionItemCount, r.LinkedActionItemCount))
                .ToList();
        });

        api.MapGet("/meeting-notes/{id:guid}", async (Guid id, TenantDb db, CancellationToken ct) =>
        {
            var note = await db.ReadAsync(s => GetAsync(s, id), ct);
            return note is null ? Results.NotFound() : Results.Ok(note);
        });

        api.MapPost("/meeting-notes", (HttpContext http, ICurrentUser user, TenantDb db, MeetingNoteWrites writes, CancellationToken ct) =>
            RunAsync(http, user, db, ct, body: true, r =>
                r.Id != Guid.Empty && Validate(r, requireAll: false) is { } values && values.ActionItems.All(i => i.Id is null)
                    ? s => writes.CreateAsync(s, values, r.Id)
                    : null));

        api.MapPatch("/meeting-notes/{id:guid}", (Guid id, HttpContext http, ICurrentUser user, TenantDb db, MeetingNoteWrites writes, CancellationToken ct) =>
            RunAsync(http, user, db, ct, body: true, r =>
                r.ExpectedUpdatedAt is { } expected && Validate(r, requireAll: true) is { } values
                    ? s => writes.UpdateAsync(s, id, values, expected)
                    : null));

        api.MapDelete("/meeting-notes/{id:guid}", (Guid id, HttpContext http, ICurrentUser user, TenantDb db, MeetingNoteWrites writes, CancellationToken ct) =>
            RunAsync(http, user, db, ct, body: false, _ => s => writes.DeleteAsync(s, id)));
    }

    // Null when the body breaks a rule in docs/api-contract.md. Membership of attendees, assignees and the
    // project is checked by the writer, inside the transaction.
    internal static MeetingNoteValues? Validate(MeetingNoteInput r, bool requireAll)
    {
        var title = r.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > TitleMaxLength || !TenantsEndpoints.IsPrintable(title))
            return null;
        if (r.HeldAt is not { } heldAt)
            return null;
        if (requireAll && (r.BodyMd is null || r.Decisions is null || r.AttendeeIds is null || r.ActionItems is null))
            return null;

        var body = r.BodyMd ?? "";
        if (body.Length > BodyMaxLength)
            return null;

        var decisions = new List<string>();
        foreach (var d in r.Decisions ?? [])
        {
            var text = d?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;
            if (text.Length > ItemTextMaxLength || !TenantsEndpoints.IsPrintable(text))
                return null;
            decisions.Add(text);
        }
        if (decisions.Count > MaxDecisions)
            return null;

        var attendees = (r.AttendeeIds ?? []).Distinct().ToArray();
        if (attendees.Length > MaxAttendees)
            return null;

        var items = new List<ActionItemValues>();
        var ids = new HashSet<Guid>();
        foreach (var item in r.ActionItems ?? [])
        {
            if (item is null)
                return null;
            var text = item.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > ItemTextMaxLength || !TenantsEndpoints.IsPrintable(text))
                return null;
            if (item.Id is { } itemId && !ids.Add(itemId))
                return null;
            items.Add(new ActionItemValues(item.Id, text, item.AssigneeId, item.DueDate));
        }
        if (items.Count > MaxActionItems)
            return null;

        return new MeetingNoteValues(title, heldAt, r.ProjectId, body, decisions.ToArray(), attendees, items.ToArray());
    }

    private static async Task<IResult> RunAsync(
        HttpContext http, ICurrentUser user, TenantDb db, CancellationToken ct, bool body,
        Func<MeetingNoteInput, Func<TenantSession, Task<WriteResult>>?> plan)
    {
        var bytes = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        if (bytes is null)
            return ApiErrors.BadRequest();
        MeetingNoteInput? request = null;
        if (body)
        {
            if (!http.Request.HasJsonContentType())
                return ApiErrors.BadRequest();
            try
            {
                request = JsonSerializer.Deserialize<MeetingNoteInput>(bytes, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                return ApiErrors.BadRequest();
            }
            if (request is null)
                return ApiErrors.BadRequest();
        }

        var work = plan(request!);
        if (work is null)
            return ApiErrors.BadRequest();

        var key = http.Request.Headers[Idempotency.HeaderName].ToString();
        var hash = Idempotency.HashRequest(
            user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);
        return await db.WriteAsync(s => Idempotency.RunAsync(s, user, key, hash, () => work(s)), ct);
    }

    internal static async Task<MeetingNote?> GetAsync(TenantSession s, Guid id)
    {
        var note = await s.QuerySingleOrDefaultAsync<NoteRow>("""
            select n.id, n.title, n.held_at, n.body_md, n.project_id, p.name as project_name, n.decisions,
                   n.created_at, n.updated_at,
                   m.id as member_id, m.github_user_id, m.github_login, m.display_name, m.avatar_url, m.role
              from meeting_notes n
              left join projects p on p.id = n.project_id
              left join members m on m.id = n.created_by
             where n.id = @id
            """, new { id });
        if (note is null)
            return null;

        var attendees = await s.QueryAsync<MemberRow>($"""
            select {MemberRow.Columns}
              from meeting_attendees ma
              join members m on m.id = ma.member_id
             where ma.note_id = @id
             order by m.github_login
            """, new { id });

        var items = await ActionItemsAsync(s, "a.note_id = @id", new { id });

        return new MeetingNote(
            note.Id,
            note.Title,
            note.HeldAt,
            note.BodyMd,
            ToProject(note.ProjectId, note.ProjectName),
            note.Decisions,
            attendees.Select(a => a.ToMember()).ToList(),
            items,
            ToMember(note.MemberId, note.GithubUserId, note.GithubLogin, note.DisplayName, note.AvatarUrl, note.Role),
            note.CreatedAt,
            note.UpdatedAt);
    }

    internal static async Task<ActionItemDto?> GetActionItemAsync(TenantSession s, Guid id) =>
        (await ActionItemsAsync(s, "a.id = @id", new { id })).SingleOrDefault();

    // An item whose issue GitHub created but the mirror lacked shows it as pending until the mirror stores it (PendingLink).
    private static async Task<List<ActionItemDto>> ActionItemsAsync(TenantSession s, string where, object args)
    {
        var rows = await s.QueryAsync<ActionItemRow>($"""
            select a.id, a.text, a.due_date::text as due_date,
                   m.id as member_id, m.github_user_id, m.github_login, m.display_name, m.avatar_url, m.role,
                   i.id as issue_id, i.number as issue_number, r.name as repository_name, st.status as issue_status,
                   i.state as issue_state, i.html_url as issue_html_url,
                   cr.name as claim_repository_name, a.issue_claim_number as claim_number,
                   ({ActionItemIssues.ActiveClaim}) as creating_issue
              from meeting_action_items a
              left join members m on m.id = a.assignee_member_id
              left join repositories cr on cr.id = a.issue_claim_repository_id and a.issue_claim_number is not null
              left join issues i on i.id = a.issue_id
              left join repositories r on r.id = i.repository_id
              left join issue_status st on st.issue_id = i.id
             where {where}
             order by a.position, a.created_at, a.id
            """, args);
        return rows.Select(a => new ActionItemDto(
            a.Id,
            a.Text,
            ToMember(a.MemberId, a.GithubUserId, a.GithubLogin, a.DisplayName, a.AvatarUrl, a.Role),
            a.DueDate is { } due ? DateOnly.Parse(due) : null,
            a.IssueId is { } issueId
                ? new ActionItemIssue(issueId, a.IssueNumber ?? 0, a.RepositoryName ?? "", a.IssueStatus ?? (a.IssueState == "closed" ? "done" : "todo"),
                    a.IssueState ?? "open", a.IssueHtmlUrl)
                : null,
            a.IssueId is null && a.ClaimNumber is { } number
                ? new ActionItemPendingIssue(a.ClaimRepositoryName ?? "", number)
                : null,
            a.IssueId is null && a.CreatingIssue)).ToList();
    }

    private static Member? ToMember(Guid? id, long? userId, string? login, string? displayName, string? avatarUrl, string? role) =>
        id is { } memberId ? new Member(memberId, userId ?? 0, login ?? "", displayName, avatarUrl, role ?? "member") : null;

    private static ProjectRef? ToProject(Guid? id, string? name) => id is { } projectId ? new ProjectRef(projectId, name ?? "") : null;

    private sealed class SummaryRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public DateTime HeldAt { get; set; }
        public Guid? ProjectId { get; set; }
        public string? ProjectName { get; set; }
        public int ActionItemCount { get; set; }
        public int LinkedActionItemCount { get; set; }
    }

    private sealed class NoteRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public DateTime HeldAt { get; set; }
        public string BodyMd { get; set; } = "";
        public Guid? ProjectId { get; set; }
        public string? ProjectName { get; set; }
        public string[] Decisions { get; set; } = [];
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public Guid? MemberId { get; set; }
        public long? GithubUserId { get; set; }
        public string? GithubLogin { get; set; }
        public string? DisplayName { get; set; }
        public string? AvatarUrl { get; set; }
        public string? Role { get; set; }
    }

    private sealed class ActionItemRow
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = "";
        public string? DueDate { get; set; }
        public Guid? MemberId { get; set; }
        public long? GithubUserId { get; set; }
        public string? GithubLogin { get; set; }
        public string? DisplayName { get; set; }
        public string? AvatarUrl { get; set; }
        public string? Role { get; set; }
        public Guid? IssueId { get; set; }
        public int? IssueNumber { get; set; }
        public string? RepositoryName { get; set; }
        public string? IssueStatus { get; set; }
        public string? IssueState { get; set; }
        public string? IssueHtmlUrl { get; set; }
        public string? ClaimRepositoryName { get; set; }
        public int? ClaimNumber { get; set; }
        public bool CreatingIssue { get; set; }
    }
}
