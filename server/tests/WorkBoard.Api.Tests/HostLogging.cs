using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkBoard.Api.Tests;

[assembly: HostingStartup(typeof(HostLogging))]

namespace WorkBoard.Api.Tests;

// Every test host (found through ASPNETCORE_HOSTINGSTARTUPASSEMBLIES) appends its errors to host-errors.log next
// to the test binaries, so the server exception behind a failing request can be read after the run.
public sealed class HostLogging : IHostingStartup
{
    private static readonly object Gate = new();
    public static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "host-errors.log");

    // Production-mode test hosts use the local default role passwords; the guard has its own tests, which remove this.
    public void Configure(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.AddLogging(logging => logging.AddProvider(new FileProvider()));
            services.AddSingleton<WorkBoard.Api.Infrastructure.Hardening.StartupGuardBypass>();
        });

    private sealed class FileProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);
        public void Dispose() { }
    }

    private sealed class FileLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var line = $"{DateTime.UtcNow:O} {logLevel} {category}: {formatter(state, exception)}{Environment.NewLine}{exception}{Environment.NewLine}";
            lock (Gate)
                File.AppendAllText(LogPath, line);
        }
    }
}
