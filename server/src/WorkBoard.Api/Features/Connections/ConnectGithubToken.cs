using System.Text.Json;
using Dapper;
using Npgsql;
using WorkBoard.Api.Infrastructure.Credentials;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Connections;

public sealed record ConnectGithubTokenRequest(string? Token, string? Organization);

public sealed record ReplaceGithubTokenRequest(string? Token);

// Connects an organization with a personal access token instead of the GitHub App. The token is checked against GitHub,
// stored encrypted (CredentialStore) and never returned: responses and audit rows show its last 4 characters at most.
public static class ConnectGithubToken
{
    public const string FineGrainedPrefix = "github_pat_";
    private const int MinTokenLength = 20;
    private const int MaxTokenLength = 255;

    // Classic scopes the board accepts. repo and read:org are required; anything else is broader than needed.
    private static readonly string[] Required = ["repo", "read:org"];
    private static readonly HashSet<string> Allowed =
        new(["repo", "read:org", "public_repo", "read:user", "user:email"], StringComparer.OrdinalIgnoreCase);

    public static void MapConnectGithubToken(this RouteGroupBuilder api)
    {
        api.MapPost("/connections/github-token", ConnectAsync);
        api.MapPut("/connections/{id:guid}/github-token", ReplaceAsync);
        api.MapDelete("/connections/{id:guid}/github-token", DeleteAsync);
    }

    private sealed record Verified(
        long OrgId, string OrgLogin, string Kind, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt, string OwnerLogin);

    private sealed record Failure(int Status, string Code, object? Body = null);

    private sealed record Existing(Guid Id, Guid TenantId);

    private sealed record TokenConnection(Guid Id, long AccountId, string AccountLogin);

