using System.Text.Json;
using WorkBoard.Api.Features.Tenants;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Issues;

public sealed record NewIssueRepository(Guid Id, string Name, string FullName, bool Archived, IReadOnlyList<NewIssueLabel> Labels);

public sealed record NewIssueLabel(string Name, string? Color);

public sealed record NewIssueAssignee(long GithubUserId, string GithubLogin, string? DisplayName, string? AvatarUrl);

public sealed record NewIssueOptions(IReadOnlyList<NewIssueRepository> Repositories, IReadOnlyList<NewIssueAssignee> Assignees);

public static class NewIssueEndpoints
{
    public const int TitleMaxLength = 256;
    public const int BodyMaxLength = 60_000;
    public const int MaxLabels = 20;
    public const int LabelMaxLength = 50;
    public const int MaxAssignees = 10;
    private const int MaxBodyBytes = 256 * 1024;

    private static readonly HashSet<string> Statuses = ["todo", "in_progress", "in_review", "done"];
    private static readonly HashSet<string> Priorities = ["urgent", "high", "normal", "low"];
    private static readonly HashSet<string> Sizes = ["S", "M", "L"];

    private sealed record RepoRow(Guid Id, string Name, string FullName, bool Archived);

    private sealed record LabelRow(Guid RepositoryId, string Name, string? Color);

    public static void MapNewIssue(this RouteGroupBuilder api)
    {
        api.MapGet("/issues/new-options", async (TenantDb db, CancellationToken ct) =>
            Results.Ok(await db.ReadAsync(OptionsAsync, ct)));

        api.MapPost("/issues", CreateAsync);
    }

    private static async Task<NewIssueOptions> OptionsAsync(TenantSession s)
    {
        var repos = await s.QueryAsync<RepoRow>("""
            select r.id, r.name, r.full_name, r.archived
              from repositories r
              join github_connections c on c.tenant_id = r.tenant_id and c.id = r.connection_id
             where c.disconnected_at is null and c.installation_id is not null
             order by r.archived, r.name
            """);
        var labels = (await s.QueryAsync<LabelRow>("""
            select l.repository_id, l.name, l.color
              from labels l
              join repositories r on r.id = l.repository_id
              join github_connections c on c.tenant_id = r.tenant_id and c.id = r.connection_id
             where c.disconnected_at is null and c.installation_id is not null and not r.archived
             order by l.name
            """)).ToLookup(l => l.RepositoryId);
        var members = await s.QueryAsync<NewIssueAssignee>("""
            select github_user_id, github_login, display_name, avatar_url from members order by github_login
            """);
        return new NewIssueOptions(
            repos.Select(r => new NewIssueRepository(r.Id, r.Name, r.FullName, r.Archived,
                labels[r.Id].Select(l => new NewIssueLabel(l.Name, l.Color)).ToList())).ToList(),
            members);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http, ICurrentUser user, IssueCreation creation, IGitHubWriteTargets targets,
        ILogger<IssueCreation> log, CancellationToken ct)
    {
        var key = http.Request.Headers[Idempotency.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key))
            return ApiErrors.BadRequest();
        var bytes = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        if (bytes is null || !http.Request.HasJsonContentType())
            return ApiErrors.BadRequest();
        CreateIssueRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<CreateIssueRequest>(bytes, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return ApiErrors.BadRequest();
        }

        if (Validate(request) is not { } valid)
            return ApiErrors.BadRequest();
        var (title, labels, assigneeIds) = valid;

        var target = await targets.ResolveAsync(request!.RepositoryId!.Value, ct);
        if (target is null)
            return ApiErrors.NotFoundResult();
        if (target.Archived)
            return ApiErrors.ConflictResult(IssueCreation.RepositoryArchived);

        var assignees = await creation.ResolveAssigneesAsync(assigneeIds, ct);
        if (assignees is null)
            return ApiErrors.BadRequest();

        var hash = Idempotency.HashRequest(
            user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);
        var body = string.IsNullOrWhiteSpace(request.Body) ? null : request.Body;
        return await creation.CreateAsync(
            new NewIssue(target, title, body, labels, assignees, request.Status, request.Priority, request.Size),
            key, hash, log, ct);
    }

    private static (string Title, string[] Labels, long[] AssigneeIds)? Validate(CreateIssueRequest? r)
    {
        if (r is not { RepositoryId: { } repositoryId } || repositoryId == Guid.Empty)
            return null;
        var title = r.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > TitleMaxLength || !TenantsEndpoints.IsPrintable(title))
            return null;
        if (r.Body is { Length: > BodyMaxLength })
            return null;
        if (r.Status is not null && !Statuses.Contains(r.Status)
            || r.Priority is not null && !Priorities.Contains(r.Priority)
            || r.Size is not null && !Sizes.Contains(r.Size))
            return null;

        var labels = (r.Labels ?? []).Select(l => l?.Trim() ?? "").ToArray();
        if (labels.Any(l => l.Length == 0 || l.Length > LabelMaxLength || l.Contains(',')))
            return null;
        labels = labels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var assigneeIds = (r.AssigneeGithubUserIds ?? []).Distinct().Order().ToArray();
        return labels.Length > MaxLabels || assigneeIds.Length > MaxAssignees ? null : (title, labels, assigneeIds);
    }
}
