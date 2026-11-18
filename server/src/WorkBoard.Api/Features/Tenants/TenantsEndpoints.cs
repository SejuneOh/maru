using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using WorkBoard.Api.Features.Me;
using WorkBoard.Api.Features.Members;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Tenants;

public sealed record CreateTenantRequest(string? Name);

public sealed record CreateTenantResponse(TenantInfo Tenant, Member Member);

public static class TenantsEndpoints
{
    public const int NameMaxLength = 80;
    public const int MaxTenantsPerPerson = 5;

    // Not behind CurrentMemberFilter: the caller has no membership yet.
    public static void MapTenants(this RouteGroupBuilder api) =>
        api.MapPost("/tenants", async (
            HttpContext http, ICallerResolver callers, IAuthIdentitySource source, IMemberLookup members,
            [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, CancellationToken ct) =>
        {
            var caller = await callers.ResolveAsync(http, ct);
            if (caller is null)
                return Error(StatusCodes.Status401Unauthorized, "unauthenticated");

            var profile = await ProfileAsync(caller, source, members, ct);
            if (profile is null || string.IsNullOrWhiteSpace(profile.Login))
                return Error(StatusCodes.Status401Unauthorized, "unauthenticated");

            var name = await ReadNameAsync(http, ct);
            if (name is null)
                return Error(StatusCodes.Status400BadRequest, "invalid_name");

            var key = http.Request.Headers[Idempotency.HeaderName].ToString();
            if (key.Length > Idempotency.KeyMaxLength)
                return Error(StatusCodes.Status400BadRequest, "invalid_request");

            return await CreateAsync(sync, name, profile, key, ct);
        });

    private static IResult Error(int status, string code) => Results.Json(new ErrorBody(code), statusCode: status);

    internal static async Task<GithubProfile?> ProfileAsync(
        Caller caller, IAuthIdentitySource source, IMemberLookup members, CancellationToken ct)
    {
        if (caller.AuthUserId is { } authUserId)
        {
            var profile = await source.FindGithubProfileAsync(authUserId, ct);
            return profile?.Id == caller.GithubUserId ? profile : null;
        }

        var existing = (await members.FindByUserIdAsync(caller.GithubUserId, ct)).Select(i => i.Member).FirstOrDefault();
        return new GithubProfile(caller.GithubUserId, caller.DevLogin, existing?.DisplayName, existing?.AvatarUrl);
    }

    private static async Task<string?> ReadNameAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType())
            return null;

        CreateTenantRequest? body;
        try
        {
            body = await http.Request.ReadFromJsonAsync<CreateTenantRequest>(ct);
        }
        catch (JsonException)
        {
            return null;
        }

        var name = body?.Name?.Trim();
        return string.IsNullOrEmpty(name) || name.Length > NameMaxLength || !IsPrintable(name) ? null : name;
    }

    // Rejects control and invisible format characters (zero-width, bidi overrides) and broken surrogates.
    internal static bool IsPrintable(string name)
    {
        for (var i = 0; i < name.Length;)
        {
            if (Rune.DecodeFromUtf16(name.AsSpan(i), out var rune, out var used) != OperationStatus.Done)
                return false;
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
                return false;
            i += used;
        }
        return true;
    }

    // 409 tenant_limit when the person already created MaxTenantsPerPerson tenants. A non-empty key makes a repeat
    // within the key lifetime return the stored 201, or 409 idempotency_key_reused for a different name.
    private static async Task<IResult> CreateAsync(
        NpgsqlDataSource sync, string name, GithubProfile profile, string key, CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Serializes concurrent creations by the same person so the limit and the key hold.
        await conn.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(@key)", new { key = profile.Id }, tx, cancellationToken: ct));

        var useKey = !string.IsNullOrWhiteSpace(key);
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        if (useKey)
        {
            var stored = await conn.QuerySingleOrDefaultAsync<StoredCreate>(new CommandDefinition("""
                select request_hash as RequestHash, status_code as StatusCode, response as Response
                  from tenant_create_keys
                 where github_user_id = @id and key = @key and created_at > now() - @lifetime
                """, new { id = profile.Id, key, lifetime = Idempotency.Lifetime }, tx, cancellationToken: ct));
            if (stored is not null)
                return stored.RequestHash == requestHash
                    ? (IResult)new IdempotentResponse(stored.StatusCode, stored.Response, Replayed: true)
                    : Error(StatusCodes.Status409Conflict, ApiErrors.IdempotencyKeyReused);
        }

        var owned = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*) from tenants where created_by_github_user_id = @id", new { id = profile.Id }, tx, cancellationToken: ct));
        if (owned >= MaxTenantsPerPerson)
            return Error(StatusCodes.Status409Conflict, "tenant_limit");

        var tenantId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "insert into tenants (name, created_by_github_user_id) values (@name, @id) returning id", new { name, id = profile.Id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "insert into tenant_settings (tenant_id) values (@tenantId)", new { tenantId }, tx, cancellationToken: ct));
        var row = await conn.QuerySingleAsync<MemberRow>(new CommandDefinition("""
            insert into members (tenant_id, github_user_id, github_login, display_name, avatar_url, role)
            values (@tenantId, @id, @login, @displayName, @avatarUrl, 'admin')
            returning id, github_user_id, github_login, display_name, avatar_url, role
            """, new { tenantId, id = profile.Id, login = profile.Login, displayName = profile.DisplayName, avatarUrl = profile.AvatarUrl },
            tx, cancellationToken: ct));

        var json = JsonSerializer.Serialize(
            new CreateTenantResponse(new TenantInfo(tenantId, name), row.ToMember()), JsonSerializerOptions.Web);
        if (useKey)
            await conn.ExecuteAsync(new CommandDefinition("""
                insert into tenant_create_keys (github_user_id, key, request_hash, status_code, response)
                values (@id, @key, @requestHash, @status, @json)
                on conflict (github_user_id, key) do update
                   set request_hash = excluded.request_hash, status_code = excluded.status_code,
                       response = excluded.response, created_at = now()
                """, new { id = profile.Id, key, requestHash, status = StatusCodes.Status201Created, json }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new IdempotentResponse(StatusCodes.Status201Created, json, Replayed: false);
    }

    private sealed record StoredCreate(string RequestHash, int StatusCode, string Response);
}
