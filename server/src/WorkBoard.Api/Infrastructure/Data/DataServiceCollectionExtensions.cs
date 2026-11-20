using Npgsql;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Infrastructure.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddWorkBoardData(this IServiceCollection services, IConfiguration config)
    {
        services.AddKeyedSingleton(DataKeys.App, (_, _) => BuildSource(config, "App"));
        services.AddKeyedSingleton(DataKeys.Sync, (_, _) => BuildSource(config, "Sync"));

        services.AddSingleton<IAppConnectionFactory, NpgsqlAppConnectionFactory>();
        services.AddSingleton<IMemberLookup, NpgsqlMemberLookup>();

        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
        services.AddScoped<TenantDb>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IIssueEvents, IssueEvents>();
        services.AddScoped<IGitHubWriteTargets, GitHubWriteTargets>();
        services.AddScoped<Features.Sprints.SprintWrites>();
        services.AddScoped<Features.Projects.ProjectWrites>();
        services.AddScoped<Features.MeetingNotes.MeetingNoteWrites>();
        services.AddScoped<Features.Issues.IssueCreation>();
        services.AddScoped<Features.MeetingNotes.ActionItemIssues>();
        services.Configure<Features.MeetingNotes.WebOptions>(config.GetSection(Features.MeetingNotes.WebOptions.Section));
        services.AddScoped<Features.Issues.BoardFieldWrites>();
        services.AddScoped<Features.Issues.AssigneeWrites>();
        services.AddScoped<Features.Issues.CommentWrites>();
        return services;
    }

    private static NpgsqlDataSource BuildSource(IConfiguration config, string name) =>
        NpgsqlDataSource.Create(
            config.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Connection string '{name}' is not configured."));
}
