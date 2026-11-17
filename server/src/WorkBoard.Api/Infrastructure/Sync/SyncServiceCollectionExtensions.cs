namespace WorkBoard.Api.Infrastructure.Sync;

public static class SyncServiceCollectionExtensions
{
    public static IServiceCollection AddWorkBoardSync(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SyncOptions>(config.GetSection(SyncOptions.Section));
        services.AddSingleton<MirrorWriter>();
        services.AddSingleton<SyncRunner>();
        services.AddSingleton<SprintDailyRecorder>();
        services.AddHostedService<SyncWorker>();
        services.AddSingleton<Webhooks.WebhookWake>();
        services.AddSingleton<Webhooks.WebhookProcessor>();
        services.AddHostedService<Webhooks.WebhookWorker>();
        return services;
    }
}
