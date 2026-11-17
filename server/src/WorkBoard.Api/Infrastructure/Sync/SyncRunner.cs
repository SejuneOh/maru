using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using WorkBoard.Api.Infrastructure.Credentials;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;

namespace WorkBoard.Api.Infrastructure.Sync;

// Runs syncs in the background, at most one per tenant at a time (in this process). Every run is
// recorded in sync_runs; failures are recorded and never thrown.
public sealed class SyncRunner(
    IServiceScopeFactory scopes,
    [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync,
    IHostApplicationLifetime lifetime,
    IOptions<GitHubAppOptions> app,
    CredentialStore credentialStore,
    TimeProvider time,
    ILogger<SyncRunner> logger)
{
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    private const int MaxErrorLength = 300;

    // A token's owner shares GitHub's 5,000 requests per hour with everything else they do. Below this many left the
    // connection pauses until the limit resets.
    public const int LowRateLimitRemaining = 500;
    private static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    // Set by SyncWorker; null while the periodic loop is off.
    public DateTimeOffset? NextRunAt { get; set; }

    public bool IsRunning(Guid tenantId) => _running.ContainsKey(tenantId);

    // Null when a run for the tenant is already in progress.
    public Task? TryStart(Guid tenantId)
    {
        var done = new TaskCompletionSource();
        if (!_running.TryAdd(tenantId, done.Task))
            return null;

        return Task.Run(async () =>
        {
            try
            {
                await RunTenantAsync(tenantId, lifetime.ApplicationStopping);
            }
            catch (Exception e)
            {
                logger.LogError("Sync for tenant {TenantId} crashed: {Error}", tenantId, Detail(e));
            }
            finally
            {
                _running.TryRemove(tenantId, out _);
                done.SetResult();
            }
        });
    }

    public async Task FailInterruptedRunsAsync(CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "update sync_runs set status = 'failed', finished_at = now(), error = 'interrupted' where status = 'running'",
            cancellationToken: ct));
    }

    private sealed record ConnectionRow(Guid Id, long? InstallationId, string AuthKind, string AccountLogin, long AccountId);

    private static GitHubConnectionRef Ref(Guid tenantId, ConnectionRow c) =>
        new(tenantId, c.Id, c.AuthKind == "token" ? GitHubAuthKind.Token : GitHubAuthKind.App, c.InstallationId, c.AccountLogin);

    public sealed record RepoRow(Guid Id, string FullName, DateTime? LastSyncedAt);

    // Reads what changed in one repository since its last sync and applies it through the MirrorWriter.
    // Shared by the periodic run and the webhook worker.
    public async Task<(MirrorResult Result, RateLimitInfo? RateLimit, RateLimitInfo? GraphQlRateLimit)> SyncRepositoryAsync(
        IGitHubSource source, MirrorWriter writer, GitHubConnectionRef connection, RepoRow repo,
        CancellationToken ct)
    {
        var startedAt = time.GetUtcNow();
        var slash = repo.FullName.IndexOf('/');
        DateTimeOffset? since = repo.LastSyncedAt is { } last
            ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) - Overlap
            : null;
        var snapshot = await source.ReadRepositoryAsync(
            connection, repo.FullName[..slash], repo.FullName[(slash + 1)..], since, ct);
        var result = await writer.ApplyAsync(connection.TenantId, connection.ConnectionId, snapshot, ct);
        await MarkSyncedAsync(connection.TenantId, repo.Id, startedAt, since, result);
        return (result, snapshot.RateLimit, snapshot.GraphQlRateLimit);
    }

    private async Task RunTenantAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            await credentialStore.ExpireDueAsync(tenantId, ct);
            await credentialStore.ReleaseDepartedOwnersAsync(tenantId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Token health check for tenant {TenantId} failed: {Error}", tenantId, Detail(e));
        }

        List<ConnectionRow> connections;
        await using (var conn = await sync.OpenConnectionAsync(ct))
        {
            connections = (await conn.QueryAsync<ConnectionRow>(new CommandDefinition("""
                select c.id, c.installation_id, c.auth_kind, c.account_login, c.account_id from github_connections c
                 where c.tenant_id = @tenantId and c.disconnected_at is null
                   and ((c.installation_id is not null and @appReady)
                        or (c.auth_kind = 'token'
                            and (c.sync_not_before is null or c.sync_not_before <= now())
                            and exists (select 1 from github_credentials k
                                         where k.tenant_id = c.tenant_id and k.connection_id = c.id and k.status in ('active', 'invalid')
                                           and (k.expires_at is null or k.expires_at > now()))))
                 order by c.created_at
                """, new { tenantId, appReady = app.Value.CanReadInstallations }, cancellationToken: ct))).AsList();
        }

        foreach (var connection in connections)
            await RunConnectionAsync(tenantId, connection, ct);
    }

    private async Task RunConnectionAsync(Guid tenantId, ConnectionRow connection, CancellationToken ct)
    {
        var runId = await StartRunAsync(tenantId, connection.Id);
        var (issues, pullRequests, comments) = (0, 0, 0);
        RateLimitInfo? rest = null, graphQl = null;
        DateTimeOffset? pauseUntil = null;
        var isToken = connection.AuthKind == "token";
        var errors = new List<string>();

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var source = scope.ServiceProvider.GetRequiredService<IGitHubSource>();
            var writer = scope.ServiceProvider.GetRequiredService<MirrorWriter>();

            if (isToken)
            {
                var (stopped, current) = await CheckOwnerAsync(scope.ServiceProvider, tenantId, connection, ct);
                connection = current;
                if (stopped is not null)
                    errors.Add(stopped);
            }

            if (errors.Count == 0)
            {
                var list = await source.ListRepositoriesAsync(Ref(tenantId, connection), ct);
                rest = list.RateLimit ?? rest;
                await writer.UpsertRepositoriesAsync(tenantId, connection.Id, list.Repositories, ct);

                var repos = await EnabledRepositoriesAsync(tenantId, connection.Id, list.Repositories.Select(r => r.Id).ToArray(), ct);
                foreach (var repo in repos)
                {
                    if (isToken && IsLow(rest, graphQl))
                        break;
                    try
                    {
                        var (result, left, leftGraphQl) = await SyncRepositoryAsync(
                            source, writer, Ref(tenantId, connection), repo, ct);
                        rest = left ?? rest;
                        graphQl = leftGraphQl ?? graphQl;
                        issues += result.Issues;
                        pullRequests += result.PullRequests;
                        comments += result.Comments;
                    }
                    catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        logger.LogWarning("Sync of {Repository} failed: {Error}", repo.FullName, Detail(e));
                        errors.Add($"{repo.FullName}: {Describe(e)}");
                        if (e is GitHubSourceException g)
                            pauseUntil = PauseFor(g);
                        if (e is GitHubSourceException
                            { Kind: GitHubSourceErrorKind.RateLimited or GitHubSourceErrorKind.Unauthorized
                                or GitHubSourceErrorKind.CredentialRevoked or GitHubSourceErrorKind.CredentialInvalid
                                or GitHubSourceErrorKind.CredentialExpired })
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            errors.Add("interrupted");
        }
        catch (Exception e)
        {
            logger.LogWarning("Sync of connection {ConnectionId} failed: {Error}", connection.Id, Detail(e));
            errors.Add(Describe(e));
            if (e is GitHubSourceException g)
                pauseUntil = PauseFor(g);
        }

        if (isToken)
        {
            if (IsLow(rest, graphQl))
                pauseUntil ??= LowResetAt(rest, graphQl) ?? time.GetUtcNow() + DefaultPause;
            if (pauseUntil is { } until)
                await PauseAsync(tenantId, connection.Id, until);
        }

        var remaining = (rest?.Remaining, graphQl?.Remaining) switch
        {
            ({ } r, { } g) => Math.Min(r, g),
            ({ } r, null) => r,
            (null, { } g) => g,
            _ => (int?)null,
        };
        await FinishRunAsync(runId, errors, issues, pullRequests, comments, remaining);
    }

    // GitHub keeps separate buckets for REST and GraphQL. Either one running low pauses the connection, because a run
    // uses both.
    private static bool IsLow(params RateLimitInfo?[] buckets) =>
        buckets.Any(b => b?.Remaining < LowRateLimitRemaining);

    private static DateTimeOffset? LowResetAt(params RateLimitInfo?[] buckets) =>
        buckets.Where(b => b?.Remaining < LowRateLimitRemaining && b.ResetAt is not null).Max(b => b!.ResetAt);

    private DateTimeOffset? PauseFor(GitHubSourceException e) =>
        e.Kind == GitHubSourceErrorKind.RateLimited ? e.RetryAt ?? time.GetUtcNow() + DefaultPause : null;

    // At least a minute and at most an hour from now, so a reset time in the past or far away cannot stall or spin.
    private async Task PauseAsync(Guid tenantId, Guid connectionId, DateTimeOffset until)
    {
        await using var conn = await sync.OpenConnectionAsync();
        await conn.ExecuteAsync("""
            update github_connections
               set sync_not_before = least(greatest(@until, now() + interval '1 minute'), now() + interval '1 hour')
             where tenant_id = @tenantId and id = @connectionId
            """, new { tenantId, connectionId, until = until.UtcDateTime });
    }

    private async Task<ConnectionRow> RefreshLoginAsync(Guid tenantId, ConnectionRow connection, string login, CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "update github_connections set account_login = @login where tenant_id = @tenantId and id = @id",
            new { tenantId, id = connection.Id, login }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into audit_log (tenant_id, action, target_type, target_id, after)
            values (@tenantId, 'connection.org_renamed', 'connection', @id, cast(@after as jsonb))
            """, new { tenantId, id = connection.Id, after = JsonSerializer.Serialize(new { from = connection.AccountLogin, to = login }) },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return connection with { AccountLogin = login };
    }

    // Asks GitHub, with the stored token, whether its owner is still an active member of the organization. Stop is
    // null when the connection may sync; otherwise the reason it stopped. The organization is first looked up by its
    // stored id, so a renamed organization updates account_login instead of looking like lost membership.
    //
    // "Not a member" and "membership not active" take two checks in a row for the same token: the first marks the
    // token invalid (kept, sync stops), the next one that says the same deletes it (token needed). A passing check in
    // between makes the token active again and resets the count. A rejected token (401) is revoked at once. Every
    // transition applies only while the stored token is still the one that was checked.
    private async Task<(string? Stop, ConnectionRow Connection)> CheckOwnerAsync(
        IServiceProvider services, Guid tenantId, ConnectionRow connection, CancellationToken ct)
    {
        var credentials = services.GetRequiredService<IGitHubCredentials>();
        var github = services.GetRequiredService<IGitHubTokenClient>();
        var reference = Ref(tenantId, connection);
        var target = await credentialStore.OpenForCheckAsync(tenantId, connection.Id, ct)
            ?? throw new GitHubSourceException(GitHubSourceErrorKind.Unauthorized, "The connection has no usable credential.");
        var token = target.Token;

        if (await github.GetOrganizationByIdAsync(token, connection.AccountId, ct) is { } org
            && org.Id == connection.AccountId
            && !string.Equals(org.Login, connection.AccountLogin, StringComparison.Ordinal))
            connection = await RefreshLoginAsync(tenantId, connection, org.Login, ct);

        var access = await github.GetOrganizationAccessAsync(token, connection.AccountLogin, ct);
        if (access is null)
        {
            await credentials.ReportRejectedAsync(reference, token, ct);
            return ("credential_revoked: GitHub rejected the stored token", connection);
        }

        switch (access.Status)
        {
            case OrgAccessStatus.Active:
                if (target.Status == "invalid")
                    await credentialStore.ReactivateAsync(tenantId, connection.Id, token, ct);
                return (null, connection);
            case OrgAccessStatus.NotMember or OrgAccessStatus.NotActive:
                var reason = access.Status == OrgAccessStatus.NotMember ? "owner_not_member" : "owner_not_active";
                if (target.Status == "invalid" && target.InvalidReason is "owner_not_member" or "owner_not_active")
                {
                    await credentialStore.ReleaseAsync(tenantId, connection.Id, token, "owner_left_organization", ct);
                    return ("token_required: the token owner is no longer an active member of the organization", connection);
                }
                await credentialStore.MarkInvalidAsync(tenantId, connection.Id, token, reason, ct);
                return ($"credential_invalid: {reason}, the token is deleted if the next check says the same", connection);
            default:
                await credentialStore.MarkInvalidAsync(tenantId, connection.Id, token,
                    access.Status == OrgAccessStatus.SsoRequired ? "sso_not_authorized" : "org_access_denied", ct);
                return ("credential_invalid: GitHub refuses the token for the organization", connection);
        }
    }

    // Type, message and stack for the log, with anything shaped like a token masked.
    private static string Detail(Exception e) => TokenMasker.Apply(e.ToString())!;

    private static string Describe(Exception e) => TokenMasker.Apply(e switch
    {
        GitHubSourceException g => $"{g.Kind}: {g.Message}",
        GitHubAppKeyException g => g.Message,
        CredentialKeyException g => g.Message,
        HttpRequestException or TaskCanceledException => "github_unavailable",
        _ => "unexpected_error",
    })!;

    private async Task<Guid> StartRunAsync(Guid tenantId, Guid connectionId)
    {
        await using var conn = await sync.OpenConnectionAsync();
        return await conn.QuerySingleAsync<Guid>(
            "insert into sync_runs (tenant_id, connection_id) values (@tenantId, @connectionId) returning id",
            new { tenantId, connectionId });
    }

    private async Task<List<RepoRow>> EnabledRepositoriesAsync(Guid tenantId, Guid connectionId, long[] githubIds, CancellationToken ct)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<RepoRow>(new CommandDefinition("""
            select id, full_name, last_synced_at from repositories
             where tenant_id = @tenantId and connection_id = @connectionId and sync_enabled and github_repo_id = any(@githubIds)
             order by full_name
            """, new { tenantId, connectionId, githubIds }, cancellationToken: ct))).AsList();
    }

    private async Task MarkSyncedAsync(Guid tenantId, Guid repositoryId, DateTimeOffset startedAt, DateTimeOffset? since, MirrorResult result)
    {
        var cursor = JsonSerializer.Serialize(new
        {
            since,
            syncedAt = startedAt,
            result.Issues,
            result.PullRequests,
            result.Comments,
        });
        await using var conn = await sync.OpenConnectionAsync();
        await conn.ExecuteAsync(
            "update repositories set last_synced_at = @syncedAt, sync_cursor = @cursor::jsonb where tenant_id = @tenantId and id = @repositoryId",
            new { tenantId, repositoryId, syncedAt = startedAt.UtcDateTime, cursor });
    }

    private async Task FinishRunAsync(
        Guid runId, List<string> errors, int issues, int pullRequests, int comments, int? remaining)
    {
        var error = errors.Count == 0 ? null : TokenMasker.Apply(string.Join("; ", errors));
        if (error is { Length: > MaxErrorLength })
            error = error[..MaxErrorLength];
        await using var conn = await sync.OpenConnectionAsync();
        await conn.ExecuteAsync("""
            update sync_runs
               set finished_at = now(), status = @status, issues_synced = @issues, pull_requests_synced = @pullRequests,
                   comments_synced = @comments, rate_limit_remaining = @remaining, error = @error
             where id = @runId
            """, new
        {
            runId, status = error is null ? "ok" : "failed", issues, pullRequests, comments, remaining, error,
        });
    }
}
