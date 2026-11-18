using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;

namespace WorkBoard.Api.Features.Connections;

public sealed record ConnectGithubRequest(string? Code, long? InstallationId);

public static class ConnectGithub
{
    public static void MapConnectGithub(this RouteGroupBuilder api) =>
        api.MapPost("/connections/github", HandleAsync);

    private static async Task<IResult> HandleAsync(
        HttpContext http, ICurrentUser user, IOptions<GitHubAppOptions> options, IGitHubAppClient github,
        [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, ILoggerFactory logs, CancellationToken ct)
    {
        var caller = user.Identity;
        if (caller.Member.Role != "admin")
            return Error(StatusCodes.Status403Forbidden, "admin_only");
        if (!options.Value.Configured)
            return Error(StatusCodes.Status503ServiceUnavailable, "github_app_not_configured");

        var request = await ReadAsync(http, ct);
        if (request is not { Code: { Length: > 0 } code, InstallationId: > 0 and var installationId })
            return Error(StatusCodes.Status400BadRequest, "invalid_request");

        try
        {
            var token = await github.ExchangeCodeAsync(code, ct);
            if (token is null)
                return Error(StatusCodes.Status400BadRequest, "invalid_code");

            var githubUser = await github.GetUserAsync(token, ct);
            if (githubUser is null)
                return Error(StatusCodes.Status400BadRequest, "invalid_code");
            if (githubUser.Id != caller.Member.GithubUserId)
                return Error(StatusCodes.Status403Forbidden, "not_your_account");

            var owned = await github.ListUserInstallationIdsAsync(token, ct);
            if (owned is null)
                return Error(StatusCodes.Status400BadRequest, "invalid_code");
            if (!owned.Contains(installationId))
                return Error(StatusCodes.Status403Forbidden, "not_your_installation");

            var installation = await github.GetInstallationAsync(installationId, ct);
            if (installation is null)
                return Error(StatusCodes.Status403Forbidden, "not_your_installation");
            if (string.Equals(installation.AccountType, "User", StringComparison.OrdinalIgnoreCase))
            {
                if (installation.AccountId != githubUser.Id)
                    return Error(StatusCodes.Status403Forbidden, "not_your_account");
                return Linked(await LinkAsync(sync, github, logs, caller.TenantId, installationId, installation, ct));
            }
            if (!IsOrganization(installation.AccountType))
                return Error(StatusCodes.Status422UnprocessableEntity, "unsupported_account");

            var membership = await github.GetOrganizationMembershipAsync(
                installationId, installation.AccountLogin, githubUser.Id, githubUser.Login, ct);
            return membership switch
            {
                OrgMembership.Owner => Linked(await LinkAsync(sync, github, logs, caller.TenantId, installationId, installation, ct)),
                OrgMembership.Member => Error(StatusCodes.Status403Forbidden, "not_org_owner"),
                OrgMembership.NotGranted => Error(StatusCodes.Status422UnprocessableEntity, "org_members_not_granted"),
                _ => Error(StatusCodes.Status403Forbidden, "not_org_member"),
            };
        }
        catch (GitHubAppKeyException)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "github_app_not_configured");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or GitHubSourceException
                                  || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Unreachable, timed out (HttpClient reports its timeout as a cancellation) or answered with a non-JSON page.
            return Error(StatusCodes.Status502BadGateway, "github_unavailable");
        }
    }

    internal static IResult Error(int status, string code) => Results.Json(new ErrorBody(code), statusCode: status);

    private static IResult Linked((GithubConnection Connection, bool Created)? linked) =>
        linked is { } l
            ? Results.Json(l.Connection, statusCode: l.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK)
            : Error(StatusCodes.Status409Conflict, "account_connected");

    internal static bool IsOrganization(string accountType) =>
        string.Equals(accountType, "Organization", StringComparison.OrdinalIgnoreCase);

    // Null when the account or installation is actively connected to another tenant.
    // Saves the verified installation, then (for an organization) closes the company's open install request and
    // imports the organization's members. A failed import leaves the connection in place without a member list.
    internal static async Task<(GithubConnection Connection, bool Created)?> LinkAsync(
        NpgsqlDataSource sync, IGitHubAppClient github, ILoggerFactory logs, Guid tenantId, long installationId,
        GitHubInstallation installation, CancellationToken ct)
    {
        var organization = IsOrganization(installation.AccountType);
        var saved = await SaveAsync(sync, tenantId, installationId, installation, organization, ct);
        if (saved is null)
            return null;

        if (organization)
        {
            try
            {
                await OrgMembers.ImportAsync(sync, github, tenantId, saved.Id, installationId, installation.AccountLogin, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logs.CreateLogger(typeof(ConnectGithub)).LogWarning(
                    "Organization member import failed ({Kind})", e.GetType().Name);
            }
        }

        await using var conn = await sync.OpenConnectionAsync(ct);
        return (await ConnectionsEndpoints.ReadAsync(conn, tenantId, saved.Id, ct), saved.Created);
    }

    private static async Task<ConnectGithubRequest?> ReadAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType())
            return null;
        try
        {
            return await http.Request.ReadFromJsonAsync<ConnectGithubRequest>(ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Saved(Guid Id, bool Created);

    private sealed record ExistingRow(Guid Id, Guid TenantId, string AuthKind);

    // Null when the account or installation is actively connected to another tenant, or the account is connected with a token.
    private static async Task<Saved?> SaveAsync(
        NpgsqlDataSource sync, Guid tenantId, long installationId, GitHubInstallation installation, bool organization,
        CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var args = new
        {
            tenantId, installationId, accountId = installation.AccountId,
            login = installation.AccountLogin, selection = installation.RepositorySelection,
            type = organization ? "organization" : "user",
        };

        var existing = await conn.QuerySingleOrDefaultAsync<ExistingRow>(new CommandDefinition(
            "select id, tenant_id, auth_kind from github_connections where account_id = @accountId and disconnected_at is null for update",
            args, tx, cancellationToken: ct));
        if (existing is not null && (existing.TenantId != tenantId || existing.AuthKind == "token"))
            return null;

        try
        {
            Saved saved;
            if (existing is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    update github_connections
                       set installation_id = @installationId, repository_selection = @selection, account_login = @login
                     where id = @id
                    """, new { installationId, args.selection, args.login, existing.Id }, tx, cancellationToken: ct));
                saved = new Saved(existing.Id, false);
            }
            else
            {
                var id = await conn.QuerySingleAsync<Guid>(new CommandDefinition("""
                    insert into github_connections (tenant_id, account_id, account_login, account_type, installation_id, repository_selection)
                    values (@tenantId, @accountId, @login, @type, @installationId, @selection)
                    returning id
                    """, args, tx, cancellationToken: ct));
                saved = new Saved(id, true);
            }

            if (organization)
                await conn.ExecuteAsync(new CommandDefinition("""
                    update github_connection_requests
                       set resolved_at = now(), connection_id = @id, account_login = coalesce(account_login, @login)
                     where tenant_id = @tenantId and resolved_at is null
                    """, new { saved.Id, tenantId, args.login }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return saved;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
    }
}
