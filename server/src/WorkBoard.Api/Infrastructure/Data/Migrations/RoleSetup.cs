using System.Security.Cryptography;
using System.Text;
using Npgsql;
using WorkBoard.Api.Infrastructure.Hardening;

namespace WorkBoard.Api.Infrastructure.Data.Migrations;

// Deploy-time role handling on the migration connection: real passwords for the two API roles, and a check that
// the sync role can bypass row level security (it writes across tenants).
public static class RoleSetup
{
    public const int MinPasswordLength = 16;

    public static IReadOnlyList<string> ValidatePasswords(string? app, string? sync)
    {
        var problems = new List<string>();
        foreach (var (key, value) in new[] { ("Database:AppPassword", app), ("Database:SyncPassword", sync) })
        {
            if (string.IsNullOrEmpty(value))
                problems.Add($"{key} is not set.");
            else if (value.Trim().Length < MinPasswordLength || StartupGuard.IsDefaultPassword(value))
                problems.Add($"{key} must be at least {MinPasswordLength} characters and not a default value.");
        }
        if (app is { Length: > 0 } && app == sync)
            problems.Add("Database:AppPassword and Database:SyncPassword must differ.");
        return problems;
    }

    public static async Task SetPasswordsAsync(string migrateConnectionString, string app, string sync, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(migrateConnectionString);
        await conn.OpenAsync(ct);
        await SetRolePasswordAsync(conn, "workboard_app", app, ct);
        await SetRolePasswordAsync(conn, "workboard_sync", sync, ct);
    }

    public static async Task SetRolePasswordAsync(NpgsqlConnection conn, string role, string password, CancellationToken ct = default)
    {
        await using var alter = new NpgsqlCommand(AlterPasswordStatement(role, ScramVerifier(password)), conn);
        await alter.ExecuteNonQueryAsync(ct);
    }

    // The statement carries a SCRAM verifier, never the password, so server logs and pg_stat_statements only see the hash.
    public static string AlterPasswordStatement(string role, string verifier) =>
        $"alter role {QuoteIdent(role)} password '{verifier.Replace("'", "''")}'";

    private static string QuoteIdent(string name) => '"' + name.Replace("\"", "\"\"") + '"';

    // PostgreSQL's SCRAM-SHA-256 secret (RFC 5802, 7677): SCRAM-SHA-256$<iterations>:<salt>$<StoredKey>:<ServerKey>.
    public static string ScramVerifier(string password, byte[]? salt = null, int iterations = 4096)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, Encoding.UTF8.GetBytes("Client Key"));
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HMACSHA256.HashData(salted, Encoding.UTF8.GetBytes("Server Key"));
        return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }

    // Null when the sync role bypasses RLS (or is a superuser), otherwise what is wrong.
    public static async Task<string?> CheckSyncRoleAsync(string migrateConnectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(migrateConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select rolsuper or rolbypassrls from pg_roles where rolname = 'workboard_sync'", conn);
        return await cmd.ExecuteScalarAsync(ct) switch
        {
            true => null,
            false => "Role workboard_sync cannot bypass row level security, so sync and invitation joins would see no rows. See docs/deploy.md, 'BYPASSRLS on Supabase'.",
            _ => "Role workboard_sync does not exist.",
        };
    }

    // True when the migration role is exempt from the forced RLS on tenant tables.
    public static async Task<bool> MigrationRoleBypassesRlsAsync(string migrateConnectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(migrateConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select rolsuper or rolbypassrls from pg_roles where rolname = current_user", conn);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
