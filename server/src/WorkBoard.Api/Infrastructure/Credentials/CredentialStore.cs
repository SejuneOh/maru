using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using WorkBoard.Api.Infrastructure.Identity;

namespace WorkBoard.Api.Infrastructure.Credentials;

// Reads and writes github_credentials on the workboard_sync role, the only role allowed to touch ciphertext.
// The role bypasses RLS, so every statement filters on tenant_id itself.
public sealed class CredentialStore(
    [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync,
    CredentialCipher cipher)
{
    private sealed record Row(byte[] Ciphertext, byte[] Nonce, int KeyVersion);

    // Stores or replaces the connection's credential, active again, with usage and check times cleared.
    public async Task SaveAsync(
        Guid tenantId, Guid connectionId, Guid ownerMemberId, string token,
        IReadOnlyList<string> scopes, DateTimeOffset? expiresAt, CancellationToken ct = default,
        string? ownerLogin = null)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await SaveAsync(conn, null, tenantId, connectionId, ownerMemberId, token, scopes, expiresAt, ownerLogin, ct);
    }

    // The same on a caller's connection and transaction, e.g. together with the connection row it belongs to.
    public async Task SaveAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, Guid connectionId, Guid ownerMemberId, string token,
        IReadOnlyList<string> scopes, DateTimeOffset? expiresAt, string? ownerLogin, CancellationToken ct = default)
    {
        var sealedToken = cipher.Seal(token, tenantId, connectionId);
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into github_credentials (tenant_id, connection_id, owner_member_id, ciphertext, nonce, key_version, scopes, expires_at, last4, owner_login)
            values (@tenantId, @connectionId, @ownerMemberId, @ciphertext, @nonce, @keyVersion, @scopes, @expiresAt, @last4, @ownerLogin)
            on conflict (connection_id) do update
               set owner_member_id = excluded.owner_member_id, ciphertext = excluded.ciphertext, nonce = excluded.nonce,
                   key_version = excluded.key_version, scopes = excluded.scopes, expires_at = excluded.expires_at,
                   last4 = excluded.last4, owner_login = excluded.owner_login,
                   status = 'active', last_checked_at = null, last_used_at = null, updated_at = now()
             where github_credentials.tenant_id = excluded.tenant_id
            """, new
        {
            tenantId, connectionId, ownerMemberId, ciphertext = sealedToken.Ciphertext, nonce = sealedToken.Nonce,
            keyVersion = sealedToken.KeyVersion, scopes = scopes.ToArray(), expiresAt = expiresAt?.UtcDateTime,
            last4 = token.Length >= 4 ? token[^4..] : null, ownerLogin,
        }, tx, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid connectionId, CancellationToken ct = default)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "delete from github_credentials where tenant_id = @tenantId and connection_id = @connectionId",
            new { tenantId, connectionId }, cancellationToken: ct)) > 0;
    }

    // The token, or null when the connection has none, it is not active, or it has expired. Records last_used_at.
    public async Task<string?> OpenAsync(Guid tenantId, Guid connectionId, CancellationToken ct = default)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("""
            update github_credentials set last_used_at = now()
             where tenant_id = @tenantId and connection_id = @connectionId
               and status = 'active' and (expires_at is null or expires_at > now())
            returning ciphertext as Ciphertext, nonce as Nonce, key_version as KeyVersion
            """, new { tenantId, connectionId }, cancellationToken: ct));
        return row is null ? null : cipher.Open(new SealedCredential(row.Ciphertext, row.Nonce, row.KeyVersion), tenantId, connectionId);
    }

    public async Task<bool> SetStatusAsync(Guid tenantId, Guid connectionId, string status, CancellationToken ct = default)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            update github_credentials set status = @status, last_checked_at = now(), updated_at = now()
             where tenant_id = @tenantId and connection_id = @connectionId
            """, new { tenantId, connectionId, status }, cancellationToken: ct)) > 0;
    }

    public sealed record CredentialState(string Status, DateTime? ExpiresAt);

    // The stored credential's status, with an active one past its expiry reported as expired. Null when there is none.
    public async Task<CredentialState?> GetStateAsync(Guid tenantId, Guid connectionId, CancellationToken ct = default)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<CredentialState>(new CommandDefinition("""
            select case when status = 'active' and expires_at <= now() then 'expired' else status end as Status,
                   expires_at as ExpiresAt
              from github_credentials where tenant_id = @tenantId and connection_id = @connectionId
            """, new { tenantId, connectionId }, cancellationToken: ct));
    }

    // GitHub answered 401 for rejectedToken. Marks the stored credential revoked, but only while it still holds that
    // token: a replacement saved in the meantime stays active. Returns whether a row changed.
    public async Task<bool> RevokeAsync(Guid tenantId, Guid connectionId, string rejectedToken, CancellationToken ct = default)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("""
            select ciphertext as Ciphertext, nonce as Nonce, key_version as KeyVersion from github_credentials
             where tenant_id = @tenantId and connection_id = @connectionId and status = 'active' for update
            """, new { tenantId, connectionId }, tx, cancellationToken: ct));
        if (row is null)
            return false;
        var stored = cipher.Open(new SealedCredential(row.Ciphertext, row.Nonce, row.KeyVersion), tenantId, connectionId);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(rejectedToken)))
            return false;
        await TransitionAsync(conn, tx, tenantId, connectionId, "revoked", "connection.token_revoked", "github_401", ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // GitHub no longer accepts the token for the organization (SAML SSO authorization lost, organization policy).
    public async Task<bool> MarkInvalidAsync(Guid tenantId, Guid connectionId, string reason, CancellationToken ct = default)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var changed = await TransitionAsync(conn, tx, tenantId, connectionId, "invalid", "connection.token_invalid", reason, ct);
        await tx.CommitAsync(ct);
        return changed;
    }

    // Marks every active credential of the company whose expires_at has passed as expired; returns how many.
    public async Task<int> ExpireDueAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var due = (await conn.QueryAsync<Guid>(new CommandDefinition("""
            select connection_id from github_credentials
             where tenant_id = @tenantId and status = 'active' and expires_at <= now() for update
            """, new { tenantId }, tx, cancellationToken: ct))).AsList();
        foreach (var connectionId in due)
            await TransitionAsync(conn, tx, tenantId, connectionId, "expired", "connection.token_expired", "expires_at_passed", ct);
        await tx.CommitAsync(ct);
        return due.Count;
    }

    // Deletes the credential, leaving the connection without a token ("token needed"). Returns whether one existed.
    public async Task<bool> ReleaseAsync(Guid tenantId, Guid connectionId, string reason, CancellationToken ct = default)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var released = (await conn.QueryAsync<Released>(new CommandDefinition("""
            delete from github_credentials
             where tenant_id = @tenantId and connection_id = @connectionId
            returning connection_id as ConnectionId, last4 as Last4, owner_login as OwnerLogin, status as Status
            """, new { tenantId, connectionId }, tx, cancellationToken: ct))).AsList();
        await AuditReleasedAsync(conn, tx, tenantId, released, reason, ct);
        await tx.CommitAsync(ct);
        return released.Count > 0;
    }

    // Deletes the credentials of the company whose owner has left the company (members.left_at set).
    public async Task<int> ReleaseDepartedOwnersAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var released = (await conn.QueryAsync<Released>(new CommandDefinition("""
            delete from github_credentials k
             where k.tenant_id = @tenantId
               and exists (select 1 from members m
                            where m.tenant_id = k.tenant_id and m.id = k.owner_member_id and m.left_at is not null)
            returning k.connection_id as ConnectionId, k.last4 as Last4, k.owner_login as OwnerLogin, k.status as Status
            """, new { tenantId }, tx, cancellationToken: ct))).AsList();
        await AuditReleasedAsync(conn, tx, tenantId, released, "owner_left_company", ct);
        await tx.CommitAsync(ct);
        return released.Count;
    }

    private sealed record Released(Guid ConnectionId, string? Last4, string? OwnerLogin, string Status);

    private static async Task AuditReleasedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, List<Released> released, string reason, CancellationToken ct)
    {
        foreach (var r in released)
            await AuditAsync(conn, tx, tenantId, "connection.token_released", r.ConnectionId,
                new { reason, previousStatus = r.Status, last4 = r.Last4, owner = r.OwnerLogin }, ct);
    }

    private static async Task<bool> TransitionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, Guid connectionId, string status, string action, string reason,
        CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<Released>(new CommandDefinition("""
            update github_credentials set status = @status, last_checked_at = now(), updated_at = now()
             where tenant_id = @tenantId and connection_id = @connectionId and status = 'active'
            returning connection_id as ConnectionId, last4 as Last4, owner_login as OwnerLogin, status as Status
            """, new { tenantId, connectionId, status }, tx, cancellationToken: ct));
        if (row is null)
            return false;
        await AuditAsync(conn, tx, tenantId, action, connectionId, new { reason, last4 = row.Last4, owner = row.OwnerLogin }, ct);
        return true;
    }

    // System-made audit rows have no member. Never carries the token.
    private static Task AuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, string action, Guid connectionId, object after, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            insert into audit_log (tenant_id, action, target_type, target_id, after)
            values (@tenantId, @action, 'connection', @connectionId, cast(@after as jsonb))
            """, new { tenantId, action, connectionId, after = JsonSerializer.Serialize(after, JsonSerializerOptions.Web) },
            tx, cancellationToken: ct));

    // Seals every row that is not on the current key version again; returns how many rows changed.
    public async Task<int> ReencryptAsync(CancellationToken ct = default)
    {
        var current = cipher.CurrentKeyVersion;
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = (await conn.QueryAsync<(Guid TenantId, Guid ConnectionId, byte[] Ciphertext, byte[] Nonce, int KeyVersion)>(
            new CommandDefinition("""
                select tenant_id, connection_id, ciphertext, nonce, key_version from github_credentials
                 where key_version <> @current for update
                """, new { current }, tx, cancellationToken: ct))).AsList();
        foreach (var r in rows)
        {
            var token = cipher.Open(new SealedCredential(r.Ciphertext, r.Nonce, r.KeyVersion), r.TenantId, r.ConnectionId);
            var resealed = cipher.Seal(token, r.TenantId, r.ConnectionId);
            await conn.ExecuteAsync(new CommandDefinition("""
                update github_credentials set ciphertext = @ciphertext, nonce = @nonce, key_version = @keyVersion
                 where tenant_id = @tenantId and connection_id = @connectionId
                """, new
            {
                tenantId = r.TenantId, connectionId = r.ConnectionId, ciphertext = resealed.Ciphertext,
                nonce = resealed.Nonce, keyVersion = resealed.KeyVersion,
            }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return rows.Count;
    }
}