    private static async Task<IResult> ConnectAsync(
        HttpContext http, ICurrentUser user, IGitHubTokenClient github, ICredentialKeys keys,
        [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, CredentialStore store, TenantDb db, IAuditLog audit,
        ILoggerFactory logs, CancellationToken ct)
    {
        var caller = user.Identity;
        if (caller.Member.Role != "admin")
            return ConnectGithub.Error(StatusCodes.Status403Forbidden, ApiErrors.AdminOnly);
        var request = await ReadAsync<ConnectGithubTokenRequest>(http, ct);
        if (request?.Token is not { } raw || CleanToken(raw) is not { } token
            || request.Organization?.Trim() is not { } org || !GitHubUserLookup.IsValidLogin(org))
            return ConnectGithub.Error(StatusCodes.Status400BadRequest, ApiErrors.InvalidRequest);
        if (KeysUnavailable(keys))
            return ConnectGithub.Error(StatusCodes.Status503ServiceUnavailable, CredentialStoreNotConfigured);

        var log = logs.CreateLogger(typeof(ConnectGithubToken));
        var verified = await VerifyAsync(github, token, org, caller.Member.GithubUserId, ct);
        if (verified.Failure is { } failure)
            return await FailAsync(db, audit, log, "connect", org, token, failure, null);
        var v = verified.Value!;

        Guid id;
        try
        {
            var saved = await SaveAsync(sync, store, caller.TenantId, caller.Member.Id, token, v, ct);
            if (saved is null)
                return await FailAsync(db, audit, log, "connect", org, token,
                    new Failure(StatusCodes.Status409Conflict, "account_connected"), null);
            id = saved.Value;
        }
        catch (Exception e) when (e is CredentialKeyException or CredentialException)
        {
            return ConnectGithub.Error(StatusCodes.Status503ServiceUnavailable, CredentialStoreNotConfigured);
        }

        await AuditAsync(db, audit, log, "connection.token_connect", id, TokenFacts(v, token, "connect"));
        await using var conn = await sync.OpenConnectionAsync(ct);
        return Results.Json(await ConnectionsEndpoints.ReadAsync(conn, caller.TenantId, id, ct),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ReplaceAsync(
        Guid id, HttpContext http, ICurrentUser user, IGitHubTokenClient github, ICredentialKeys keys,
        [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, CredentialStore store, TenantDb db, IAuditLog audit,
        ILoggerFactory logs, CancellationToken ct)
    {
        var caller = user.Identity;
        if (caller.Member.Role != "admin")
            return ConnectGithub.Error(StatusCodes.Status403Forbidden, ApiErrors.AdminOnly);
        var request = await ReadAsync<ReplaceGithubTokenRequest>(http, ct);
        if (request?.Token is not { } raw || CleanToken(raw) is not { } token)
            return ConnectGithub.Error(StatusCodes.Status400BadRequest, ApiErrors.InvalidRequest);

        var connection = await FindActiveAsync(sync, caller.TenantId, id, ct);
        if (connection is null)
            return ConnectGithub.Error(StatusCodes.Status404NotFound, ApiErrors.NotFound);
        if (KeysUnavailable(keys))
            return ConnectGithub.Error(StatusCodes.Status503ServiceUnavailable, CredentialStoreNotConfigured);

        var log = logs.CreateLogger(typeof(ConnectGithubToken));
        var verified = await VerifyAsync(github, token, connection.AccountLogin, caller.Member.GithubUserId, ct);
        if (verified.Failure is { } failure)
            return await FailAsync(db, audit, log, "replace", connection.AccountLogin, token, failure, id);
        var v = verified.Value!;
        if (v.OrgId != connection.AccountId)
            return await FailAsync(db, audit, log, "replace", connection.AccountLogin, token,
                new Failure(StatusCodes.Status422UnprocessableEntity, "organization_mismatch"), id);

        try
        {
            await store.SaveAsync(caller.TenantId, id, caller.Member.Id, token, v.Scopes, v.ExpiresAt, ct, v.OwnerLogin);
        }
        catch (Exception e) when (e is CredentialKeyException or CredentialException)
        {
            return ConnectGithub.Error(StatusCodes.Status503ServiceUnavailable, CredentialStoreNotConfigured);
        }

        await AuditAsync(db, audit, log, "connection.token_replace", id, TokenFacts(v, token, "replace"));
        await using var conn = await sync.OpenConnectionAsync(ct);
        return Results.Ok(await ConnectionsEndpoints.ReadAsync(conn, caller.TenantId, id, ct));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, ICurrentUser user, [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, CredentialStore store,
        TenantDb db, IAuditLog audit, ILoggerFactory logs, CancellationToken ct)
    {
        var caller = user.Identity;
        if (caller.Member.Role != "admin")
            return ConnectGithub.Error(StatusCodes.Status403Forbidden, ApiErrors.AdminOnly);

        await using (var conn = await sync.OpenConnectionAsync(ct))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var changed = await conn.ExecuteAsync(new CommandDefinition("""
                update github_connections set disconnected_at = now()
                 where tenant_id = @tenantId and id = @id and auth_kind = 'token' and disconnected_at is null
                """, new { tenantId = caller.TenantId, id }, tx, cancellationToken: ct));
            if (changed == 0)
                return ConnectGithub.Error(StatusCodes.Status404NotFound, ApiErrors.NotFound);
            await conn.ExecuteAsync(new CommandDefinition(
                "delete from github_credentials where tenant_id = @tenantId and connection_id = @id",
                new { tenantId = caller.TenantId, id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            await OrgMembers.RecomputeTenantsAsync(conn, [caller.TenantId], ct);
        }

        await AuditAsync(db, audit, logs.CreateLogger(typeof(ConnectGithubToken)), "connection.token_delete", id, new { });
        return Results.NoContent();
    }

    public const string CredentialStoreNotConfigured = "credential_store_not_configured";

    private static string? CleanToken(string raw)
    {
        var token = raw.Trim();
        return token.Length is < MinTokenLength or > MaxTokenLength || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            ? null
            : token;
    }

    private static bool KeysUnavailable(ICredentialKeys keys)
    {
        try
        {
            keys.Get();
            return false;
        }
        catch (CredentialKeyException)
        {
            return true;
        }
    }

    private static async Task<(Verified? Value, Failure? Failure)> VerifyAsync(
        IGitHubTokenClient github, string token, string organization, long callerGithubUserId, CancellationToken ct)
    {
        try
        {
            var identity = await github.GetIdentityAsync(token, ct);
            if (identity is null)
                return (null, new Failure(StatusCodes.Status400BadRequest, "invalid_token"));
            if (identity.User.Id != callerGithubUserId)
                return (null, new Failure(StatusCodes.Status403Forbidden, "not_your_account"));
            if (identity.ExpiresAt is { } at && at <= DateTimeOffset.UtcNow)
                return (null, new Failure(StatusCodes.Status400BadRequest, "invalid_token"));

            string kind;
            IReadOnlyList<string> scopes;
            if (identity.Scopes is null)
            {
                if (!token.StartsWith(FineGrainedPrefix, StringComparison.Ordinal))
                    return (null, new Failure(StatusCodes.Status400BadRequest, "unsupported_token"));
                (kind, scopes) = ("fine_grained", []);
            }
            else
            {
                (kind, scopes) = ("classic", identity.Scopes);
                var tooBroad = scopes.Where(s => !Allowed.Contains(s)).ToArray();
                if (tooBroad.Length > 0)
                    return (null, new Failure(StatusCodes.Status422UnprocessableEntity, "token_scope_too_broad",
                        new { error = "token_scope_too_broad", scopes = tooBroad }));
                var missing = Required.Where(r => !scopes.Contains(r, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (missing.Length > 0)
                    return (null, new Failure(StatusCodes.Status422UnprocessableEntity, "token_scope_missing",
                        new { error = "token_scope_missing", missing }));
            }

            var access = await github.GetOrganizationAccessAsync(token, organization, ct);
            if (access is null)
                return (null, new Failure(StatusCodes.Status400BadRequest, "invalid_token"));
            return access.Status switch
            {
                OrgAccessStatus.Active => (new Verified(access.OrgId!.Value, access.OrgLogin!, kind, scopes, identity.ExpiresAt,
                    identity.User.Login), null),
                OrgAccessStatus.NotMember => (null, new Failure(StatusCodes.Status403Forbidden, "not_org_member")),
                OrgAccessStatus.NotActive => (null, new Failure(StatusCodes.Status403Forbidden, "org_membership_inactive")),
                OrgAccessStatus.SsoRequired => (null, new Failure(StatusCodes.Status403Forbidden, "sso_not_authorized",
                    new { error = "sso_not_authorized", ssoUrl = access.SsoUrl })),
                _ => (null, new Failure(StatusCodes.Status403Forbidden, "org_access_denied")),
            };
        }
        catch (Exception e) when (e is GitHubSourceException or HttpRequestException)
        {
            return (null, new Failure(StatusCodes.Status502BadGateway, ApiErrors.GithubUnavailable));
        }
    }

    // Null when the organization is actively connected already, by this company or another one.
    private static async Task<Guid?> SaveAsync(
        NpgsqlDataSource sync, CredentialStore store, Guid tenantId, Guid memberId, string token, Verified v, CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var existing = await conn.QuerySingleOrDefaultAsync<Existing>(new CommandDefinition(
            "select id, tenant_id from github_connections where account_id = @accountId and disconnected_at is null for update",
            new { accountId = v.OrgId }, tx, cancellationToken: ct));
        if (existing is not null)
            return null;

        var id = Guid.NewGuid();
        try
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                insert into github_connections (id, tenant_id, account_id, account_login, account_type, auth_kind, store_bodies)
                values (@id, @tenantId, @accountId, @login, 'organization', 'token', false)
                """, new { id, tenantId, accountId = v.OrgId, login = v.OrgLogin }, tx, cancellationToken: ct));
            await store.SaveAsync(conn, tx, tenantId, id, memberId, token, v.Scopes, v.ExpiresAt, v.OwnerLogin, ct);
            await conn.ExecuteAsync(new CommandDefinition("""
                update github_connection_requests
                   set resolved_at = now(), connection_id = @id, account_login = coalesce(account_login, @login)
                 where tenant_id = @tenantId and resolved_at is null
                """, new { id, tenantId, login = v.OrgLogin }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return id;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
    }

    private static async Task<TokenConnection?> FindActiveAsync(NpgsqlDataSource sync, Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TokenConnection>(new CommandDefinition("""
            select id, account_id, account_login from github_connections
             where tenant_id = @tenantId and id = @id and auth_kind = 'token' and disconnected_at is null
            """, new { tenantId, id }, cancellationToken: ct));
    }

    private static object TokenFacts(Verified v, string token, string operation) => new
    {
        operation, organization = v.OrgLogin, tokenKind = v.Kind, last4 = Last4(token), scopes = v.Scopes,
        expiresAt = v.ExpiresAt, owner = v.OwnerLogin,
    };

    private static string Last4(string token) => token[^4..];

    private static async Task AuditAsync(
        TenantDb db, IAuditLog audit, ILogger log, string action, Guid? targetId, object after)
    {
        try
        {
            await db.WriteAsync(async s =>
            {
                await audit.WriteAsync(s, action, "connection", targetId, null, after);
                return 0;
            }, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogError("Audit row {Action} failed ({Kind})", action, e.GetType().Name);
        }
    }

    private static async Task<IResult> FailAsync(
        TenantDb db, IAuditLog audit, ILogger log, string operation, string organization, string token, Failure failure,
        Guid? connectionId)
    {
        log.LogWarning("Token connection {Operation} refused: {Code}", operation, failure.Code);
        await AuditAsync(db, audit, log, "connection.token_failed", connectionId,
            new { operation, organization, last4 = Last4(token), reason = failure.Code });
        return failure.Body is { } body
            ? Results.Json(body, statusCode: failure.Status)
            : ConnectGithub.Error(failure.Status, failure.Code);
    }

    private static async Task<T?> ReadAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (!http.Request.HasJsonContentType())
            return null;
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
