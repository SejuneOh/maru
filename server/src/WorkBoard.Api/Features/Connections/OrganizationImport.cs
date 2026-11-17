using Dapper;
using Npgsql;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Connections;

public sealed record ImportOrganizationRequest(IReadOnlyList<long>? Repositories, IReadOnlyList<string>? TeamProjects);

public sealed record UpdateRepositorySelectionRequest(IReadOnlyList<long>? Repositories);

public sealed record OrganizationSummary(long Id, string Login, string? Name, string? Description, string? HtmlUrl, string? AvatarUrl);

public sealed record PreviewRepository(
    long Id, string Name, string FullName, string? HtmlUrl, bool Archived, bool Private, string? Description,
    bool Selected, int? LabelCount, int? MilestoneCount);

public sealed record PreviewTeam(long Id, string Name, string Slug, string? Description, string? Privacy);

public sealed record PreviewMember(long Id, string Login, string? AvatarUrl, bool OnBoard);

public sealed record PreviewCounts(int Repositories, int Teams, int Members, int Labels, int Milestones, int CountedRepositories);

public sealed record PreviewTruncated(bool Repositories, bool Teams, bool Members, bool Counts);

public sealed record OrganizationPreview(
    OrganizationSummary Organization,
    IReadOnlyList<PreviewRepository> Repositories,
    IReadOnlyList<PreviewTeam> Teams,
    IReadOnlyList<PreviewMember> Members,
    PreviewCounts Counts,
    PreviewTruncated Truncated);

public sealed record ImportedProject(Guid Id, string Name, int RepositoryCount);

public sealed record SkippedTeam(string Slug, string Reason);

public sealed record SelectionResult(int Selected, int Deselected);

public sealed record ImportResult(
    int Selected, int Deselected, IReadOnlyList<ImportedProject> Projects, IReadOnlyList<SkippedTeam> SkippedTeams, int MembersReferenced);

// Preview and import of a token-connected organization. Only repositories chosen here are synced (repositories.sync_enabled).
// Members are written as github_users reference rows only; nobody becomes a board member. Secrets, billing, webhooks,
// security settings and organization Projects are never read.
public static class OrganizationImport
{
    // Labels and milestones are counted per repository (two requests each), for the first CountedRepositoryCap
    // non-archived repositories.
    public const int CountedRepositoryCap = 30;
    public const int MaxSelection = 1000;
    public const int MaxTeamProjects = 50;

    public const string UnknownRepository = "unknown_repository";
    public const string UnknownTeam = "unknown_team";
    public const string GithubRateLimited = "github_rate_limited";
    public const string NoSelectedRepositories = "no_selected_repositories";
    public const string NameTaken = "name_taken";

    public static void MapOrganizationImport(this RouteGroupBuilder api)
    {
        api.MapGet("/connections/{id:guid}/organization/preview", PreviewAsync);
        api.MapPost("/connections/{id:guid}/organization/import", ImportAsync);
        api.MapPut("/connections/{id:guid}/organization/repositories", UpdateAsync);
    }

    private sealed record Target(Guid Id, long AccountId, string AccountLogin, string? OwnerLogin);

    private static GitHubConnectionRef Ref(Guid tenantId, Target t) =>
        new(tenantId, t.Id, GitHubAuthKind.Token, null, t.AccountLogin, t.OwnerLogin);

    private static async Task<Target?> FindAsync(NpgsqlDataSource sync, Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Target>(new CommandDefinition("""
            select c.id, c.account_id, c.account_login, k.owner_login
              from github_connections c
              left join github_credentials k on k.tenant_id = c.tenant_id and k.connection_id = c.id
             where c.tenant_id = @tenantId and c.id = @id and c.auth_kind = 'token' and c.account_type = 'organization'
               and c.disconnected_at is null
            """, new { tenantId, id }, cancellationToken: ct));
    }

