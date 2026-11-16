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

// A request that passed validation: assignees are already resolved to tenant members.
public sealed record NewIssue(
    GitHubWriteTarget Target, string Title, string? Body, IReadOnlyList<string> Labels,
    IReadOnlyList<(long UserId, string Login)> Assignees, string? Status, string? Priority, string? Size);

public sealed class IssueCreation(
    ICurrentUser user, TenantDb db, IGitHubWriter writer, MirrorWriter mirror, IAuditLog audit, IIssueEvents events)
{
    public const string RepositoryArchived = "repository_archived";

    private sealed record AssigneeRow(long GithubUserId, string GithubLogin);

    // Order matters: the GitHub call comes first, then the mirror, then board fields and the audit row. Nothing is
    // written locally when GitHub refuses.
    public Task<IdempotentResponse> CreateAsync(
        NewIssue issue, string key, string requestHash, ILogger log, CancellationToken ct) =>
        Idempotency.RunExternalAsync(db, user, key, requestHash, async callCt =>
        {
            SourceIssue created;
            try
            {
                created = await writer.CreateIssueAsync(issue.Target.Repo, user.Identity.Member.GithubLogin, issue.Title,
                    issue.Body, issue.Labels, issue.Assignees.Select(a => a.Login).ToList(), callCt);
            }
            catch (GitHubWriteException e) when (!e.NoResponse)
            {
                return GitHubWriteErrors.ToOutcome(e);
            }

            // GitHub has the issue from here on; finishing locally must not be cut short by the caller leaving.
            var mirrored = await mirror.ApplyWrittenIssueAsync(
                user.Identity.TenantId, issue.Target.RepositoryId, created, null, null, CancellationToken.None);
            if (mirrored is null)
                return new ExternalOutcome(new WriteResult(StatusCodes.Status404NotFound, new ErrorBody(ApiErrors.NotFound)), true);

            var dropped = issue.Assignees
                .Where(a => !created.Assignees.Any(c => string.Equals(c.Login, a.Login, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.UserId).ToList();
            var detail = await db.WriteAsync(s => FinishAsync(s, mirrored.IssueId, issue, created), CancellationToken.None);
            return new ExternalOutcome(new WriteResult(StatusCodes.Status201Created, new CreatedIssue(detail, dropped)), true);
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

    // Null when any id is not a member of the caller's tenant.
    public Task<List<(long UserId, string Login)>?> ResolveAssigneesAsync(long[] ids, CancellationToken ct) =>
        db.ReadAsync<List<(long, string)>?>(async s =>
        {
            if (ids.Length == 0)
                return [];
            var rows = await s.QueryAsync<AssigneeRow>(
                "select github_user_id, github_login from members where github_user_id = any(@ids)", new { ids });
            return rows.Count == ids.Length ? rows.Select(r => (r.GithubUserId, r.GithubLogin)).ToList() : null;
        }, ct);
}
