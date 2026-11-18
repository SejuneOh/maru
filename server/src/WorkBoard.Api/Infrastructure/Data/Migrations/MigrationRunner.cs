using Npgsql;

namespace WorkBoard.Api.Infrastructure.Data.Migrations;

public static class MigrationRunner
{
    // Applies pending migrations; on a database that had no schema at all, also loads the sample data.
    // production: also sets the API role passwords from Database:AppPassword / Database:SyncPassword and requires the sync
    // role to bypass RLS, so a deployment never serves with the baseline's default passwords.
    public static async Task<MigrationResult> RunAsync(
        IConfiguration config, bool seedFreshDatabase, ILogger logger, bool production = false)
    {
        var connectionString = config.GetConnectionString("Migrate")
            ?? throw new MigrationException(
                "ConnectionStrings:Migrate is not configured. Migrations need a connection that owns the schema (not workboard_app or workboard_sync).");

        var appPassword = config["Database:AppPassword"];
        var syncPassword = config["Database:SyncPassword"];
        if (production)
        {
            var problems = RoleSetup.ValidatePasswords(appPassword, syncPassword);
            if (problems.Count > 0)
                throw new MigrationException("Cannot set the database role passwords: " + string.Join(" ", problems));
        }

        MigrationResult result;
        try
        {
            if (!await RoleSetup.MigrationRoleBypassesRlsAsync(connectionString))
                logger.LogWarning(
                    "The migration role does not bypass row level security. Data migrations on tenant tables would touch no rows (docs/deploy.md).");
            result = await new Migrator(connectionString, EmbeddedSql.Migrations()).RunAsync(seedFreshDatabase ? EmbeddedSql.Seed() : null);
        }
        catch (NpgsqlException e)
        {
            throw new MigrationException($"Could not run migrations: {e.Message}", e);
        }

        try
        {
            if (production)
            {
                await RoleSetup.SetPasswordsAsync(connectionString, appPassword!, syncPassword!);
                logger.LogInformation("Set the workboard_app and workboard_sync role passwords");
            }
            if (await RoleSetup.CheckSyncRoleAsync(connectionString) is { } roleProblem)
            {
                if (production)
                    throw new MigrationException(roleProblem);
                logger.LogWarning("{Problem}", roleProblem);
            }
        }
        catch (NpgsqlException e)
        {
            throw new MigrationException($"Could not finish the role setup: {e.Message}", e);
        }

        foreach (var name in result.Applied)
            logger.LogInformation("Applied migration {Migration}", name);
        if (result.BaselineAdopted)
            logger.LogInformation("Existing schema adopted as the 0001 baseline");
        if (result.Applied.Count == 0)
            logger.LogInformation("Database schema is up to date");

        if (result.Seeded)
            logger.LogInformation("Loaded sample data into the new database");
        return result;
    }
}
