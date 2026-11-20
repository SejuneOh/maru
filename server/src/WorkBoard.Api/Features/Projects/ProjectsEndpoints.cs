using System.Text.Json;
using WorkBoard.Api.Features.Tenants;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Projects;

public sealed record RepositoryRef(Guid Id, string Name);

public sealed record Project(Guid Id, string Name, string? Color, IReadOnlyList<RepositoryRef> Repositories);

public static class ProjectsEndpoints
{
    public static void MapProjects(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/done-by-week", async (TenantDb db, CancellationToken ct) =>
            Results.Ok(await db.ReadAsync(DoneByWeekQuery.BuildAsync, ct)));
        api.MapGet("/projects/{id:guid}/schedule", async (Guid id, TenantDb db, CancellationToken ct) =>
            await db.ReadAsync(s => ScheduleQuery.BuildAsync(s, id), ct) is { } schedule
                ? Results.Ok(schedule)
                : ApiErrors.NotFoundResult());
        api.MapGet("/projects", async (TenantDb db, CancellationToken ct) =>
        {
            var (projects, repos) = await db.ReadAsync(async s => (
                await s.QueryAsync<ProjectRow>("select id, name, color from projects order by name"),
                await s.QueryAsync<ProjectRepoRow>("""
                    select pr.project_id, r.id as repository_id, r.name
                      from project_repositories pr
                      join repositories r on r.id = pr.repository_id and r.sync_enabled
                     order by r.name
                    """)), ct);

            var byProject = repos.ToLookup(r => r.ProjectId, r => new RepositoryRef(r.RepositoryId, r.Name));
            return projects.Select(p => new Project(p.Id, p.Name, p.Color, byProject[p.Id].ToList())).ToList();
        });

        // Manual projects: an admin names one and picks the repositories to group. See ProjectWrites.
        api.MapPost("/projects", (HttpContext http, ICurrentUser user, TenantDb db, ProjectWrites writes, CancellationToken ct) =>
            RunAsync<CreateProjectRequest>(http, user, db, ct,
                r => ValidName(r.Name) is { } name && RepoIds(r.RepositoryIds) is { } repos
                    ? Do(s => writes.CreateAsync(s, name, repos))
                    : Invalid(),
                body: true));

        api.MapPatch("/projects/{id:guid}", (Guid id, HttpContext http, ICurrentUser user, TenantDb db, ProjectWrites writes, CancellationToken ct) =>
            RunAsync<UpdateProjectRequest>(http, user, db, ct, r =>
            {
                var name = r.Name is null ? null : ValidName(r.Name);
                if (r.Name is not null && name is null) return Invalid();
                if (r.Name is null && r.RepositoryIds is null) return Invalid();
                if (r.RepositoryIds is not null && RepoIds(r.RepositoryIds) is null) return Invalid();
                return Do(s => writes.UpdateAsync(s, id, name, r.RepositoryIds is null ? null : RepoIds(r.RepositoryIds)));
            }, body: true));

        api.MapDelete("/projects/{id:guid}", (Guid id, HttpContext http, ICurrentUser user, TenantDb db, ProjectWrites writes, CancellationToken ct) =>
            RunAsync<object>(http, user, db, ct, _ => Do(s => writes.DeleteAsync(s, id)), body: false));
    }

    internal const int NameMaxLength = 80;
    private const int MaxBodyBytes = 64 * 1024;

    public static async Task<Project> GetAsync(TenantSession s, Guid id)
    {
        var row = (await s.QuerySingleOrDefaultAsync<ProjectRow>("select id, name, color from projects where id = @id", new { id }))!;
        var repos = await s.QueryAsync<ProjectRepoRow>("""
            select pr.project_id, r.id as repository_id, r.name
              from project_repositories pr
              join repositories r on r.id = pr.repository_id and r.sync_enabled
             where pr.project_id = @id
             order by r.name
            """, new { id });
        return new Project(row.Id, row.Name, row.Color, repos.Select(r => new RepositoryRef(r.RepositoryId, r.Name)).ToList());
    }

    private static string? ValidName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > NameMaxLength || !TenantsEndpoints.IsPrintable(trimmed)
            ? null
            : trimmed;
    }

    private static Guid[]? RepoIds(IReadOnlyList<Guid>? ids)
    {
        var a = (ids ?? []).Distinct().ToArray();
        return a.Length > ProjectWrites.MaxRepositories ? null : a;
    }

    private readonly record struct Plan(Func<TenantSession, Task<WriteResult>>? Work, IResult? Error);

    private static Plan Do(Func<TenantSession, Task<WriteResult>> work) => new(work, null);

    private static Plan Invalid(string code = ApiErrors.InvalidRequest) => new(null, ApiErrors.BadRequest(code));

    private static async Task<IResult> RunAsync<T>(
        HttpContext http, ICurrentUser user, TenantDb db, CancellationToken ct, Func<T, Plan> plan, bool body)
    {
        var bytes = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        T? request = default;
        if (body)
        {
            if (bytes is null || !http.Request.HasJsonContentType())
                return ApiErrors.BadRequest();
            try
            {
                request = JsonSerializer.Deserialize<T>(bytes, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                return ApiErrors.BadRequest();
            }
            if (request is null)
                return ApiErrors.BadRequest();
        }

        var chosen = plan(request!);
        if (chosen.Error is not null)
            return chosen.Error;

        var key = http.Request.Headers[Idempotency.HeaderName].ToString();
        var hash = Idempotency.HashRequest(
            user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes ?? []);
        return await db.WriteAsync(s => Idempotency.RunAsync(s, user, key, hash, () => chosen.Work!(s)), ct);
    }

    private sealed class ProjectRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Color { get; set; }
    }

    private sealed class ProjectRepoRow
    {
        public Guid ProjectId { get; set; }
        public Guid RepositoryId { get; set; }
        public string Name { get; set; } = "";
    }
}
