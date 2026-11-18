using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using WorkBoard.Api.Infrastructure.Data.Migrations;

namespace WorkBoard.Api.Tests;

internal static class TestEnvironment
{
    private const string Local = "Host=localhost;Port=5434;Database=workboard";

    // Each test host builds its own NpgsqlDataSources, so many short pools exist at once. A small cap and a short
    // idle lifetime keep the whole run under Postgres' default max_connections of 100.
    private const int MaxPoolSize = 3;
    private const int IdleSeconds = 3;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Test hosts run in Development, where the periodic sync loop would otherwise start and call GitHub.
        Environment.SetEnvironmentVariable("Sync__Enabled", "false");
        Environment.SetEnvironmentVariable("Maintenance__Enabled", "false");
        // Every host would otherwise hold inotify watchers on its appsettings files (Program.cs also asks for
        // reloadOnChange on appsettings.Local.json), and Linux caps inotify instances per user.
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
        Environment.SetEnvironmentVariable("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", typeof(TestEnvironment).Assembly.GetName().Name);

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DbFactAttribute.EnvVar)))
            return;

        var migrate = Env("Migrate", $"{Local};Username=postgres;Password=postgres");
        Env("App", $"{Local};Username=workboard_app;Password=workboard_app", capPool: true);
        Env("Sync", $"{Local};Username=workboard_sync;Password=workboard_sync", capPool: true);

        // Migrating once before any test runs creates the workboard_app and workboard_sync roles that every test
        // host connects with; each host's own startup migration then finds nothing to do.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Migrate"] = migrate }).Build();
        MigrationRunner.RunAsync(config, seedFreshDatabase: true, NullLogger.Instance).GetAwaiter().GetResult();
    }

    private static string Env(string name, string fallback, bool capPool = false)
    {
        var key = $"ConnectionStrings__{name}";
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(value))
            value = fallback;
        if (capPool)
            value = new NpgsqlConnectionStringBuilder(value)
            {
                MaxPoolSize = MaxPoolSize,
                ConnectionIdleLifetime = IdleSeconds,
                ConnectionPruningInterval = IdleSeconds,
            }.ConnectionString;
        Environment.SetEnvironmentVariable(key, value);
        return value;
    }
}
