using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using WorkBoard.Api.Infrastructure.Credentials;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Writes;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class TokenHealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Replacement = "ghp_ZZZZZZZZZZ9876543210zyxwvutsrqponmlk";

    private sealed class FakeSource : IGitHubSource
    {
        public List<SourceRepository> Repositories { get; set; } = [];
        public RateLimitInfo? Limit { get; set; } = new(4900, null);
        public List<string> Reads { get; } = [];
        public int Lists;

        public Task<RepositoryList> ListRepositoriesAsync(GitHubConnectionRef connection, CancellationToken ct)
        {
            Interlocked.Increment(ref Lists);
            return Task.FromResult(new RepositoryList(Repositories, Limit));
        }

        public Task<RepositorySnapshot> ReadRepositoryAsync(
            GitHubConnectionRef connection, string owner, string name, DateTimeOffset? since, CancellationToken ct)
        {
            lock (Reads)
                Reads.Add($"{owner}/{name}");
            var repo = Repositories.Single(r => r.FullName == $"{owner}/{name}");
            return Task.FromResult(new RepositorySnapshot(repo, [], [], [], [], [], Limit));
        }
    }

    private readonly WebApplicationFactory<Program> _factory;

    public TokenHealthTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // Disposing releases the tenant and the extra hosts, so their connection pools do not pile up across tests.
    private sealed record Setup(
        TestTenant T, string Login, long UserId, FakeSource Source, FakeMembership Membership, HttpClient Client,
        List<IDisposable> Owned) : IAsyncDisposable
    {
        public HttpClient Other(string login, IGitHubSource? source = null, IGitHubTokenClient? membership = null,
            WebApplicationFactory<Program>? factory = null) =>
            TokenFakes.Client(factory ?? Factory!, login, source ?? Source, membership ?? Membership, owned: Owned);

        public WebApplicationFactory<Program>? Factory { get; init; }

        public async ValueTask DisposeAsync()
        {
            foreach (var host in Owned)
                host.Dispose();
            await T.DisposeAsync();
        }
    }

    private async Task<Setup> SetupAsync(DateTimeOffset? expires = null, string status = "active", bool credential = true, bool app = false)
    {
        var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await t.AddMemberAsync("admin");
        await t.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_user_id = @userId", new { tid = t.Id, userId });
        if (app)
            await t.ExecAsync("update github_connections set installation_id = @i where id = @id", new { i = TestTenant.NextId(), id = t.ConnectionId });
        else
            await TokenFakes.MakeTokenConnectionAsync(t);
        if (credential && !app)
            await TokenFakes.AddCredentialAsync(_factory, t, userId, expires, status);

        var repo = new SourceRepository(TestTenant.NextId(), "api", $"org-{t.Suffix}/api", "https://x/api", false);
        await t.ExecAsync("insert into repositories (tenant_id, connection_id, github_repo_id, name, full_name, sync_enabled) values (@tid, @cid, @gid, 'api', @full, true)",
            new { tid = t.Id, cid = t.ConnectionId, gid = repo.Id, full = repo.FullName });
        var source = new FakeSource { Repositories = [repo] };
        var membership = new FakeMembership
        {
            OrgId = await t.ScalarAsync<long>("select account_id from github_connections where id = @id", new { id = t.ConnectionId }),
        };
        var owned = new List<IDisposable>();
        var client = TokenFakes.Client(_factory, login, source, membership, appReady: app, owned: owned);
        return new Setup(t, login, userId, source, membership, client, owned) { Factory = _factory };
    }

    private static async Task RunSyncAsync(Setup s)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await s.Client.PostAsync("/api/sync/run", null)).StatusCode);
        await TokenFakes.WaitSyncIdleAsync(s.Client);
    }

    private static async Task<JsonElement> ConnectionAsync(Setup s) =>
        (await s.Client.GetFromJsonAsync<JsonElement>("/api/connections")).EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == s.T.ConnectionId);

    private static async Task<string> AuditAsync(TestTenant t) =>
        string.Join("\n", (await t.QueryAsync("select action || ' ' || coalesce(after::text, '') as line from audit_log where tenant_id = @tid order by created_at",
            new { tid = t.Id })).Select(r => (string)r.line));

    // ---- expiry --------------------------------------------------------------------------------

    [DbFact]
    public async Task Connections_list_warns_from_seven_days_before_expiry()
    {
        var soon = await SetupAsync(DateTimeOffset.UtcNow.AddDays(5));
        await using var _ = soon;
        var later = await SetupAsync(DateTimeOffset.UtcNow.AddDays(30));
        await using var __ = later;
        var never = await SetupAsync();
        await using var ___ = never;

        var a = await ConnectionAsync(soon);
        Assert.True(a.GetProperty("tokenExpiresSoon").GetBoolean());
        Assert.Equal(5, a.GetProperty("tokenDaysLeft").GetInt32());
        Assert.Equal("active", a.GetProperty("tokenStatus").GetString());

        var b = await ConnectionAsync(later);
        Assert.False(b.GetProperty("tokenExpiresSoon").GetBoolean());
        Assert.Equal(30, b.GetProperty("tokenDaysLeft").GetInt32());

        var c = await ConnectionAsync(never);
        Assert.False(c.GetProperty("tokenExpiresSoon").GetBoolean());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("tokenDaysLeft").ValueKind);

        var status = await soon.Client.GetFromJsonAsync<JsonElement>("/api/sync/status");
        var token = Assert.Single(status.GetProperty("tokens").EnumerateArray());
        Assert.True(token.GetProperty("tokenExpiresSoon").GetBoolean());
        Assert.Equal(soon.T.ConnectionId, token.GetProperty("connectionId").GetGuid());
    }

    [DbFact]
    public async Task A_token_past_its_expiry_is_marked_expired_and_the_connection_stops_syncing()
    {
        var s = await SetupAsync(DateTimeOffset.UtcNow.AddDays(1));
        await using var _ = s;
        await s.T.ExecAsync("update github_credentials set expires_at = now() - interval '1 minute' where connection_id = @id", new { id = s.T.ConnectionId });
        Assert.Equal("expired", (await ConnectionAsync(s)).GetProperty("tokenStatus").GetString());

        await RunSyncAsync(s);

        Assert.Empty(s.Source.Reads);
        Assert.Equal(0, s.Source.Lists);
        Assert.Equal("expired", await TokenFakes.StatusOfAsync(s.T));
        Assert.Equal("expired", (await ConnectionAsync(s)).GetProperty("tokenStatus").GetString());
        var audit = await AuditAsync(s.T);
        Assert.Contains("connection.token_expired", audit);
        Assert.DoesNotContain(TokenFakes.Token, audit);
        Assert.Equal(0, await s.T.ScalarAsync<int>("select count(*)::int from sync_runs where connection_id = @id", new { id = s.T.ConnectionId }));
    }

    [DbFact]
    public async Task Writing_with_an_expired_token_is_credential_expired_without_calling_github()
    {
        var s = await SetupAsync(DateTimeOffset.UtcNow.AddDays(1));
        await using var _ = s;
        await s.T.ExecAsync("update github_credentials set expires_at = now() - interval '1 minute' where connection_id = @id", new { id = s.T.ConnectionId });
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var writer = new GitHubWriter(new HttpClient(handler), Credentials(), Options.Create(new GitHubAppOptions()));

        var e = await Assert.ThrowsAsync<GitHubWriteException>(() => writer.CloseIssueAsync(Repo(s.T), 7, IssueCloseReason.Completed, default));

        Assert.Equal(GitHubWriteErrorKind.CredentialExpired, e.Kind);
        Assert.Equal(0, handler.Calls);
        var result = GitHubWriteErrors.ToResult(e);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("credential_expired", ((ErrorBody)result.Body!).Error);
    }

    // ---- revocation ----------------------------------------------------------------------------

    private sealed class FakeInstallationTokens : IGitHubInstallationTokens
    {
        public Task<string> GetAsync(long installationId, CancellationToken ct) => Task.FromResult("ghs_unused");
        public void Invalidate(long installationId) { }
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public List<string> IfNoneMatch { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            lock (IfNoneMatch)
                IfNoneMatch.Add(string.Join(",", request.Headers.IfNoneMatch));
            return Task.FromResult(respond(request));
        }
    }

    private GitHubCredentials Credentials() => new(new FakeInstallationTokens(), TokenFakes.Store(_factory));

    private static GitHubRepoRef Repo(TestTenant t) =>
        new(new GitHubConnectionRef(t.Id, t.ConnectionId, GitHubAuthKind.Token, null, "org-" + t.Suffix, "owner"), "org", "api");

    [DbFact]
    public async Task A_401_on_a_sync_read_marks_the_token_revoked_and_is_not_retried()
    {
        var s = await SetupAsync();
        await using var _ = s;
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var source = new GitHubSource(new HttpClient(handler), Credentials());
        var connection = Repo(s.T).Connection;

        var e = await Assert.ThrowsAsync<GitHubSourceException>(() => source.ListRepositoriesAsync(connection, default));

        Assert.Equal(GitHubSourceErrorKind.CredentialRevoked, e.Kind);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("revoked", await TokenFakes.StatusOfAsync(s.T));
        var again = await Assert.ThrowsAsync<GitHubSourceException>(() => source.ListRepositoriesAsync(connection, default));
        Assert.Equal(GitHubSourceErrorKind.CredentialRevoked, again.Kind);
        Assert.Equal(1, handler.Calls);

        var audit = await AuditAsync(s.T);
        Assert.Contains("connection.token_revoked", audit);
        Assert.DoesNotContain(TokenFakes.Token, audit);
        Assert.Equal("revoked", (await ConnectionAsync(s)).GetProperty("tokenStatus").GetString());

        await RunSyncAsync(s);
        Assert.Equal(0, s.Source.Lists);
    }

    [DbFact]
    public async Task A_401_on_a_write_marks_the_token_revoked_and_the_next_write_does_not_call_github()
    {
        var s = await SetupAsync();
        await using var _ = s;
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var writer = new GitHubWriter(new HttpClient(handler), Credentials(), Options.Create(new GitHubAppOptions()));

        var first = await Assert.ThrowsAsync<GitHubWriteException>(() => writer.CloseIssueAsync(Repo(s.T), 7, IssueCloseReason.Completed, default));
        var second = await Assert.ThrowsAsync<GitHubWriteException>(() => writer.CloseIssueAsync(Repo(s.T), 7, IssueCloseReason.Completed, default));

        Assert.Equal(GitHubWriteErrorKind.CredentialRevoked, first.Kind);
        Assert.Equal(GitHubWriteErrorKind.CredentialRevoked, second.Kind);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("revoked", await TokenFakes.StatusOfAsync(s.T));
        var result = GitHubWriteErrors.ToResult(second);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("credential_revoked", ((ErrorBody)result.Body!).Error);
    }

    [DbFact]
    public async Task A_401_for_an_old_token_does_not_revoke_a_token_saved_since()
    {
        var s = await SetupAsync();
        await using var _ = s;
        var store = TokenFakes.Store(_factory);
        var memberId = await s.T.ScalarAsync<Guid>("select id from members where tenant_id = @tid and github_user_id = @u", new { tid = s.T.Id, u = s.UserId });
        await store.SaveAsync(s.T.Id, s.T.ConnectionId, memberId, Replacement, [], null);

        Assert.False(await store.RevokeAsync(s.T.Id, s.T.ConnectionId, TokenFakes.Token));

        Assert.Equal("active", await TokenFakes.StatusOfAsync(s.T));
    }

    // ---- owner leaves --------------------------------------------------------------------------

    [DbFact]
    public async Task Removing_the_token_owner_deletes_the_token_and_the_connection_needs_a_token()
    {
        var s = await SetupAsync();
        await using var _ = s;
        var (otherLogin, otherId) = await s.T.AddMemberAsync("admin2");
        await s.T.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_user_id = @otherId", new { tid = s.T.Id, otherId });
        var ownerMember = await s.T.ScalarAsync<Guid>("select id from members where tenant_id = @tid and github_user_id = @u", new { tid = s.T.Id, u = s.UserId });
        var other = s.Other(otherLogin);

        var removed = await other.DeleteAsync($"/api/members/{ownerMember}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await s.T.ScalarAsync<int>("select count(*)::int from github_credentials where connection_id = @id", new { id = s.T.ConnectionId }));
        var connection = await ConnectionAsync(s with { Client = other });
        Assert.Equal("token_required", connection.GetProperty("tokenStatus").GetString());
        var audit = await AuditAsync(s.T);
        Assert.Contains("connection.token_released", audit);
        Assert.Contains("owner_left_company", audit);
        Assert.DoesNotContain(TokenFakes.Token, audit);

        await RunSyncAsync(s with { Client = other });
        Assert.Equal(0, s.Source.Lists);
    }

    [DbFact]
    public async Task A_sync_run_releases_the_token_of_an_owner_who_left_by_any_path()
    {
        var s = await SetupAsync();
        await using var _ = s;
        var (_, otherId) = await s.T.AddMemberAsync("admin2");
        await s.T.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_user_id = @otherId", new { tid = s.T.Id, otherId });
        await s.T.ExecAsync("update members set left_at = now() where tenant_id = @tid and github_user_id = @u", new { tid = s.T.Id, u = s.UserId });
        var other = s.Other((string)(await s.T.QueryAsync("select github_login from members where tenant_id = @tid and github_user_id = @otherId",
            new { tid = s.T.Id, otherId })).Single().github_login);

        await RunSyncAsync(s with { Client = other });

        Assert.Equal(0, await s.T.ScalarAsync<int>("select count(*)::int from github_credentials where connection_id = @id", new { id = s.T.ConnectionId }));
        Assert.Equal(0, s.Source.Lists);
    }

    [DbTheory]
    [InlineData(OrgAccessStatus.NotMember)]
    [InlineData(OrgAccessStatus.NotActive)]
    public async Task An_owner_who_lost_organization_membership_loses_the_token_at_the_next_sync(OrgAccessStatus status)
    {
        var s = await SetupAsync();
        await using var _ = s;
        s.Membership.Access = new OrgAccess(status);

        await RunSyncAsync(s);

        Assert.Equal(1, s.Membership.Checks);
        Assert.Equal(0, s.Source.Lists);
        Assert.Equal(0, await s.T.ScalarAsync<int>("select count(*)::int from github_credentials where connection_id = @id", new { id = s.T.ConnectionId }));
        Assert.Equal("token_required", (await ConnectionAsync(s)).GetProperty("tokenStatus").GetString());
        var audit = await AuditAsync(s.T);
        Assert.Contains("owner_left_organization", audit);
        Assert.DoesNotContain(TokenFakes.Token, audit);
        var run = await s.T.QueryAsync("select status, error from sync_runs where connection_id = @id", new { id = s.T.ConnectionId });
        Assert.StartsWith("token_required", (string)Assert.Single(run).error);
    }

    [DbFact]
    public async Task A_token_github_no_longer_accepts_for_the_organization_is_marked_invalid()
    {
        var s = await SetupAsync();
        await using var _ = s;
        s.Membership.Access = new OrgAccess(OrgAccessStatus.SsoRequired);

        await RunSyncAsync(s);

        Assert.Equal("invalid", await TokenFakes.StatusOfAsync(s.T));
        Assert.Equal(0, s.Source.Lists);
        Assert.Contains("sso_not_authorized", await AuditAsync(s.T));
    }

    [DbFact]
    public async Task A_membership_check_that_gets_a_401_revokes_the_token()
    {
        var s = await SetupAsync();
        await using var _ = s;
        s.Membership.Access = null;

        await RunSyncAsync(s);

        Assert.Equal(0, s.Source.Lists);
    }

    // ---- handover ------------------------------------------------------------------------------

    [DbTheory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("invalid")]
    [InlineData("token_required")]
    public async Task Another_admin_replacing_the_token_revives_a_stopped_connection(string state)
    {
        var s = await SetupAsync(credential: true);
        await using var _ = s;
        if (state == "token_required")
            await s.T.ExecAsync("delete from github_credentials where connection_id = @id", new { id = s.T.ConnectionId });
        else
            await s.T.ExecAsync("update github_credentials set status = @state where connection_id = @id", new { state, id = s.T.ConnectionId });
        var (otherLogin, otherId) = await s.T.AddMemberAsync("admin2");
        await s.T.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_user_id = @otherId", new { tid = s.T.Id, otherId });
        await RunSyncAsync(s);
        Assert.Equal(0, s.Source.Lists);

        var membership = new FakeMembership
        {
            OrgId = s.Membership.OrgId,
            Identity = new TokenIdentity(new GitHubUser(otherId, otherLogin), ["repo", "read:org"], DateTimeOffset.UtcNow.AddDays(60)),
        };
        var other = s.Other(otherLogin, membership: membership);
        await s.T.ExecAsync("update github_connections set sync_not_before = now() + interval '30 minutes' where id = @id", new { id = s.T.ConnectionId });

        var wrongPerson = await s.Other(s.Login, membership: membership)
            .PutAsJsonAsync($"/api/connections/{s.T.ConnectionId}/github-token", new { token = Replacement });
        Assert.Equal(HttpStatusCode.Forbidden, wrongPerson.StatusCode);
        Assert.NotEqual("active", (await s.T.QueryAsync("select status from github_credentials where connection_id = @id", new { id = s.T.ConnectionId }))
            .Select(r => (string)r.status).SingleOrDefault() ?? "token_required");

        var replaced = await other.PutAsJsonAsync($"/api/connections/{s.T.ConnectionId}/github-token", new { token = Replacement });

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var body = await replaced.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("active", body.GetProperty("tokenStatus").GetString());
        Assert.Equal(otherId, await s.T.ScalarAsync<long>(
            "select m.github_user_id from github_credentials k join members m on m.id = k.owner_member_id where k.connection_id = @id", new { id = s.T.ConnectionId }));
        Assert.Contains("\"previousStatus\": \"" + state + "\"", await AuditAsync(s.T));

        s.Membership.Access = new OrgAccess(OrgAccessStatus.Active, s.Membership.OrgId, "org");
        await RunSyncAsync(s with { Client = other });
        Assert.Equal([$"org-{s.T.Suffix}/api"], s.Source.Reads.ToArray());
    }

    // ---- rate limit ----------------------------------------------------------------------------

    [DbFact]
    public async Task A_token_connection_with_few_requests_left_pauses_until_the_limit_resets()
    {
        var s = await SetupAsync();
        await using var _ = s;
        s.Source.Limit = new RateLimitInfo(100, DateTimeOffset.UtcNow.AddMinutes(20));

        await RunSyncAsync(s);

        Assert.Equal(1, s.Source.Lists);
        var until = await s.T.ScalarAsync<DateTime?>("select sync_not_before from github_connections where id = @id", new { id = s.T.ConnectionId });
        Assert.NotNull(until);
        Assert.InRange(until!.Value, DateTime.UtcNow.AddMinutes(18), DateTime.UtcNow.AddMinutes(22));
        Assert.NotEqual(JsonValueKind.Null, (await ConnectionAsync(s)).GetProperty("syncPausedUntil").ValueKind);
        var token = Assert.Single((await s.Client.GetFromJsonAsync<JsonElement>("/api/sync/status")).GetProperty("tokens").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, token.GetProperty("syncPausedUntil").ValueKind);

        s.Source.Reads.Clear();
        await RunSyncAsync(s);
        Assert.Equal(1, s.Source.Lists);
        Assert.Empty(s.Source.Reads);

        await s.T.ExecAsync("update github_connections set sync_not_before = now() - interval '1 second' where id = @id", new { id = s.T.ConnectionId });
        s.Source.Limit = new RateLimitInfo(4000, null);
        await RunSyncAsync(s);
        Assert.Equal(2, s.Source.Lists);
        Assert.Equal([$"org-{s.T.Suffix}/api"], s.Source.Reads.ToArray());
        Assert.Equal(JsonValueKind.Null, (await ConnectionAsync(s)).GetProperty("syncPausedUntil").ValueKind);
    }

    [DbFact]
    public async Task A_rate_limited_answer_pauses_a_token_connection_until_the_reset_time()
    {
        var s = await SetupAsync();
        await using var _ = s;
        var client = s.Other(s.Login, new ThrowingSource(DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/sync/run", null)).StatusCode);
        await TokenFakes.WaitSyncIdleAsync(client);

        var until = await s.T.ScalarAsync<DateTime?>("select sync_not_before from github_connections where id = @id", new { id = s.T.ConnectionId });
        Assert.NotNull(until);
        Assert.InRange(until!.Value, DateTime.UtcNow.AddMinutes(8), DateTime.UtcNow.AddMinutes(12));
    }

    private sealed class ThrowingSource(DateTimeOffset retryAt) : IGitHubSource
    {
        public Task<RepositoryList> ListRepositoriesAsync(GitHubConnectionRef connection, CancellationToken ct) =>
            throw new GitHubSourceException(GitHubSourceErrorKind.RateLimited, "limit", retryAt);

        public Task<RepositorySnapshot> ReadRepositoryAsync(
            GitHubConnectionRef connection, string owner, string name, DateTimeOffset? since, CancellationToken ct) =>
            throw new InvalidOperationException();
    }

    [DbFact]
    public async Task App_connections_do_not_pause_when_few_requests_are_left()
    {
        var s = await SetupAsync(app: true);
        await using var _ = s;
        s.Source.Limit = new RateLimitInfo(5, DateTimeOffset.UtcNow.AddMinutes(20));

        await RunSyncAsync(s);
        await RunSyncAsync(s);

        Assert.Equal(2, s.Source.Lists);
        Assert.Null(await s.T.ScalarAsync<DateTime?>("select sync_not_before from github_connections where id = @id", new { id = s.T.ConnectionId }));
        Assert.Equal(0, s.Membership.Checks);
    }

    // ---- ETag ----------------------------------------------------------------------------------

    private static HttpResponseMessage RepoPage(string etag, int rateRemaining = 4000)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"id":1,"name":"api","full_name":"acme/api","html_url":"https://x","archived":false}]""", Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", rateRemaining.ToString());
        return response;
    }

    private sealed class TokenOf(string token) : IGitHubCredentials
    {
        public string Token { get; set; } = token;
        public Task<GitHubCredential> GetAsync(GitHubConnectionRef connection, CancellationToken ct) =>
            Task.FromResult(new GitHubCredential(Token, connection.Kind));
        public void Invalidate(GitHubConnectionRef connection) { }
    }

    private static readonly GitHubConnectionRef TokenConn = new(Guid.NewGuid(), Guid.NewGuid(), GitHubAuthKind.Token, null, "acme", "owner");

    [Fact]
    public async Task A_304_reuses_the_remembered_list_and_sends_if_none_match()
    {
        var handler = new CountingHandler(r => r.Headers.IfNoneMatch.Count == 0
            ? RepoPage("\"v1\"")
            : new HttpResponseMessage(HttpStatusCode.NotModified));
        var source = new GitHubSource(new HttpClient(handler), new TokenOf("ghp_a"), new GitHubEtagCache());

        var first = await source.ListRepositoriesAsync(TokenConn, default);
        var second = await source.ListRepositoriesAsync(TokenConn, default);

        Assert.Equal(["acme/api"], first.Repositories.Select(r => r.FullName).ToArray());
        Assert.Equal(first.Repositories, second.Repositories);
        Assert.Equal(["", "\"v1\""], handler.IfNoneMatch.ToArray());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_changed_answer_replaces_the_remembered_etag_and_a_new_token_starts_without_one()
    {
        var version = 1;
        var handler = new CountingHandler(r =>
            r.Headers.IfNoneMatch.Any(t => t.Tag == $"\"v{version}\"")
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : RepoPage($"\"v{version}\""));
        var creds = new TokenOf("ghp_a");
        var source = new GitHubSource(new HttpClient(handler), creds, new GitHubEtagCache());

        await source.ListRepositoriesAsync(TokenConn, default);
        version = 2;
        await source.ListRepositoriesAsync(TokenConn, default);
        await source.ListRepositoriesAsync(TokenConn, default);
        creds.Token = "ghp_b";
        await source.ListRepositoriesAsync(TokenConn, default);

        Assert.Equal(["", "\"v1\"", "\"v2\"", ""], handler.IfNoneMatch.ToArray());
    }

    [Fact]
    public async Task A_304_without_a_remembered_body_is_an_error_and_app_connections_never_use_etags()
    {
        var cache = new GitHubEtagCache();
        var lone = new GitHubSource(new HttpClient(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified))), new TokenOf("ghp_a"), cache);
        await Assert.ThrowsAsync<GitHubSourceException>(() => lone.ListRepositoriesAsync(TokenConn, default));

        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"total_count":0,"repositories":[]}""", Encoding.UTF8, "application/json"),
        });
        var app = new GitHubSource(new HttpClient(handler), new TokenOf("ghs_a"), cache);
        var appConn = new GitHubConnectionRef(Guid.NewGuid(), Guid.NewGuid(), GitHubAuthKind.App, 5);
        await app.ListRepositoriesAsync(appConn, default);
        await app.ListRepositoriesAsync(appConn, default);

        Assert.All(handler.IfNoneMatch, v => Assert.Equal("", v));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task A_304_still_reports_the_rate_limit()
    {
        var handler = new CountingHandler(r =>
        {
            if (r.Headers.IfNoneMatch.Count == 0)
                return RepoPage("\"v1\"");
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "123");
            return response;
        });
        var source = new GitHubSource(new HttpClient(handler), new TokenOf("ghp_a"), new GitHubEtagCache());

        await source.ListRepositoriesAsync(TokenConn, default);
        var second = await source.ListRepositoriesAsync(TokenConn, default);

        Assert.Equal(123, second.RateLimit!.Remaining);
    }
}
