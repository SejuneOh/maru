using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Infrastructure.Maintenance;

public sealed class MaintenanceOptions
{
    public const string Section = "Maintenance";

    // Turns the periodic cleanup off.
    public bool Enabled { get; set; } = true;

    // Rows older than this are deleted. Never below the 24 hours a stored key stays usable.
    public int IdempotencyRetentionHours { get; set; } = 48;

    public int CleanupIntervalMinutes { get; set; } = 60;

    public TimeSpan IdempotencyRetention =>
        TimeSpan.FromHours(Math.Max(IdempotencyRetentionHours, Idempotency.Lifetime.TotalHours));

    public TimeSpan CleanupInterval => TimeSpan.FromMinutes(Math.Max(1, CleanupIntervalMinutes));
}

// Runs on the sync role (RLS bypassed): expired keys of every tenant go in one pass.
public sealed class IdempotencyCleaner(
    [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, IOptions<MaintenanceOptions> options)
{
    private const int BatchSize = 1000;

    public async Task<int> DeleteExpiredAsync(CancellationToken ct)
    {
        var retention = options.Value.IdempotencyRetention;
        await using var conn = await sync.OpenConnectionAsync(ct);
        var total = 0;
        foreach (var table in new[] { "idempotency_keys", "tenant_create_keys" })
        {
            int deleted;
            do
            {
                deleted = await conn.ExecuteAsync(new CommandDefinition($"""
                    delete from {table}
                     where ctid in (select ctid from {table} where created_at < now() - @retention limit @batch)
                    """, new { retention, batch = BatchSize }, cancellationToken: ct));
                total += deleted;
            }
            while (deleted == BatchSize);
        }
        return total;
    }
}

public sealed class IdempotencyCleanupWorker(
    IdempotencyCleaner cleaner, IOptions<MaintenanceOptions> options, ILogger<IdempotencyCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var deleted = await cleaner.DeleteExpiredAsync(ct);
                if (deleted > 0)
                    logger.LogInformation("Deleted {Count} expired idempotency keys", deleted);
                await Task.Delay(options.Value.CleanupInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Idempotency key cleanup failed");
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}

public static class MaintenanceServiceCollectionExtensions
{
    public static IServiceCollection AddWorkBoardMaintenance(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MaintenanceOptions>(config.GetSection(MaintenanceOptions.Section));
        services.AddSingleton<IdempotencyCleaner>();
        services.AddHostedService<IdempotencyCleanupWorker>();
        return services;
    }
}
