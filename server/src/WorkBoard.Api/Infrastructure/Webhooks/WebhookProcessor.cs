using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using WorkBoard.Api.Features.Connections;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;

namespace WorkBoard.Api.Infrastructure.Webhooks;

// Drains webhook_deliveries on the sync role (RLS bypassed), so every statement filters on tenant_id itself.
//
// Installation and organization events are small row updates. Repository events (issues, comments, pull requests,
// reviews, milestones, labels) do not apply the payload: the repository is re-read through IGitHubSource since its
// last sync and applied by MirrorWriter, the same path as the periodic run, so the "older never overwrites newer"
// rule, issue_events and PendingLink behave identically. All pending events of one repository share one read.
public sealed class WebhookProcessor(
    IServiceScopeFactory scopes,
    SyncRunner runner,
    MirrorWriter writer,
    IOptions<GitHubAppOptions> app,
    [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync,
    ILogger<WebhookProcessor> logger)
{
    public const int MaxAttempts = 5;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private sealed record Row(
        string DeliveryId, string Event, string? Action, long? InstallationId, long? RepositoryId,
        long? SubjectUserId, long? AccountId, int Attempts);

    private sealed record Connection(Guid Id, Guid TenantId, long AccountId);

    private sealed record RepoLookup(Guid Id, string FullName, DateTime? LastSyncedAt, bool SyncEnabled);

    // Returns how many deliveries were claimed.
    public async Task<int> ProcessPendingAsync(CancellationToken ct)
    {
        var rows = await ClaimAsync(ct);
        foreach (var row in rows.Where(r => r.Event is WebhookRoute.Installation or WebhookRoute.Organization))
            await HandleAsync([row], ct, () => HandleAccountEventAsync(row, ct));

        var groups = rows
            .Where(r => r.Event is not (WebhookRoute.Installation or WebhookRoute.Organization))
            .GroupBy(r => (r.InstallationId, Repository: r.RepositoryId));
        foreach (var group in groups)
            await HandleAsync(group.ToList(), ct, () => HandleRepositoryAsync(group.Key.InstallationId, group.Key.Repository, ct));
        return rows.Count;
    }

    public async Task DeleteExpiredAsync(CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "delete from webhook_deliveries where processed_at is not null and received_at < now() - @retention",
            new { retention = Retention }, cancellationToken: ct));
    }

    private async Task<List<Row>> ClaimAsync(CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Row>(new CommandDefinition("""
            update webhook_deliveries
               set attempts = attempts + 1, available_at = now() + @lease
             where delivery_id in (select delivery_id from webhook_deliveries
                                    where processed_at is null and available_at <= now()
                                    order by received_at limit 100 for update skip locked)
            returning delivery_id, event, action, installation_id, repository_id, subject_user_id, account_id, attempts
            """, new { lease = Lease }, cancellationToken: ct))).AsList();
    }

    private async Task HandleAsync(List<Row> rows, CancellationToken ct, Func<Task<string>> work)
    {
        try
        {
            await FinishAsync(rows, await work(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var last = rows.Max(r => r.Attempts) >= MaxAttempts;
            logger.LogWarning("Webhook handling failed ({Kind}), attempt {Attempt}{Giveup}", Describe(e), rows.Max(r => r.Attempts),
                last ? ", giving up" : "");
            if (last)
                await FinishAsync(rows, "failed: " + Describe(e), ct);
            else
                await RetryAsync(rows, ct);
        }
    }

    private static string Describe(Exception e) => e switch
    {
        GitHubSourceException g => g.Kind.ToString(),
        GitHubAppKeyException => "app_key",
        HttpRequestException or TaskCanceledException => "github_unavailable",
        _ => "unexpected_error",
    };

    private async Task FinishAsync(List<Row> rows, string result, CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "update webhook_deliveries set processed_at = now(), result = @result where delivery_id = any(@ids)",
            new { result, ids = rows.Select(r => r.DeliveryId).ToArray() }, cancellationToken: ct));
    }

    private async Task RetryAsync(List<Row> rows, CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "update webhook_deliveries set available_at = now() + attempts * interval '30 seconds' where delivery_id = any(@ids)",
            new { ids = rows.Select(r => r.DeliveryId).ToArray() }, cancellationToken: ct));
    }

    private async Task<Connection?> ActiveConnectionAsync(NpgsqlConnection conn, long? installationId, CancellationToken ct) =>
        installationId is null
            ? null
            : await conn.QuerySingleOrDefaultAsync<Connection>(new CommandDefinition("""
                select id, tenant_id, account_id from github_connections
                 where installation_id = @installationId and disconnected_at is null
                """, new { installationId }, cancellationToken: ct));

    private async Task<string> HandleRepositoryAsync(long? installationId, long? repositoryId, CancellationToken ct)
    {
        if (installationId is null || repositoryId is null)
            return "ignored: incomplete";
        if (!app.Value.CanReadInstallations)
            return "ignored: github_app_not_configured";

        Connection? connection;
        SyncRunner.RepoRow? repo;
        bool enabled;
        await using (var conn = await sync.OpenConnectionAsync(ct))
        {
            connection = await ActiveConnectionAsync(conn, installationId, ct);
            if (connection is null)
                return "ignored: unknown_installation";
            var row = await conn.QuerySingleOrDefaultAsync<RepoLookup>(
                new CommandDefinition("""
                    select id, full_name, last_synced_at, sync_enabled from repositories
                     where tenant_id = @tenantId and connection_id = @connectionId and github_repo_id = @repositoryId
                    """, new { tenantId = connection.TenantId, connectionId = connection.Id, repositoryId }, cancellationToken: ct));
            repo = row is null ? null : new SyncRunner.RepoRow(row.Id, row.FullName, row.LastSyncedAt);
            enabled = row?.SyncEnabled ?? false;
        }
        if (repo is null)
            return "ignored: unknown_repository";
        if (!enabled)
            return "ignored: sync_disabled";

        await using var scope = scopes.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<IGitHubSource>();
        await runner.SyncRepositoryAsync(
            source, writer, new GitHubConnectionRef(connection.TenantId, connection.Id, GitHubAuthKind.App, installationId.Value), repo, ct);
        return "synced";
    }

    private async Task<string> HandleAccountEventAsync(Row row, CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        if (row.Event == WebhookRoute.Installation)
            return row.Action switch
            {
                "deleted" or "suspend" => await DisconnectAsync(conn, row.InstallationId, ct),
                "unsuspend" => await ReconnectAsync(conn, row.InstallationId, ct),
                _ => "ignored: action",
            };

        var connection = await ActiveConnectionAsync(conn, row.InstallationId, ct);
        if (connection is null)
            return "ignored: unknown_installation";
        if (row.SubjectUserId is null || connection.AccountId != row.AccountId)
            return "ignored: other_account";
        return await OrgMembers.ApplyEventAsync(
            conn, connection.TenantId, connection.Id, row.SubjectUserId.Value, row.Action == "member_removed", ct);
    }

    private static async Task<string> DisconnectAsync(NpgsqlConnection conn, long? installationId, CancellationToken ct)
    {
        var tenants = (await conn.QueryAsync<Guid>(new CommandDefinition("""
            update github_connections set disconnected_at = now()
             where installation_id = @installationId and disconnected_at is null
            returning tenant_id
            """, new { installationId }, cancellationToken: ct))).AsList();
        await OrgMembers.RecomputeTenantsAsync(conn, tenants, ct);
        return tenants.Count > 0 ? "disconnected" : "ignored: unknown_installation";
    }

    // Only a disconnect that happened to this installation id is undone, and only while the account has no newer active connection.
    private static async Task<string> ReconnectAsync(NpgsqlConnection conn, long? installationId, CancellationToken ct)
    {
        try
        {
            var tenants = (await conn.QueryAsync<Guid>(new CommandDefinition("""
                update github_connections c set disconnected_at = null
                 where c.id = (select id from github_connections
                                where installation_id = @installationId and disconnected_at is not null
                                order by disconnected_at desc limit 1)
                   and not exists (select 1 from github_connections o
                                    where o.disconnected_at is null and (o.installation_id = @installationId or o.account_id = c.account_id))
                returning c.tenant_id
                """, new { installationId }, cancellationToken: ct))).AsList();
            await OrgMembers.RecomputeTenantsAsync(conn, tenants, ct);
            return tenants.Count > 0 ? "reconnected" : "ignored: nothing_to_resume";
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return "ignored: conflict";
        }
    }
}
