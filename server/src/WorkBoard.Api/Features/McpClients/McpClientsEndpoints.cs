using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.McpClients;

public sealed record McpClientOwner(string GithubLogin, string? DisplayName);

public sealed record McpClient(
    Guid Id, string Name, string Scope, string TokenPrefix, DateTime CreatedAt,
    DateTime? LastUsedAt, DateTime ExpiresAt, DateTime? RevokedAt, McpClientOwner? Owner);

// The plaintext token is the only copy of the secret: shown once, null in a replay of the same Idempotency-Key.
public sealed record CreatedMcpClient(McpClient Client, string? Token);

internal sealed record CreateMcpClientRequest(string? Name, string? Scope, int? ExpiresInDays);

/// <summary>
/// Access tokens for AI tools (MCP clients). A member mints tokens for their own board access; an admin can list
/// and revoke every token in the company. The token is minted and hashed here; only the hash is stored.
/// </summary>
public static class McpClients
{
    public const string TokenPrefix = "wbm_";
    public const int DefaultLifetimeDays = 30;
    public const int MaxLifetimeDays = 90;
    public const int NameMaxLength = 80;
    public const int MaxActivePerMember = 20;
    private const int MaxBodyBytes = 4096;

    public const string InvalidName = "invalid_name";
    public const string InvalidScope = "invalid_scope";
    public const string InvalidExpiry = "invalid_expiry";
    public const string TooManyClients = "too_many_clients";

    private static readonly string[] Scopes = ["read", "write"];

    // Admin and member endpoints, behind CurrentMemberFilter.
    public static void MapMcpClients(this RouteGroupBuilder api)
    {
        api.MapGet("/mcp-clients", ListAsync);
        api.MapPost("/mcp-clients", CreateAsync);
        api.MapDelete("/mcp-clients/{id:guid}", RevokeAsync);
    }

    /// <summary>A new token: the prefix plus 256 bits of randomness, base64url, shown to the caller once.</summary>
    public static string NewToken() => TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static WriteResult WriteError(int status, string code) => new(status, new ErrorBody(code));

    // ---- list ----

    // The caller's own tokens, or, for an admin with ?all=true, every token in the company (with its owner).
    private static async Task<IResult> ListAsync(bool? all, ICurrentUser user, TenantDb db, CancellationToken ct)
    {
        var adminAll = all == true && user.Identity.Member.Role == "admin";
        var memberId = user.Identity.Member.Id;
        var rows = await db.ReadAsync(s => s.QueryAsync<McpClientRow>($"""
            select c.id, c.name, c.scope, c.token_prefix, c.created_at, c.last_used_at, c.expires_at, c.revoked_at,
                   m.github_login as owner_login, m.display_name as owner_name
              from mcp_clients c
              join members m on m.id = c.owner_member_id
             where {(adminAll ? "true" : "c.owner_member_id = @memberId")}
             order by c.created_at desc
            """, new { memberId }), ct);
        return Results.Ok(rows.Select(r => r.ToClient(withOwner: adminAll)).ToList());
    }

    // ---- create ----

    private static async Task<IResult> CreateAsync(
        HttpContext http, ICurrentUser user, TenantDb db, IAuditLog audit, CancellationToken ct)
    {
        var bytes = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        if (bytes is null || !http.Request.HasJsonContentType())
            return ApiErrors.BadRequest();
        CreateMcpClientRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<CreateMcpClientRequest>(bytes, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return ApiErrors.BadRequest();
        }

        var name = request?.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > NameMaxLength)
            return ApiErrors.BadRequest(InvalidName);
        var scope = request?.Scope ?? "read";
        if (!Scopes.Contains(scope))
            return ApiErrors.BadRequest(InvalidScope);
        var days = request?.ExpiresInDays ?? DefaultLifetimeDays;
        if (days is < 1 or > MaxLifetimeDays)
            return ApiErrors.BadRequest(InvalidExpiry);

        var key = http.Request.Headers[Idempotency.HeaderName].ToString();
        if (key.Length > Idempotency.KeyMaxLength)
            return ApiErrors.BadRequest();
        var hash = Idempotency.HashRequest(
            user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);

