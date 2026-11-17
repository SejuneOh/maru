namespace WorkBoard.Api.Infrastructure.Sync;

public sealed class SyncOptions
{
    public const string Section = "Sync";

    // Turns the periodic loop off. Manual runs through POST /api/sync/run still work.
    public bool Enabled { get; set; } = true;

    // Unset: 10 minutes, or 60 when webhooks are enabled and the loop only reconciles.
    public int? IntervalMinutes { get; set; }

    public TimeSpan IntervalFor(bool webhooksEnabled) =>
        TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes ?? (webhooksEnabled ? 60 : 10)));
}
