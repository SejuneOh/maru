using System.Runtime.CompilerServices;

namespace WorkBoard.Api.Tests;

internal static class TestEnvironment
{
    // Test hosts run in Development, where the periodic sync loop would otherwise start and call GitHub.
    [ModuleInitializer]
    internal static void DisablePeriodicSync()
    {
        Environment.SetEnvironmentVariable("Sync__Enabled", "false");
        Environment.SetEnvironmentVariable("Maintenance__Enabled", "false");
        // Production-mode test hosts use the local default role passwords; the guard has its own tests.
        Environment.SetEnvironmentVariable("Hardening__SkipChecks", "true");
    }
}
