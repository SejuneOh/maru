using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Infrastructure.Webhooks;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class WebhookTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private sealed class FakeSource : IGitHubSource
    {
        public Func<SourceRepository, RepositorySnapshot>? Snapshot { get; set; }
        public Exception? ThrowOnRead { get; set; }
        public List<(string Name, DateTimeOffset? Since)> Reads { get; } = [];
        public SourceRepository? Repository { get; set; }

        public Task<RepositoryList> ListRepositoriesAsync(GitHubConnectionRef connection, CancellationToken ct) =>
            Task.FromResult(new RepositoryList([], null));

        public Task<RepositorySnapshot> ReadRepositoryAsync(
            GitHubConnectionRef connection, string owner, string name, DateTimeOffset? since, CancellationToken ct)
        {
            lock (Reads)
                Reads.Add(($"{owner}/{name}", since));
            if (ThrowOnRead is { } e)
                throw e;
            var repo = Repository!;
            return Task.FromResult(Snapshot?.Invoke(repo) ?? new RepositorySnapshot(repo, [], [], [], [], [], null));
        }
    }

    private readonly WebApplicationFactory<Program> _factory;

    public WebhookTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private (HttpClient Client, WebApplicationFactory<Program> Host) Host(FakeSource? fake = null, string? secret = null, string? login = null, bool syncEnabled = true)
    {
        var host = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:App:AppId"] = "123",
                ["GitHub:App:PrivateKeyPath"] = "unused.pem",
                ["GitHub:App:WebhookSecret"] = secret ?? Secret,
                ["Sync:Enabled"] = syncEnabled ? "true" : "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IGitHubSource>();
                s.AddSingleton<IGitHubSource>(fake ?? new FakeSource());
                s.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            });
        });
        var client = host.CreateClient();
        if (login is not null)
            client = client.WithDevUser(login);
        return (client, host);
    }

    private static string Sign(string secret, byte[] body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string eventName, object payload, string? delivery = null, string? secret = null, string? signature = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhook") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-GitHub-Delivery", delivery ?? Guid.NewGuid().ToString());
        if (signature is not "" )
            request.Headers.Add("X-Hub-Signature-256", signature ?? Sign(secret ?? Secret, body));
        return await client.SendAsync(request);
    }

    private static async Task<string?> StatusAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString();

    private static async Task<long> InstallAsync(TestTenant t)
    {
        var id = TestTenant.NextId();
        await t.ExecAsync("update github_connections set installation_id = @id where id = @c", new { id, c = t.ConnectionId });
        return id;
    }

    private static async Task<(Guid Id, long GithubId, string FullName)> RepoAsync(TestTenant t, string name)
    {
        var id = await t.AddRepoAsync(name);
        var fullName = $"org-{t.Suffix}/{name}";
        await t.ExecAsync("update repositories set full_name = @fullName where id = @id", new { id, fullName });
        return (id, await t.ScalarAsync<long>("select github_repo_id from repositories where id = @id", new { id }), fullName);
    }

    private static object IssuePayload(long installation, long repositoryId, string action = "edited") =>
        new { action, installation = new { id = installation }, repository = new { full_name = "renamed/elsewhere", id = repositoryId } };

    private static SourceIssue Issue(long id, int number, DateTimeOffset updated, string title) =>
        new(id, "n" + id, number, title, null, null, "open", null, null, T0, null, updated, "https://x/" + number, null, [], [], null, null);

    private Task<int> DrainAsync(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<WebhookProcessor>().ProcessPendingAsync(CancellationToken.None);

    private static Task<long> RowsAsync(TestTenant t, string delivery) =>
        t.ScalarAsync<long>("select count(*) from webhook_deliveries where delivery_id = @delivery", new { delivery });

    [DbTheory]
    [InlineData("wrong")]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task A_bad_signature_is_rejected_and_nothing_is_stored(string kind)
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (client, _) = Host();
        var delivery = Guid.NewGuid().ToString();
        var payload = IssuePayload(1, 1);

        var response = await PostAsync(client, "issues", payload, delivery,
            signature: kind switch { "wrong" => Sign("another-secret", JsonSerializer.SerializeToUtf8Bytes(payload)), "missing" => "", _ => "sha256=zz" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await RowsAsync(t, delivery));
    }

    [DbFact]
    public async Task A_signature_over_a_different_body_is_rejected()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (client, _) = Host();
        var delivery = Guid.NewGuid().ToString();
        var signed = JsonSerializer.SerializeToUtf8Bytes(IssuePayload(1, 1, "opened"));

        var response = await PostAsync(client, "issues", IssuePayload(1, 1, "closed"), delivery, signature: Sign(Secret, signed));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await RowsAsync(t, delivery));
    }

    [DbFact]
    public async Task Without_a_configured_secret_the_endpoint_is_503()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (client, _) = Host(secret: " ");
        var delivery = Guid.NewGuid().ToString();

        var response = await PostAsync(client, "issues", IssuePayload(1, 1), delivery, secret: " ");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await RowsAsync(t, delivery));
    }

    [DbFact]
    public async Task An_oversized_body_is_rejected_before_it_is_stored()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (client, _) = Host();
        var delivery = Guid.NewGuid().ToString();

        var response = await PostAsync(client, "issues", new { action = "edited", pad = new string('a', 2 * 1024 * 1024) }, delivery);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await RowsAsync(t, delivery));
    }

    [DbFact]
    public async Task Ping_and_unsupported_events_are_acknowledged_without_a_row()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (client, _) = Host();
        var push = Guid.NewGuid().ToString();
        var created = Guid.NewGuid().ToString();

        var ping = await PostAsync(client, "ping", new { zen = "x" });
        var other = await PostAsync(client, "push", new { installation = new { id = 1 } }, push);
        var installed = await PostAsync(client, "installation", new { action = "created", installation = new { id = 1 } }, created);

        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
        Assert.Equal("ignored", await StatusAsync(other));
        Assert.Equal("ignored", await StatusAsync(installed));
        Assert.Equal(0, await RowsAsync(t, push) + await RowsAsync(t, created));
    }

    [DbFact]
    public async Task A_duplicate_delivery_is_applied_once()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var repo = await RepoAsync(t, "api");
        var fake = new FakeSource { Repository = new SourceRepository(repo.GithubId, "api", repo.FullName, null, false) };
        var (client, host) = Host(fake);
        var delivery = Guid.NewGuid().ToString();
        var payload = IssuePayload(installation, repo.GithubId);

        var first = await PostAsync(client, "issues", payload, delivery);
        var second = await PostAsync(client, "issues", payload, delivery);
        await DrainAsync(host);
        var third = await PostAsync(client, "issues", payload, delivery);
        var drained = await DrainAsync(host);

        Assert.Equal("queued", await StatusAsync(first));
        Assert.Equal("duplicate", await StatusAsync(second));
        Assert.Equal("duplicate", await StatusAsync(third));
        Assert.Equal(0, drained);
        Assert.Equal(1, await RowsAsync(t, delivery));
        Assert.Single(fake.Reads);
    }

    [DbFact]
    public async Task An_issue_event_reads_the_repository_and_mirrors_the_issue()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var repo = await RepoAsync(t, "api");
        var issueId = TestTenant.NextId();
        var fake = new FakeSource
        {
            Repository = new SourceRepository(repo.GithubId, "api", repo.FullName, null, false),
            Snapshot = r => new RepositorySnapshot(r, [], [], [Issue(issueId, 7, T0, "from webhook")], [], [], null),
        };
        var (client, host) = Host(fake);
        var delivery = Guid.NewGuid().ToString();

        await PostAsync(client, "issues", IssuePayload(installation, repo.GithubId, "opened"), delivery);
        await DrainAsync(host);

        Assert.Equal("from webhook", await t.ScalarAsync<string>("select title from issues where github_issue_id = @issueId", new { issueId }));
        Assert.Equal("synced", await t.ScalarAsync<string>("select result from webhook_deliveries where delivery_id = @delivery", new { delivery }));
        Assert.NotNull(await t.ScalarAsync<DateTime?>("select last_synced_at from repositories where id = @id", new { id = repo.Id }));
    }

    [DbFact]
    public async Task An_older_copy_from_a_webhook_never_overwrites_a_newer_one()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var repo = await RepoAsync(t, "api");
        var issueId = TestTenant.NextId();
        var updated = T0.AddHours(2);
        var fake = new FakeSource
        {
            Repository = new SourceRepository(repo.GithubId, "api", repo.FullName, null, false),
            Snapshot = r => new RepositorySnapshot(r, [], [], [Issue(issueId, 7, updated, "newer")], [], [], null),
        };
        var (client, host) = Host(fake);
        await PostAsync(client, "issues", IssuePayload(installation, repo.GithubId), Guid.NewGuid().ToString());
        await DrainAsync(host);

        updated = T0;
        fake.Snapshot = r => new RepositorySnapshot(r, [], [], [Issue(issueId, 7, T0, "older")], [], [], null);
        await PostAsync(client, "issues", IssuePayload(installation, repo.GithubId), Guid.NewGuid().ToString());
        await DrainAsync(host);

        Assert.Equal("newer", await t.ScalarAsync<string>("select title from issues where github_issue_id = @issueId", new { issueId }));
    }

    [DbFact]
    public async Task Several_events_for_one_repository_share_one_read()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var repo = await RepoAsync(t, "api");
        var fake = new FakeSource { Repository = new SourceRepository(repo.GithubId, "api", repo.FullName, null, false) };
        var (client, host) = Host(fake);

        foreach (var kind in new[] { "issues", "issue_comment", "pull_request", "pull_request_review", "milestone" })
            await PostAsync(client, kind, IssuePayload(installation, repo.GithubId));
        await DrainAsync(host);

        Assert.Single(fake.Reads);
    }

    [DbFact]
    public async Task An_unknown_installation_is_acknowledged_and_ignored()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var fake = new FakeSource();
        var (client, host) = Host(fake);
        var delivery = Guid.NewGuid().ToString();

        var response = await PostAsync(client, "issues", IssuePayload(TestTenant.NextId(), 1), delivery);
        await DrainAsync(host);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(fake.Reads);
        Assert.Equal("ignored: unknown_installation",
            await t.ScalarAsync<string>("select result from webhook_deliveries where delivery_id = @delivery", new { delivery }));
    }

    [DbFact]
    public async Task A_failed_read_is_retried_and_given_up_after_the_last_attempt()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var repo = await RepoAsync(t, "api");
        var fake = new FakeSource
        {
            Repository = new SourceRepository(repo.GithubId, "api", repo.FullName, null, false),
            ThrowOnRead = new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "boom"),
        };
        var (client, host) = Host(fake);
        var delivery = Guid.NewGuid().ToString();
        await PostAsync(client, "issues", IssuePayload(installation, repo.GithubId), delivery);

        for (var i = 0; i < WebhookProcessor.MaxAttempts; i++)
        {
            Assert.Equal(1, await DrainAsync(host));
            if (i < WebhookProcessor.MaxAttempts - 1)
            {
                Assert.Null(await t.ScalarAsync<DateTime?>("select processed_at from webhook_deliveries where delivery_id = @delivery", new { delivery }));
                await t.ExecAsync("update webhook_deliveries set available_at = now() where delivery_id = @delivery", new { delivery });
            }
        }

        Assert.Equal("failed: Unexpected", await t.ScalarAsync<string>("select result from webhook_deliveries where delivery_id = @delivery", new { delivery }));
    }

    [DbFact]
    public async Task Installation_deleted_marks_the_connection_disconnected_and_stops_sync()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var (admin, _) = await t.AddMemberAsync("admin");
        await t.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_login = @admin", new { tid = t.Id, admin });
        var (client, host) = Host(login: admin);

        await PostAsync(client, "installation", new { action = "deleted", installation = new { id = installation } });
        await DrainAsync(host);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/connections");
        Assert.NotEqual(JsonValueKind.Null, list[0].GetProperty("disconnectedAt").ValueKind);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/sync/status");
        Assert.False(status.GetProperty("connected").GetBoolean());
        var run = await client.PostAsync("/api/sync/run", null);
        Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
    }

    [DbFact]
    public async Task Repository_events_of_a_disconnected_installation_are_ignored()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var repo = await RepoAsync(t, "api");
        var fake = new FakeSource { Repository = new SourceRepository(repo.GithubId, "api", repo.FullName, null, false) };
        var (client, host) = Host(fake);
        await PostAsync(client, "installation", new { action = "suspend", installation = new { id = installation } });
        await PostAsync(client, "issues", IssuePayload(installation, repo.GithubId));

        await DrainAsync(host);

        Assert.Empty(fake.Reads);
    }

    [DbFact]
    public async Task Unsuspend_reconnects_the_suspended_installation()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var (client, host) = Host();

        await PostAsync(client, "installation", new { action = "suspend", installation = new { id = installation } });
        await DrainAsync(host);
        Assert.NotNull(await t.ScalarAsync<DateTime?>("select disconnected_at from github_connections where id = @c", new { c = t.ConnectionId }));

        await PostAsync(client, "installation", new { action = "unsuspend", installation = new { id = installation } });
        await DrainAsync(host);

        Assert.Null(await t.ScalarAsync<DateTime?>("select disconnected_at from github_connections where id = @c", new { c = t.ConnectionId }));
    }

    [DbFact]
    public async Task Member_removed_marks_the_member_and_member_added_clears_it()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var (login, userId) = await t.AddMemberAsync("leaver");
        var other = await t.AddMemberAsync("stays");
        var accountId = await t.ScalarAsync<long>("select account_id from github_connections where id = @c", new { c = t.ConnectionId });
        var (client, host) = Host(login: t.ViewerLogin);
        object Payload(string action, long org) => new
        {
            action, installation = new { id = installation }, organization = new { id = org },
            membership = new { user = new { id = userId } },
        };

        await PostAsync(client, "organization", Payload("member_removed", accountId + 1));
        await DrainAsync(host);
        var untouched = await client.GetFromJsonAsync<JsonElement>("/api/members");
        Assert.All(untouched.EnumerateArray(), m => Assert.Equal(JsonValueKind.Null, m.GetProperty("removedAt").ValueKind));

        await PostAsync(client, "organization", Payload("member_removed", accountId));
        await DrainAsync(host);
        var removed = await client.GetFromJsonAsync<JsonElement>("/api/members");
        Assert.NotEqual(JsonValueKind.Null, removed.EnumerateArray().Single(m => m.GetProperty("githubLogin").GetString() == login).GetProperty("removedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, removed.EnumerateArray().Single(m => m.GetProperty("githubLogin").GetString() == other.Login).GetProperty("removedAt").ValueKind);

        await PostAsync(client, "organization", Payload("member_added", accountId));
        await DrainAsync(host);
        var back = await client.GetFromJsonAsync<JsonElement>("/api/members");
        Assert.Equal(JsonValueKind.Null, back.EnumerateArray().Single(m => m.GetProperty("githubLogin").GetString() == login).GetProperty("removedAt").ValueKind);
    }

    [DbFact]
    public async Task Removal_from_one_organization_does_not_mark_someone_still_in_another()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var accountId = await t.ScalarAsync<long>("select account_id from github_connections where id = @c", new { c = t.ConnectionId });
        var second = Guid.NewGuid();
        var secondAccount = TestTenant.NextId();
        var secondInstallation = TestTenant.NextId();
        await t.ExecAsync("""
            insert into github_connections (id, tenant_id, account_id, account_login, account_type, installation_id)
            values (@second, @tid, @secondAccount, 'second-org', 'organization', @secondInstallation)
            """, new { second, tid = t.Id, secondAccount, secondInstallation });
        var (login, userId) = await t.AddMemberAsync("both");
        await t.ExecAsync("""
            insert into github_org_members (tenant_id, connection_id, github_user_id, github_login)
            values (@tid, @first, @userId, @login), (@tid, @second, @userId, @login)
            """, new { tid = t.Id, first = t.ConnectionId, second, userId, login });
        var (client, host) = Host(login: t.ViewerLogin);
        object Payload(string action, long installationId, long org) => new
        {
            action, installation = new { id = installationId }, organization = new { id = org },
            membership = new { user = new { id = userId } },
        };
        async Task<JsonValueKind> RemovedAsync()
        {
            var list = await client.GetFromJsonAsync<JsonElement>("/api/members");
            return list.EnumerateArray().Single(m => m.GetProperty("githubLogin").GetString() == login).GetProperty("removedAt").ValueKind;
        }

        await PostAsync(client, "organization", Payload("member_removed", installation, accountId));
        await DrainAsync(host);
        Assert.Equal(JsonValueKind.Null, await RemovedAsync());
        Assert.NotNull(await t.ScalarAsync<DateTime?>(
            "select removed_at from github_org_members where connection_id = @c and github_user_id = @userId",
            new { c = t.ConnectionId, userId }));

        await PostAsync(client, "organization", Payload("member_removed", secondInstallation, secondAccount));
        await DrainAsync(host);
        Assert.Equal(JsonValueKind.String, await RemovedAsync());

        await PostAsync(client, "organization", Payload("member_added", secondInstallation, secondAccount));
        await DrainAsync(host);
        Assert.Equal(JsonValueKind.Null, await RemovedAsync());
    }

    [DbFact]
    public async Task Disconnecting_the_organization_stops_hiding_people_it_removed_and_resuming_restores_it()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var installation = await InstallAsync(t);
        var accountId = await t.ScalarAsync<long>("select account_id from github_connections where id = @c", new { c = t.ConnectionId });
        var (login, userId) = await t.AddMemberAsync("leaver");
        var (client, host) = Host(login: t.ViewerLogin);
        async Task<JsonValueKind> RemovedAsync()
        {
            var list = await client.GetFromJsonAsync<JsonElement>("/api/members");
            return list.EnumerateArray().Single(m => m.GetProperty("githubLogin").GetString() == login).GetProperty("removedAt").ValueKind;
        }

        var removal = new
        {
            action = "member_removed", installation = new { id = installation }, organization = new { id = accountId },
            membership = new { user = new { id = userId } },
        };
        await PostAsync(client, "organization", removal);
        await DrainAsync(host);
        Assert.Equal(JsonValueKind.String, await RemovedAsync());
        var repeat = Guid.NewGuid().ToString();
        await PostAsync(client, "organization", removal, repeat);
        await DrainAsync(host);
        Assert.Equal("unchanged", await t.ScalarAsync<string>(
            "select result from webhook_deliveries where delivery_id = @repeat", new { repeat }));

        await PostAsync(client, "installation", new { action = "suspend", installation = new { id = installation } });
        await DrainAsync(host);
        Assert.Equal(JsonValueKind.Null, await RemovedAsync());

        await PostAsync(client, "installation", new { action = "unsuspend", installation = new { id = installation } });
        await DrainAsync(host);
        Assert.Equal(JsonValueKind.String, await RemovedAsync());

        await PostAsync(client, "installation", new { action = "deleted", installation = new { id = installation } });
        await DrainAsync(host);
        Assert.Equal(JsonValueKind.Null, await RemovedAsync());
    }

    [DbFact]
    public async Task While_sync_is_disabled_the_endpoint_does_not_queue()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (client, _) = Host(syncEnabled: false);
        var delivery = Guid.NewGuid().ToString();

        var response = await PostAsync(client, "issues", IssuePayload(1, 1), delivery);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await RowsAsync(t, delivery));
    }

    private static SyncWorker Worker(WebApplicationFactory<Program> host, int? intervalMinutes = null)
    {
        var s = host.Services;
        var options = Microsoft.Extensions.Options.Options.Create(new SyncOptions { IntervalMinutes = intervalMinutes });
        return new SyncWorker(
            s.GetRequiredService<SyncRunner>(), s.GetRequiredService<SprintDailyRecorder>(), options,
            s.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubAppOptions>>(),
            s.GetRequiredKeyedService<Npgsql.NpgsqlDataSource>(WorkBoard.Api.Infrastructure.Identity.DataKeys.Sync),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncWorker>.Instance);
    }

    [DbFact]
    public async Task The_sync_interval_is_long_only_while_deliveries_arrive()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (_, host) = Host();
        var delivery = Guid.NewGuid().ToString();
        await t.ExecAsync("delete from webhook_deliveries");

        Assert.Equal(TimeSpan.FromMinutes(10), await Worker(host).IntervalAsync(CancellationToken.None));

        await t.ExecAsync("insert into webhook_deliveries (delivery_id, event, processed_at) values (@delivery, 'issues', now())", new { delivery });
        Assert.Equal(TimeSpan.FromMinutes(60), await Worker(host).IntervalAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(5), await Worker(host, 5).IntervalAsync(CancellationToken.None));

        await t.ExecAsync("update webhook_deliveries set received_at = now() - interval '31 minutes' where delivery_id = @delivery", new { delivery });
        Assert.Equal(TimeSpan.FromMinutes(10), await Worker(host).IntervalAsync(CancellationToken.None));
        await t.ExecAsync("delete from webhook_deliveries where delivery_id = @delivery", new { delivery });
    }

    [DbFact]
    public async Task Old_processed_deliveries_are_deleted_and_pending_ones_kept()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (_, host) = Host();
        var old = Guid.NewGuid().ToString();
        var pending = Guid.NewGuid().ToString();
        await t.ExecAsync("""
            insert into webhook_deliveries (delivery_id, event, received_at, processed_at) values (@old, 'issues', now() - interval '20 days', now() - interval '20 days');
            insert into webhook_deliveries (delivery_id, event, received_at, available_at) values (@pending, 'issues', now() - interval '20 days', now() + interval '1 day');
            """, new { old, pending });

        await host.Services.GetRequiredService<WebhookProcessor>().DeleteExpiredAsync(CancellationToken.None);

        Assert.Equal(0, await RowsAsync(t, old));
        Assert.Equal(1, await RowsAsync(t, pending));
        await t.ExecAsync("delete from webhook_deliveries where delivery_id = @pending", new { pending });
    }
}
