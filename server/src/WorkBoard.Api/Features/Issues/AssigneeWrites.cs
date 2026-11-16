using System.Text.Json;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Issues;

public sealed record SetAssigneesRequest(long[]? AssigneeGithubUserIds);

// The issue after the change plus the requested assignees GitHub did not apply (by GitHub user id).
public sealed record AssignedIssue : IssueDetail
{
    public AssignedIssue(IssueDetail issue, IReadOnlyList<long> droppedAssignees) : base(issue) =>
        DroppedAssignees = droppedAssignees;

    public IReadOnlyList<long> DroppedAssignees { get; init; }
}

// Shared by issue creation and assignee changes: candidates are the caller's tenant members only.
public static class TenantAssignees
{
    private sealed record Row(long GithubUserId, string GithubLogin);

    // Null when any id is not a member of the caller's tenant.
    public static Task<List<(long UserId, string Login)>?> ResolveAsync(TenantDb db, long[] ids, CancellationToken ct) =>
        db.ReadAsync<List<(long, string)>?>(async s =>
        {
            if (ids.Length == 0)
                return [];
            var rows = await s.QueryAsync<Row>(
                "select github_user_id, github_login from members where github_user_id = any(@ids)", new { ids });
            return rows.Count == ids.Length ? rows.Select(r => (r.GithubUserId, r.GithubLogin)).ToList() : null;
        }, ct);

    // Null when the body is not an object with an assigneeGithubUserIds array of at most max ids.
    public static long[]? ParseIds(ReadOnlySpan<byte> body, int max)
    {
        try
        {
            var r = JsonSerializer.Deserialize<SetAssigneesRequest>(body, JsonSerializerOptions.Web);
            if (r?.AssigneeGithubUserIds is not { } ids)
                return null;
            var distinct = ids.Distinct().Order().ToArray();
            return distinct.Length > max || distinct.Any(id => id <= 0) ? null : distinct;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// Replaces the assignee list of one issue. GitHub is written first; the mirror and the audit row follow only after
// it succeeds, so a GitHub refusal changes nothing locally. Last write wins: there is no concurrency check.
public sealed class AssigneeWrites(
    ICurrentUser user, TenantDb db, IAuditLog audit, IGitHubWriteTargets targets, IGitHubWriter github, MirrorWriter mirror,
    ILogger<AssigneeWrites> log)
{
    private sealed record Current(Guid RepositoryId, int Number);

    private sealed record Login(string GithubLogin);

    public Task<IdempotentResponse> SetAsync(
        Guid issueId, IReadOnlyList<(long UserId, string Login)> assignees, string? key, string hash, CancellationToken ct) =>
        Idempotency.RunExternalAsync(db, user, key, hash, c => RunAsync(issueId, assignees, c), ct, log);

    private static ExternalOutcome Fail(int status, string code) => new(new WriteResult(status, new ErrorBody(code)), false);

    private async Task<ExternalOutcome> RunAsync(Guid issueId, IReadOnlyList<(long UserId, string Login)> assignees, CancellationToken ct)
    {
        var (current, before) = await db.ReadAsync(async s => (
            await s.QuerySingleOrDefaultAsync<Current>("select repository_id, number from issues where id = @issueId", new { issueId }),
            await s.QueryAsync<Login>("""
                select coalesce(m.github_login, gu.login, a.github_user_id::text) as github_login
                  from issue_assignees a
                  left join members m on m.github_user_id = a.github_user_id
                  left join github_users gu on gu.github_user_id = a.github_user_id
                 where a.issue_id = @issueId
                 order by 1
                """, new { issueId })), ct);
        if (current is null)
            return Fail(StatusCodes.Status404NotFound, ApiErrors.NotFound);
        var target = await targets.ResolveAsync(current.RepositoryId, ct);
        if (target is null)
            return Fail(StatusCodes.Status404NotFound, ApiErrors.NotFound);
        if (target.Archived)
            return Fail(StatusCodes.Status409Conflict, ApiErrors.RepositoryArchived);

        SourceIssue written;
        try
        {
            written = await github.SetAssigneesAsync(target.Repo, current.Number, assignees.Select(a => a.Login).ToList(), ct);
        }
        catch (GitHubWriteException e) when (!e.NoResponse)
        {
            return GitHubWriteErrors.ToOutcome(e);
        }

        if (await mirror.ApplyWrittenIssueAsync(user.Identity.TenantId, current.RepositoryId, written, user.Identity.Member.Id, null,
                CancellationToken.None) is null)
            log.LogWarning("Issue {IssueId} was written on GitHub but its repository is not in the mirror", issueId);

        var dropped = assignees
            .Where(a => !written.Assignees.Any(w => string.Equals(w.Login, a.Login, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.UserId).ToList();
        return await db.WriteAsync(async s =>
        {
            await audit.WriteAsync(s, "issue.assignees_update", "issue", issueId,
                new { assignees = before.Select(b => b.GithubLogin).ToList() },
                new
                {
                    assignees = written.Assignees.Select(a => a.Login).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                    requested = assignees.Select(a => a.Login).ToList(),
                });
            var detail = (await IssueDetailQueries.GetAsync(s, issueId))!;
            return new ExternalOutcome(new WriteResult(StatusCodes.Status200OK, new AssignedIssue(detail, dropped)), true);
        }, CancellationToken.None);
    }
}
