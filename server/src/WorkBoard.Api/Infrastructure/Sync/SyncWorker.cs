using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;

namespace WorkBoard.Api.Infrastructure.Sync;

// Syncs every connected tenant each Sync:IntervalMinutes (10, or 60 while webhook deliveries arrive), then records today's sprint_daily rows.
public sealed class SyncWorker(
    SyncRunner runner,
    SprintDailyRecorder sprintDaily,
    IOptions<SyncOptions> options,
    IOptions<GitHubAppOptions> app,
    [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync,
    TimeProvider time,
    ILogger<SyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return;

        try
        {
            await runner.FailInterruptedRunsAsync(ct);
            runner.NextRunAt = time.GetUtcNow() + StartDelay;
            await Task.Delay(StartDelay, time, ct);

            while (!ct.IsCancellationRequested)
            {
                await RunAllAsync(ct);
                await sprintDaily.RecordTodayAsync(ct);
                var interval = await IntervalAsync(ct);
                runner.NextRunAt = time.GetUtcNow() + interval;
                await Task.Delay(interval, time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogError(e, "Sync loop stopped");
        }
    }

    public static readonly TimeSpan DeliveryWindow = TimeSpan.FromMinutes(30);

    // The long interval applies only while a verified delivery was stored within DeliveryWindow.
    public async Task<TimeSpan> IntervalAsync(CancellationToken ct)
    {
        var recent = false;
        if (options.Value.IntervalMinutes is null && app.Value.WebhooksEnabled)
        {
            try
            {
                await using var conn = await sync.OpenConnectionAsync(ct);
                recent = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "select exists (select 1 from webhook_deliveries where received_at > now() - @window)",
                    new { window = DeliveryWindow }, cancellationToken: ct));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Reading webhook activity failed");
            }
        }
        return options.Value.IntervalFor(recent);
    }

    private async Task RunAllAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await sync.OpenConnectionAsync(ct);
            var tenants = (await conn.QueryAsync<Guid>(new CommandDefinition("""
                select distinct tenant_id from github_connections
                 where disconnected_at is null and ((installation_id is not null and @appReady) or auth_kind = 'token')
                """, new { appReady = app.Value.CanReadInstallations }, cancellationToken: ct))).AsList();
            foreach (var tenantId in tenants)
                if (runner.TryStart(tenantId) is { } running)
                    await running;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Sync pass failed");
        }
    }
}
