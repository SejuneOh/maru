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
        IReadOnlyList<string> scopes, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        var sealedToken = cipher.Seal(token, tenantId, connectionId);
        await using var conn = await sync.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into github_credentials (tenant_id, connection_id, owner_member_id, ciphertext, nonce, key_version, scopes, expires_at)
            values (@tenantId, @connectionId, @ownerMemberId, @ciphertext, @nonce, @keyVersion, @scopes, @expiresAt)
            on conflict (connection_id) do update
               set owner_member_id = excluded.owner_member_id, ciphertext = excluded.ciphertext, nonce = excluded.nonce,
                   key_version = excluded.key_version, scopes = excluded.scopes, expires_at = excluded.expires_at,
                   status = 'active', last_checked_at = null, last_used_at = null, updated_at = now()
             where github_credentials.tenant_id = excluded.tenant_id
            """, new
        {
            tenantId, connectionId, ownerMemberId, ciphertext = sealedToken.Ciphertext, nonce = sealedToken.Nonce,
            keyVersion = sealedToken.KeyVersion, scopes = scopes.ToArray(), expiresAt = expiresAt?.UtcDateTime,
        }, cancellationToken: ct));
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