    private static IResult? GitHubFailure(Exception e) => e switch
    {
        GitHubSourceException { Kind: GitHubSourceErrorKind.RateLimited } =>
            ApiErrors.Of(StatusCodes.Status429TooManyRequests, GithubRateLimited),
        GitHubSourceException { Kind: GitHubSourceErrorKind.Unauthorized } =>
            ApiErrors.Of(StatusCodes.Status409Conflict, ApiErrors.CredentialMissing),
        GitHubSourceException { Kind: GitHubSourceErrorKind.CredentialRevoked } =>
            ApiErrors.Of(StatusCodes.Status409Conflict, ApiErrors.CredentialRevoked),
        GitHubSourceException { Kind: GitHubSourceErrorKind.CredentialExpired } =>
            ApiErrors.Of(StatusCodes.Status409Conflict, ApiErrors.CredentialExpired),
        GitHubSourceException or HttpRequestException => ApiErrors.GithubUnavailableResult(),
        TaskCanceledException => ApiErrors.GithubUnavailableResult(),
        _ => null,
    };

    private static async Task<IResult> PreviewAsync(
        Guid id, ICurrentUser user, IGitHubOrgReader github, [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync,
        CancellationToken ct)
    {
        var caller = user.Identity;
        if (caller.Member.Role != "admin")
            return ApiErrors.AdminOnlyResult();
        if (await FindAsync(sync, caller.TenantId, id, ct) is not { } target)
            return ApiErrors.NotFoundResult();
        var connection = Ref(caller.TenantId, target);

        try
        {
            var org = await github.GetOrgInfoAsync(connection, ct);
            var repos = await github.ReadOrgRepositoriesAsync(connection, ct);
            var teams = await github.ReadOrgTeamsAsync(connection, ct);
            var members = await github.ReadOrgMembersAsync(connection, ct);

            var counted = repos.Items.Where(r => !r.Archived).Take(CountedRepositoryCap).ToList();
            var labels = new Dictionary<long, int>();
            var milestones = new Dictionary<long, int>();
            foreach (var repo in counted)
            {
                labels[repo.Id] = await github.CountLabelsAsync(connection, repo.FullName, ct);
                milestones[repo.Id] = await github.CountMilestonesAsync(connection, repo.FullName, ct);
            }

            await using var conn = await sync.OpenConnectionAsync(ct);
            var selected = (await conn.QueryAsync<long>(new CommandDefinition("""
                select github_repo_id from repositories
                 where tenant_id = @tenantId and connection_id = @id and sync_enabled
                """, new { tenantId = caller.TenantId, id }, cancellationToken: ct))).ToHashSet();
            var onBoard = (await conn.QueryAsync<long>(new CommandDefinition("""
                select github_user_id from members where tenant_id = @tenantId and removed_at is null and left_at is null
                """, new { tenantId = caller.TenantId }, cancellationToken: ct))).ToHashSet();

            var preview = new OrganizationPreview(
                new OrganizationSummary(org.Id, org.Login, org.Name, org.Description, org.HtmlUrl, org.AvatarUrl),
                repos.Items.Select(r => new PreviewRepository(
                    r.Id, r.Name, r.FullName, r.HtmlUrl, r.Archived, r.Private, r.Description, selected.Contains(r.Id),
                    labels.TryGetValue(r.Id, out var l) ? l : null,
                    milestones.TryGetValue(r.Id, out var m) ? m : null)).ToList(),
                teams.Items.Select(t => new PreviewTeam(t.Id, t.Name, t.Slug, t.Description, t.Privacy)).ToList(),
                members.Items.Select(u => new PreviewMember(u.Id, u.Login, u.AvatarUrl, onBoard.Contains(u.Id))).ToList(),
                new PreviewCounts(repos.Items.Count, teams.Items.Count, members.Items.Count,
                    labels.Values.Sum(), milestones.Values.Sum(), counted.Count),
                new PreviewTruncated(repos.Truncated, teams.Truncated, members.Truncated,
                    repos.Items.Count(r => !r.Archived) > counted.Count));
            return Results.Ok(preview);
        }
        catch (Exception e) when (GitHubFailure(e) is not null)
        {
            return GitHubFailure(e)!;
        }
    }

    private static List<long>? CleanSelection(IReadOnlyList<long>? ids, bool allowEmpty)
    {
        if (ids is null || ids.Count > MaxSelection || ids.Any(i => i <= 0))
            return null;
        var distinct = ids.Distinct().ToList();
        return distinct.Count == 0 && !allowEmpty ? null : distinct;
    }

    private static async Task<IResult> ImportAsync(
        Guid id, HttpContext http, ICurrentUser user, IGitHubOrgReader github, MirrorWriter mirror,
        [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, TenantDb db, IAuditLog audit, ILoggerFactory logs,
        CancellationToken ct)
    {
        var caller = user.Identity;
        if (caller.Member.Role != "admin")
            return ApiErrors.AdminOnlyResult();
        var request = await ConnectGithubToken.ReadAsync<ImportOrganizationRequest>(http, ct);
        var ids = CleanSelection(request?.Repositories, allowEmpty: false);
        var slugs = (request?.TeamProjects ?? []).Select(s => s?.Trim() ?? "").Distinct().ToList();
        if (ids is null || slugs.Count > MaxTeamProjects || slugs.Any(s => !GitHubUserLookup.IsValidLogin(s)))
            return ApiErrors.BadRequest();
        if (await FindAsync(sync, caller.TenantId, id, ct) is not { } target)
            return ApiErrors.NotFoundResult();
        var connection = Ref(caller.TenantId, target);

        try
        {
            var repos = await github.ReadOrgRepositoriesAsync(connection, ct);
            if (Unknown(repos.Items, ids) is { } unknown)
                return unknown;

            var teams = (await github.ReadOrgTeamsAsync(connection, ct)).Items;
            var chosen = new List<(OrgTeam Team, HashSet<long> RepositoryIds)>();
            var missing = slugs.Where(s => teams.All(t => t.Slug != s)).ToList();
            if (missing.Count > 0)
                return Results.Json(new { error = UnknownTeam, slugs = missing }, statusCode: StatusCodes.Status422UnprocessableEntity);
            foreach (var slug in slugs)
            {
                var team = teams.First(t => t.Slug == slug);
                var teamRepos = await github.ReadTeamRepositoryIdsAsync(connection, slug, ct);
                chosen.Add((team, teamRepos.Items.ToHashSet()));
            }

            var members = (await github.ReadOrgMembersAsync(connection, ct)).Items;

            var selected = repos.Items.Where(r => ids.Contains(r.Id))
                .Select(r => new SourceRepository(r.Id, r.Name, r.FullName, r.HtmlUrl, r.Archived)).ToList();
            await mirror.UpsertRepositoriesAsync(caller.TenantId, id, selected, ct);

            await using var conn = await sync.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await LockAsync(conn, tx, caller.TenantId, id, ct))
                return ApiErrors.NotFoundResult();
            var result = await SelectAsync(conn, tx, caller.TenantId, id, ids, ct);
            var projects = new List<ImportedProject>();
            var skipped = new List<SkippedTeam>();
            foreach (var (team, teamIds) in chosen)
            {
                var inTeam = ids.Where(teamIds.Contains).ToArray();
                if (inTeam.Length == 0)
                {
                    skipped.Add(new SkippedTeam(team.Slug, NoSelectedRepositories));
                    continue;
                }
                if (await CreateProjectAsync(conn, tx, caller.TenantId, id, team.Name, inTeam, ct) is { } project)
                    projects.Add(project);
                else
                    skipped.Add(new SkippedTeam(team.Slug, NameTaken));
            }
            await UpsertMemberReferencesAsync(conn, tx, caller.TenantId, members, ct);
            await tx.CommitAsync(ct);

            await ConnectGithubToken.AuditAsync(db, audit, logs.CreateLogger(typeof(OrganizationImport)), "connection.org_import", id,
                new { organization = target.AccountLogin, result.Selected, result.Deselected, projects = projects.Count, members = members.Count });
            return Results.Ok(new ImportResult(result.Selected, result.Deselected, projects, skipped, members.Count));
        }
        catch (Exception e) when (GitHubFailure(e) is not null)
        {
            return GitHubFailure(e)!;
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, HttpContext http, ICurrentUser user, IGitHubOrgReader github, MirrorWriter mirror,
        [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, TenantDb db, IAuditLog audit, ILoggerFactory logs,
        CancellationToken ct)
    {
        var caller = user.Identity;
        if (caller.Member.Role != "admin")
            return ApiErrors.AdminOnlyResult();
        var request = await ConnectGithubToken.ReadAsync<UpdateRepositorySelectionRequest>(http, ct);
        var ids = CleanSelection(request?.Repositories, allowEmpty: true);
        if (ids is null)
            return ApiErrors.BadRequest();
        if (await FindAsync(sync, caller.TenantId, id, ct) is not { } target)
            return ApiErrors.NotFoundResult();

        try
        {
            var repos = await github.ReadOrgRepositoriesAsync(Ref(caller.TenantId, target), ct);
            if (Unknown(repos.Items, ids) is { } unknown)
                return unknown;

            var selected = repos.Items.Where(r => ids.Contains(r.Id))
                .Select(r => new SourceRepository(r.Id, r.Name, r.FullName, r.HtmlUrl, r.Archived)).ToList();
            await mirror.UpsertRepositoriesAsync(caller.TenantId, id, selected, ct);

            await using var conn = await sync.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await LockAsync(conn, tx, caller.TenantId, id, ct))
                return ApiErrors.NotFoundResult();
            var result = await SelectAsync(conn, tx, caller.TenantId, id, ids, ct);
            await tx.CommitAsync(ct);

            await ConnectGithubToken.AuditAsync(db, audit, logs.CreateLogger(typeof(OrganizationImport)), "connection.org_repositories_update", id,
                new { organization = target.AccountLogin, result.Selected, result.Deselected });
            return Results.Ok(result);
        }
        catch (Exception e) when (GitHubFailure(e) is not null)
        {
            return GitHubFailure(e)!;
        }
    }

    private static IResult? Unknown(IReadOnlyList<OrgRepository> repos, IReadOnlyList<long> ids)
    {
        var known = repos.Select(r => r.Id).ToHashSet();
        var unknown = ids.Where(i => !known.Contains(i)).ToList();
        return unknown.Count == 0
            ? null
            : Results.Json(new { error = UnknownRepository, ids = unknown }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    // A disconnect that ran while GitHub was being read must not be followed by a selection.
    private static async Task<bool> LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid id, CancellationToken ct) =>
        await conn.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
            select id from github_connections
             where tenant_id = @tenantId and id = @id and auth_kind = 'token' and disconnected_at is null
               for update
            """, new { tenantId, id }, tx, cancellationToken: ct)) is not null;

    // The selection is the whole set: everything else of the connection stops syncing and is hidden from the board.
    private static async Task<SelectionResult> SelectAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid id, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var (selected, deselected) = await conn.QuerySingleAsync<(int, int)>(new CommandDefinition("""
            with changed as (
              update repositories set sync_enabled = (github_repo_id = any(@ids))
               where tenant_id = @tenantId and connection_id = @id and sync_enabled is distinct from (github_repo_id = any(@ids))
              returning sync_enabled
            )
            select (select count(*) from repositories where tenant_id = @tenantId and connection_id = @id and github_repo_id = any(@ids))::int,
                   (select count(*) from changed where not sync_enabled)::int
            """, new { tenantId, id, ids = ids.ToArray() }, tx, cancellationToken: ct));
        return new SelectionResult(selected, deselected);
    }

    // Null when a project with that name exists already: it may have been made by hand, so it is left alone.
    private static async Task<ImportedProject?> CreateProjectAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid connectionId, string name, long[] githubRepoIds,
        CancellationToken ct)
    {
        var projectId = await conn.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
            insert into projects (tenant_id, name) values (@tenantId, @name)
            on conflict (tenant_id, name) do nothing returning id
            """, new { tenantId, name }, tx, cancellationToken: ct));
        if (projectId is null)
            return null;
        var count = await conn.ExecuteAsync(new CommandDefinition("""
            insert into project_repositories (tenant_id, project_id, repository_id)
            select @tenantId, @projectId, r.id from repositories r
             where r.tenant_id = @tenantId and r.connection_id = @connectionId and r.github_repo_id = any(@githubRepoIds)
            """, new { tenantId, projectId, connectionId, githubRepoIds }, tx, cancellationToken: ct));
        return new ImportedProject(projectId.Value, name, count);
    }

    private static Task UpsertMemberReferencesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, IReadOnlyList<OrgMemberInfo> members, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            insert into github_users (tenant_id, github_user_id, login, avatar_url)
            select @tenantId, u.id, u.login, u.avatar from unnest(@ids, @logins, @avatars) as u(id, login, avatar)
            on conflict (tenant_id, github_user_id) do update
               set login = excluded.login, avatar_url = excluded.avatar_url, updated_at = now()
            """, new
        {
            tenantId,
            ids = members.Select(m => m.Id).ToArray(),
            logins = members.Select(m => m.Login).ToArray(),
            avatars = members.Select(m => m.AvatarUrl).ToArray(),
        }, tx, cancellationToken: ct));
}
