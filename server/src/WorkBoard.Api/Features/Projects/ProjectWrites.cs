using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Projects;

public sealed record CreateProjectRequest(string? Name, IReadOnlyList<Guid>? RepositoryIds);

public sealed record UpdateProjectRequest(string? Name, IReadOnlyList<Guid>? RepositoryIds);

// Manual projects live in the same tables as the ones organization import creates (teams -> projects); they
// coexist and are told apart only by who made them. Admins only. Each method runs on the caller's tenant
// transaction inside Idempotency.RunAsync, so the project row, its repositories and the audit entry commit together.
public sealed class ProjectWrites(ICurrentUser user, IAuditLog audit)
{
    public const string NameTaken = "name_taken";
    public const string InvalidRepository = "invalid_repository";
    public const int MaxRepositories = 500;

    private bool IsAdmin => user.Identity.Member.Role == "admin";
    private Guid TenantId => user.Identity.TenantId;

    private static WriteResult Error(int status, string code) => new(status, new ErrorBody(code));
    private static WriteResult Forbidden() => Error(StatusCodes.Status403Forbidden, ApiErrors.AdminOnly);
    private static WriteResult NotFound() => Error(StatusCodes.Status404NotFound, ApiErrors.NotFound);

    // Only this tenant's synced repositories can be grouped (same set the picker and the project view show), or
    // the request is rejected. repositories is tenant-scoped by RLS, so a foreign id drops the count.
    private static async Task<bool> ReposBelongAsync(TenantSession s, Guid[] ids) =>
        ids.Length == 0 ||
        await s.QuerySingleOrDefaultAsync<int>("select count(*)::int from repositories where id = any(@ids) and sync_enabled", new { ids }) == ids.Length;

    // One name change at a time per tenant, so two requests do not both pass the uniqueness check and then collide.
    private Task LockNamesAsync(TenantSession s) =>
        s.ExecuteAsync("select pg_advisory_xact_lock(hashtextextended(@scope, 0))", new { scope = $"project-names:{TenantId:N}" });

    private async Task SetReposAsync(TenantSession s, Guid projectId, Guid[] ids)
    {
        await s.ExecuteAsync("delete from project_repositories where project_id = @projectId", new { projectId });
        if (ids.Length > 0)
            await s.ExecuteAsync("""
                insert into project_repositories (tenant_id, project_id, repository_id)
                select @tid, @projectId, id from repositories where id = any(@ids) and sync_enabled
                """, new { tid = TenantId, projectId, ids });
    }

    public async Task<WriteResult> CreateAsync(TenantSession s, string name, Guid[] repoIds)
    {
        if (!IsAdmin) return Forbidden();
        if (!await ReposBelongAsync(s, repoIds)) return Error(StatusCodes.Status400BadRequest, InvalidRepository);
        await LockNamesAsync(s);
        var id = await s.QuerySingleOrDefaultAsync<Guid?>(
            "insert into projects (tenant_id, name) values (@tid, @name) on conflict (tenant_id, name) do nothing returning id",
            new { tid = TenantId, name });
        if (id is null) return Error(StatusCodes.Status409Conflict, NameTaken);
        await SetReposAsync(s, id.Value, repoIds);
        await audit.WriteAsync(s, "project.create", "project", id.Value, null, new { name, repositories = repoIds.Length });
        return new WriteResult(StatusCodes.Status201Created, await ProjectsEndpoints.GetAsync(s, id.Value));
    }

    public async Task<WriteResult> UpdateAsync(TenantSession s, Guid id, string? name, Guid[]? repoIds)
    {
        if (!IsAdmin) return Forbidden();
        var exists = await s.QuerySingleOrDefaultAsync<Guid?>("select id from projects where id = @id", new { id });
        if (exists is null) return NotFound();
        if (repoIds is not null && !await ReposBelongAsync(s, repoIds)) return Error(StatusCodes.Status400BadRequest, InvalidRepository);
        if (name is not null)
        {
            await LockNamesAsync(s);
            var renamed = await s.ExecuteAsync(
                "update projects set name = @name where id = @id and not exists (select 1 from projects where tenant_id = @tid and name = @name and id <> @id)",
                new { id, name, tid = TenantId });
            if (renamed == 0) return Error(StatusCodes.Status409Conflict, NameTaken);
        }
        if (repoIds is not null) await SetReposAsync(s, id, repoIds);
        await audit.WriteAsync(s, "project.update", "project", id, null, new { name, repositories = repoIds?.Length });
        return new WriteResult(StatusCodes.Status200OK, await ProjectsEndpoints.GetAsync(s, id));
    }

    public async Task<WriteResult> DeleteAsync(TenantSession s, Guid id)
    {
        if (!IsAdmin) return Forbidden();
        var deleted = await s.ExecuteAsync("delete from projects where id = @id", new { id });
        if (deleted == 0) return NotFound();
        await audit.WriteAsync(s, "project.delete", "project", id, null, null);
        return new WriteResult(StatusCodes.Status204NoContent);
    }
}
