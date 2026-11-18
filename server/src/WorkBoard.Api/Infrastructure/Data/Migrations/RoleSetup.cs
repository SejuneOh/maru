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
            else if (value.Length < MinPasswordLength || StartupGuard.DefaultPasswords.Contains(value, StringComparer.Ordinal))
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
        foreach (var (role, password) in new[] { ("workboard_app", app), ("workboard_sync", sync) })
        {
            string statement;
            await using (var format = new NpgsqlCommand("select format('alter role %I password %L', @role, @password)", conn))
            {
                format.Parameters.AddWithValue("role", role);
                format.Parameters.AddWithValue("password", password);
                statement = (string)(await format.ExecuteScalarAsync(ct))!;
            }
            await using var alter = new NpgsqlCommand(statement, conn);
            await alter.ExecuteNonQueryAsync(ct);
        }
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
