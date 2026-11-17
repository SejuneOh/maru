using Microsoft.Extensions.Options;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Sync;

namespace WorkBoard.Api.Infrastructure.Webhooks;

// Wakes the worker when a delivery is queued; the worker also polls, so other API instances' deliveries are picked up.
public sealed class WebhookWake
{
    private readonly SemaphoreSlim _signal = new(0);

    public void Pulse() => _signal.Release();

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        await _signal.WaitAsync(timeout, ct);
        while (_signal.Wait(0))
        {
        }
    }
}

// Processes queued webhook deliveries and drops old ones. Off with Sync:Enabled=false or without a webhook secret.
public sealed class WebhookWorker(
    WebhookProcessor processor,
    WebhookWake wake,
    IOptions<SyncOptions> sync,
    IOptions<GitHubAppOptions> app,
    TimeProvider time,
    ILogger<WebhookWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!sync.Value.Enabled || !app.Value.WebhooksEnabled)
            return;

        var nextCleanup = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var claimed = await processor.ProcessPendingAsync(ct);
                if (time.GetUtcNow() >= nextCleanup)
                {
                    await processor.DeleteExpiredAsync(ct);
                    nextCleanup = time.GetUtcNow() + CleanupEvery;
                }
                // A short pause after work lets a burst of events for one repository share the next read.
                if (claimed > 0)
                    await Task.Delay(Settle, ct);
                else
                    await wake.WaitAsync(Poll, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Webhook worker pass failed");
                await Task.Delay(Poll, ct);
            }
        }
    }
}