        return await db.WriteAsync(s => Idempotency.RunAsync(s, user, key, hash,
            () => InsertAsync(s, user, audit, name, scope, days),
            body => body is CreatedMcpClient created ? created with { Token = null } : body), ct);
    }

    private static async Task<WriteResult> InsertAsync(
        TenantSession s, ICurrentUser user, IAuditLog audit, string name, string scope, int days)
    {
        var tenantId = user.Identity.TenantId;
        var memberId = user.Identity.Member.Id;
        // One mint at a time per member, so a burst cannot slip past the active-token cap.
        await s.ExecuteAsync("select pg_advisory_xact_lock(hashtextextended(@scope, 0))",
            new { scope = $"mcp-client:{memberId:N}" });

        var active = await s.QuerySingleOrDefaultAsync<int>(
            "select count(*)::int from mcp_clients where owner_member_id = @memberId and revoked_at is null and expires_at > now()",
            new { memberId });
        if (active >= MaxActivePerMember)
            return WriteError(StatusCodes.Status409Conflict, TooManyClients);

        var token = NewToken();
        var id = await s.QuerySingleOrDefaultAsync<Guid>("""
            insert into mcp_clients (tenant_id, owner_member_id, name, token_hash, token_prefix, scope, expires_at)
            values (@tenantId, @memberId, @name, @hash, @prefix, @scope, now() + make_interval(days => @days))
            returning id
            """, new { tenantId, memberId, name, hash = HashToken(token), prefix = token[..12], scope, days });

        var client = (await s.QuerySingleOrDefaultAsync<McpClientRow>("""
            select c.id, c.name, c.scope, c.token_prefix, c.created_at, c.last_used_at, c.expires_at, c.revoked_at,
                   m.github_login as owner_login, m.display_name as owner_name
              from mcp_clients c join members m on m.id = c.owner_member_id where c.id = @id
            """, new { id }))!.ToClient(withOwner: false);
        await audit.WriteAsync(s, "mcp_client.create", "mcp_client", id, null, new { client.Name, client.Scope, client.ExpiresAt });
        return new WriteResult(StatusCodes.Status201Created, new CreatedMcpClient(client, token));
    }

    // ---- revoke ----

    // The caller's own token, or any token in the company when the caller is an admin. Revoking is idempotent:
    // an already-revoked or missing token is 404, and a second revoke of the same token changes nothing.
    private static async Task<IResult> RevokeAsync(
        Guid id, HttpContext http, ICurrentUser user, TenantDb db, IAuditLog audit, CancellationToken ct)
    {
        var bytes = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        if (bytes is null)
            return ApiErrors.BadRequest();
        var isAdmin = user.Identity.Member.Role == "admin";
        var memberId = user.Identity.Member.Id;
        var key = http.Request.Headers[Idempotency.HeaderName].ToString();
        var hash = Idempotency.HashRequest(
            user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);

        return await db.WriteAsync(s => Idempotency.RunAsync(s, user, key, hash, async () =>
        {
            var owner = await s.QuerySingleOrDefaultAsync<Guid?>(
                "select owner_member_id from mcp_clients where id = @id and revoked_at is null for update", new { id });
            if (owner is null || (!isAdmin && owner != memberId))
                return WriteError(StatusCodes.Status404NotFound, ApiErrors.NotFound);
            await s.ExecuteAsync("update mcp_clients set revoked_at = now() where id = @id", new { id });
            await audit.WriteAsync(s, "mcp_client.revoke", "mcp_client", id, new { ownerMemberId = owner }, null);
            return new WriteResult(StatusCodes.Status204NoContent);
        }), ct);
    }

    private sealed class McpClientRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Scope { get; set; } = "read";
        public string TokenPrefix { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? OwnerLogin { get; set; }
        public string? OwnerName { get; set; }

        public McpClient ToClient(bool withOwner) => new(
            Id, Name, Scope, TokenPrefix, CreatedAt, LastUsedAt, ExpiresAt, RevokedAt,
            withOwner && OwnerLogin is not null ? new McpClientOwner(OwnerLogin, OwnerName) : null);
    }
}
