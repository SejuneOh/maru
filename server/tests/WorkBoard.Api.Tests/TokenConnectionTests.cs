using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using WorkBoard.Api.Infrastructure.Credentials;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class TokenConnectionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Classic = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";
    private const string FineGrained = "github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz";
    private const string Replacement = "ghp_ZZZZZZZZZZ9876543210zyxwvutsrqponmlk";

    private sealed class FakeTokenClient : IGitHubTokenClient
    {
        public TokenIdentity? Identity { get; set; }
        public OrgAccess? Access { get; set; }
        public Exception? Throw { get; set; }
        public List<string> OrgCalls { get; } = [];
        public Func<Task>? DuringOrgAccess { get; set; }

        public Task<TokenIdentity?> GetIdentityAsync(string token, CancellationToken ct) =>
            Throw is { } e ? Task.FromException<TokenIdentity?>(e) : Task.FromResult(Identity);

        public async Task<OrgAccess?> GetOrganizationAccessAsync(string token, string organization, CancellationToken ct)
        {
            OrgCalls.Add(organization);
            if (DuringOrgAccess is { } hook)
                await hook();
            return Access;
        }
    }

    private sealed class StaticKeys : ICredentialKeys
    {
        private readonly CredentialKeyring _ring = new(1, new Dictionary<int, byte[]> { [1] = new byte[CredentialKeyring.KeySize] });
        public CredentialKeyring Get() => _ring;
    }

    private sealed class CapturingLogs : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
                Lines.Add(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
        }
        public void Dispose() { }
    }

    private readonly WebApplicationFactory<Program> _factory;

    public TokenConnectionTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private NpgsqlDataSource Sync => _factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.Sync);

    private HttpClient Client(FakeTokenClient fake, string login, CapturingLogs? logs = null, bool keys = true) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "false",
                ["GitHub:Credentials:KeyFile"] = keys ? null : Path.Combine(Path.GetTempPath(), "wb75-missing-key.json"),
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IGitHubTokenClient>();
                s.AddSingleton<IGitHubTokenClient>(fake);
                if (keys)
                {
                    s.RemoveAll<ICredentialKeys>();
                    s.AddSingleton<ICredentialKeys, StaticKeys>();
                }
                if (logs is not null)
                    s.AddSingleton<ILoggerProvider>(logs);
            });
        }).CreateClient().WithDevUser(login);

    private static async Task<(string Login, long UserId)> AdminAsync(TestTenant t)
    {
        var (login, userId) = await t.AddMemberAsync("admin");
        await t.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_user_id = @userId",
            new { tid = t.Id, userId });
        return (login, userId);
    }

    private static FakeTokenClient Fake(long userId, string login, string org, long orgId, IReadOnlyList<string>? scopes = null,
        DateTimeOffset? expires = null) => new()
    {
        Identity = new TokenIdentity(new GitHubUser(userId, login), scopes ?? ["repo", "read:org"], expires),
        Access = new OrgAccess(OrgAccessStatus.Active, orgId, org),
    };

    private static Task<HttpResponseMessage> PostAsync(HttpClient c, string token, string org) =>
        c.PostAsJsonAsync("/api/connections/github-token", new { token, organization = org });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string?> ErrorAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("error").GetString();

    private async Task<string> AuditTextAsync(TestTenant t)
    {
        var rows = await t.QueryAsync("select action, coalesce(before::text, '') || coalesce(after::text, '') as data from audit_log where tenant_id = @tid order by created_at",
            new { tid = t.Id });
        return string.Join("\n", rows.Select(r => $"{r.action} {r.data}"));
    }

    [DbFact]
    public async Task Admin_connects_an_organization_and_the_token_is_stored_but_never_shown()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var orgId = TestTenant.NextId();
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        var logs = new CapturingLogs();

        var response = await PostAsync(Client(Fake(userId, login, "acme-" + t.Suffix, orgId, expires: expires), login, logs), Classic, "acme-" + t.Suffix);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Classic, text);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("token", body.GetProperty("authKind").GetString());
        Assert.Equal(Classic[^4..], body.GetProperty("tokenLast4").GetString());
        Assert.Equal(login, body.GetProperty("tokenOwnerLogin").GetString());
        Assert.Equal("active", body.GetProperty("tokenStatus").GetString());
        Assert.Equal(["repo", "read:org"], body.GetProperty("tokenScopes").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("installationId").ValueKind);
        var id = body.GetProperty("id").GetGuid();

        var row = await t.QueryAsync("select auth_kind, store_bodies, installation_id, account_id, account_type from github_connections where id = @id", new { id });
        var r = Assert.Single(row);
        Assert.Equal("token", (string)r.auth_kind);
        Assert.False((bool)r.store_bodies);
        Assert.Null((object?)r.installation_id);
        Assert.Equal(orgId, (long)r.account_id);
        Assert.Equal("organization", (string)r.account_type);

        var store = new CredentialStore(Sync, new CredentialCipher(new StaticKeys()));
        Assert.Equal(Classic, await store.OpenAsync(t.Id, id));
        var stored = await t.ScalarAsync<DateTime>("select expires_at from github_credentials where connection_id = @id", new { id });
        Assert.InRange((stored - expires.UtcDateTime).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var audit = await AuditTextAsync(t);
        Assert.Contains("connection.token_connect", audit);
        Assert.DoesNotContain(Classic, audit);
        Assert.DoesNotContain(Classic, string.Join("\n", logs.Lines));
        Assert.DoesNotContain(Classic[..^4], audit);
    }

    [DbFact]
    public async Task Fine_grained_token_is_accepted_without_scopes_header()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId, login, "acme-" + t.Suffix, TestTenant.NextId());
        fake.Identity = fake.Identity! with { Scopes = null };

        var response = await PostAsync(Client(fake, login), FineGrained, "acme-" + t.Suffix);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var scopes = await t.ScalarAsync<string[]>("select scopes from github_credentials where tenant_id = @tid", new { tid = t.Id });
        Assert.Empty(scopes);
    }

    [DbFact]
    public async Task A_token_without_scopes_header_that_is_not_fine_grained_is_unsupported()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId, login, "acme", TestTenant.NextId());
        fake.Identity = fake.Identity! with { Scopes = null };

        var response = await PostAsync(Client(fake, login), "ghu_0123456789abcdefghijklmnopqrstuvwxyz", "acme");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_token", await ErrorAsync(response));
    }

    [DbTheory]
    [InlineData("gho_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("ghu_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("ghs_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("ghr_0123456789abcdefghijklmnopqrstuvwxyz")]
    public async Task Tokens_that_are_not_classic_or_fine_grained_pats_are_unsupported(string token)
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId, login, "acme", TestTenant.NextId());

        var response = await PostAsync(Client(fake, login), token, "acme");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_token", await ErrorAsync(response));
        Assert.Empty(fake.OrgCalls);
    }

    [DbFact]
    public async Task A_delete_between_verification_and_save_leaves_no_token_on_the_disconnected_connection()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var orgId = TestTenant.NextId();
        var fake = Fake(userId, login, "acme", orgId);
        var client = Client(fake, login);
        var id = (await JsonAsync(await PostAsync(client, Classic, "acme"))).GetProperty("id").GetGuid();
        fake.DuringOrgAccess = async () =>
        {
            fake.DuringOrgAccess = null;
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/connections/{id}/github-token")).StatusCode);
        };

        var response = await client.PutAsJsonAsync($"/api/connections/{id}/github-token", new { token = Replacement });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from github_credentials where connection_id = @id", new { id }));
    }

    [DbFact]
    public async Task Replace_and_delete_of_another_companys_connection_are_404()
    {
        await using var other = await TestTenant.CreateAsync(_factory);
        await using var t = await TestTenant.CreateAsync(_factory);
        var (otherLogin, otherUser) = await AdminAsync(other);
        var (login, userId) = await AdminAsync(t);
        var otherClient = Client(Fake(otherUser, otherLogin, "acme-o", TestTenant.NextId()), otherLogin);
        var foreign = (await JsonAsync(await PostAsync(otherClient, Classic, "acme-o"))).GetProperty("id").GetGuid();
        var client = Client(Fake(userId, login, "acme-o", TestTenant.NextId()), login);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/connections/{foreign}/github-token", new { token = Replacement })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/connections/{foreign}/github-token")).StatusCode);
        Assert.Equal(1, await other.ScalarAsync<int>("select count(*)::int from github_credentials where connection_id = @foreign", new { foreign }));
        Assert.True(await other.ScalarAsync<bool>("select disconnected_at is null from github_connections where id = @foreign", new { foreign }));
    }

    [DbFact]
    public async Task Rejected_token_is_invalid_token_and_the_failure_is_audited_masked()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, _) = await AdminAsync(t);
        var logs = new CapturingLogs();

        var response = await PostAsync(Client(new FakeTokenClient { Identity = null }, login, logs), Classic, "acme");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_token", await ErrorAsync(response));
        var audit = await AuditTextAsync(t);
        Assert.Contains("connection.token_failed", audit);
        Assert.Contains("invalid_token", audit);
        Assert.DoesNotContain(Classic, audit);
        Assert.DoesNotContain(Classic, string.Join("\n", logs.Lines));
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from github_credentials where tenant_id = @tid", new { tid = t.Id }));
    }

    [DbFact]
    public async Task Expired_token_is_invalid()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);

        var response = await PostAsync(
            Client(Fake(userId, login, "acme", TestTenant.NextId(), expires: DateTimeOffset.UtcNow.AddDays(-1)), login), Classic, "acme");

        Assert.Equal("invalid_token", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Token_of_another_github_user_is_refused()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId + 1, "someone-else", "acme", TestTenant.NextId());

        var response = await PostAsync(Client(fake, login), Classic, "acme");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not_your_account", await ErrorAsync(response));
        Assert.Empty(fake.OrgCalls);
    }

    [DbTheory]
    [InlineData(OrgAccessStatus.NotActive, "org_membership_inactive")]
    [InlineData(OrgAccessStatus.NotMember, "not_org_member")]
    [InlineData(OrgAccessStatus.Forbidden, "org_access_denied")]
    public async Task Membership_that_is_not_active_is_refused(OrgAccessStatus status, string code)
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId, login, "acme", TestTenant.NextId());
        fake.Access = new OrgAccess(status);

        var response = await PostAsync(Client(fake, login), Classic, "acme");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(code, await ErrorAsync(response));
    }

    [DbFact]
    public async Task Sso_not_authorized_returns_the_authorization_link()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId, login, "acme", TestTenant.NextId());
        fake.Access = new OrgAccess(OrgAccessStatus.SsoRequired, SsoUrl: "https://github.com/orgs/acme/sso?authorization_request=abc");

        var response = await PostAsync(Client(fake, login), Classic, "acme");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("sso_not_authorized", body.GetProperty("error").GetString());
        Assert.Equal("https://github.com/orgs/acme/sso?authorization_request=abc", body.GetProperty("ssoUrl").GetString());
    }

    [DbTheory]
    [InlineData("repo,read:org,admin:org")]
    [InlineData("repo,read:org,delete_repo")]
    [InlineData("repo,write:org")]
    public async Task Broad_classic_scopes_are_refused(string scopes)
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId, login, "acme", TestTenant.NextId(), scopes.Split(','));

        var response = await PostAsync(Client(fake, login), Classic, "acme");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("token_scope_too_broad", body.GetProperty("error").GetString());
        Assert.NotEmpty(body.GetProperty("scopes").EnumerateArray());
        Assert.Empty(fake.OrgCalls);
    }

    [DbFact]
    public async Task Classic_token_without_read_org_is_refused()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId, login, "acme", TestTenant.NextId(), ["repo"]);

        var response = await PostAsync(Client(fake, login), Classic, "acme");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("token_scope_missing", body.GetProperty("error").GetString());
        Assert.Equal("read:org", body.GetProperty("missing")[0].GetString());
    }

    [DbFact]
    public async Task Organization_connected_by_another_company_is_a_conflict()
    {
        await using var other = await TestTenant.CreateAsync(_factory);
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var orgId = TestTenant.NextId();
        await other.ExecAsync("update github_connections set account_id = @orgId where id = @id", new { orgId, id = other.ConnectionId });

        var response = await PostAsync(Client(Fake(userId, login, "acme", orgId), login), Classic, "acme");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("account_connected", await ErrorAsync(response));
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from github_credentials where tenant_id = @tid", new { tid = t.Id }));
    }

    [DbFact]
    public async Task Organization_connected_by_the_same_company_is_a_conflict()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var orgId = await t.ScalarAsync<long>("select account_id from github_connections where id = @id", new { id = t.ConnectionId });

        var response = await PostAsync(Client(Fake(userId, login, "acme", orgId), login), Classic, "acme");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [DbFact]
    public async Task Non_admin_is_refused_before_anything_is_checked()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var fake = Fake(1, "x", "acme", 1);

        var response = await PostAsync(Client(fake, t.ViewerLogin), Classic, "acme");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("admin_only", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Missing_key_file_answers_503_before_calling_github()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId, login, "acme", TestTenant.NextId());

        var response = await PostAsync(Client(fake, login, keys: false), Classic, "acme");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("credential_store_not_configured", await ErrorAsync(response));
        Assert.Empty(fake.OrgCalls);
    }

    [DbFact]
    public async Task Github_outage_is_502()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, _) = await AdminAsync(t);
        var fake = new FakeTokenClient { Throw = new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "down") };

        var response = await PostAsync(Client(fake, login), Classic, "acme");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("github_unavailable", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Replacing_the_token_checks_the_owner_again_and_swaps_the_stored_token()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var orgId = TestTenant.NextId();
        var fake = Fake(userId, login, "acme", orgId);
        var client = Client(fake, login);
        var id = (await JsonAsync(await PostAsync(client, Classic, "acme"))).GetProperty("id").GetGuid();

        fake.Identity = fake.Identity! with { User = new GitHubUser(userId + 1, "other") };
        var wrong = await client.PutAsJsonAsync($"/api/connections/{id}/github-token", new { token = Replacement });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        fake.Identity = fake.Identity with { User = new GitHubUser(userId, login) };
        fake.Access = new OrgAccess(OrgAccessStatus.Active, orgId + 1, "acme");
        Assert.Equal("organization_mismatch", await ErrorAsync(await client.PutAsJsonAsync($"/api/connections/{id}/github-token", new { token = Replacement })));

        fake.Access = new OrgAccess(OrgAccessStatus.Active, orgId, "acme");
        var ok = await client.PutAsJsonAsync($"/api/connections/{id}/github-token", new { token = Replacement });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var text = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Replacement, text);
        Assert.Equal(Replacement[^4..], JsonDocument.Parse(text).RootElement.GetProperty("tokenLast4").GetString());
        var store = new CredentialStore(Sync, new CredentialCipher(new StaticKeys()));
        Assert.Equal(Replacement, await store.OpenAsync(t.Id, id));
        var audit = await AuditTextAsync(t);
        Assert.Contains("connection.token_replace", audit);
        Assert.DoesNotContain(Replacement, audit);
        Assert.DoesNotContain(Classic, audit);
    }

    [DbFact]
    public async Task Deleting_a_token_connection_disconnects_it_and_removes_the_token()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId, login, "acme", TestTenant.NextId());
        var client = Client(fake, login);
        var id = (await JsonAsync(await PostAsync(client, Classic, "acme"))).GetProperty("id").GetGuid();

        var deleted = await client.DeleteAsync($"/api/connections/{id}/github-token");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await t.ScalarAsync<bool>("select disconnected_at is null from github_connections where id = @id", new { id }));
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from github_credentials where connection_id = @id", new { id }));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/connections/{id}/github-token")).StatusCode);
        Assert.Contains("connection.token_delete", await AuditTextAsync(t));

        var again = await PostAsync(client, Classic, "acme");
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [DbFact]
    public async Task Delete_and_replace_are_admin_only_and_do_not_touch_app_connections()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, userId) = await AdminAsync(t);
        var fake = Fake(userId, login, "acme", TestTenant.NextId());

        var viewer = Client(fake, t.ViewerLogin);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/connections/{t.ConnectionId}/github-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PutAsJsonAsync($"/api/connections/{t.ConnectionId}/github-token", new { token = Replacement })).StatusCode);

        var admin = Client(fake, login);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/connections/{t.ConnectionId}/github-token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync($"/api/connections/{t.ConnectionId}/github-token", new { token = Replacement })).StatusCode);
    }

    // ---- reads and writes use the connection's own auth ----------------------------------------

    private sealed class RecordingSource : IGitHubSource
    {
        public List<GitHubConnectionRef> Lists { get; } = [];
        public List<GitHubConnectionRef> Reads { get; } = [];
        public List<SourceRepository> Repositories { get; set; } = [];

        public Task<RepositoryList> ListRepositoriesAsync(GitHubConnectionRef connection, CancellationToken ct)
        {
            lock (Lists)
                Lists.Add(connection);
            return Task.FromResult(new RepositoryList(Repositories, null));
        }

        public Task<RepositorySnapshot> ReadRepositoryAsync(
            GitHubConnectionRef connection, string owner, string name, DateTimeOffset? since, CancellationToken ct)
        {
            lock (Reads)
                Reads.Add(connection);
            var repo = Repositories.Single(r => r.FullName == $"{owner}/{name}");
            return Task.FromResult(new RepositorySnapshot(repo, [], [], [], [], [], null));
        }
    }

    [DbFact]
    public async Task Sync_run_reads_a_token_connection_with_its_auth_kind_even_without_the_app()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, _) = await AdminAsync(t);
        await t.ExecAsync("update github_connections set auth_kind = 'token', store_bodies = false where id = @id", new { id = t.ConnectionId });
        var source = new RecordingSource { Repositories = [new SourceRepository(TestTenant.NextId(), "api", $"org-{t.Suffix}/api", "https://x/api", false)] };
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:App:AppId"] = "0", ["GitHub:App:PrivateKeyPath"] = "", ["Sync:Enabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IGitHubSource>();
                s.AddSingleton<IGitHubSource>(source);
            });
        }).CreateClient().WithDevUser(login);

        var run = await client.PostAsync("/api/sync/run", null);

        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (source.Reads.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        var list = Assert.Single(source.Lists);
        Assert.Equal(GitHubAuthKind.Token, list.Kind);
        Assert.Equal($"org-{t.Suffix}", list.AccountLogin);
        Assert.Null(list.InstallationId);
        Assert.Equal(GitHubAuthKind.Token, Assert.Single(source.Reads).Kind);
    }

    [DbFact]
    public async Task New_issue_options_include_token_connection_repositories()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        await t.ExecAsync("update github_connections set auth_kind = 'token' where id = @id", new { id = t.ConnectionId });
        await t.AddRepoAsync("tokrepo");
        using var client = _factory.CreateClient().WithDevUser(t.ViewerLogin);

        var options = await client.GetFromJsonAsync<JsonElement>("/api/issues/new-options");

        Assert.Contains(options.GetProperty("repositories").EnumerateArray(), r => r.GetProperty("name").GetString() == "tokrepo");
    }

    [DbFact]
    public async Task Issue_detail_hides_the_board_footer_on_what_the_token_owner_wrote()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        await t.ExecAsync("update github_connections set auth_kind = 'token' where id = @id", new { id = t.ConnectionId });
        var (owner, ownerId) = await t.AddMemberAsync("owner");
        await t.ExecAsync("insert into github_credentials (tenant_id, connection_id, owner_member_id, ciphertext, nonce, key_version, owner_login) " +
                          "select @tid, @cid, id, '\\x00', '\\x000000000000000000000000', 1, @owner from members where tenant_id = @tid and github_user_id = @ownerId",
            new { tid = t.Id, cid = t.ConnectionId, owner, ownerId });
        var repo = await t.AddRepoAsync("api");
        var issue = await t.AddIssueAsync(repo);
        await t.ExecAsync("insert into github_users (tenant_id, github_user_id, login) values (@tid, @ownerId, @owner)", new { tid = t.Id, ownerId, owner });
        await t.ExecAsync("update issues set author_github_id = @ownerId, body = @body where id = @issue",
            new { ownerId, issue, body = "hello" + GitHubWriter.Footer("carol") + "\n" + GitHubWriter.CreateMarker("abc") });
        using var client = _factory.CreateClient().WithDevUser(t.ViewerLogin);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/issues/{issue}");

        Assert.Equal("hello", detail.GetProperty("body").GetString());

        var plain = await t.AddIssueAsync(repo);
        var typed = "hi" + GitHubWriter.Footer("carol");
        await t.ExecAsync("update issues set author_github_id = @ownerId, body = @typed where id = @plain", new { ownerId, plain, typed });
        var untouched = await client.GetFromJsonAsync<JsonElement>($"/api/issues/{plain}");
        Assert.Equal(typed, untouched.GetProperty("body").GetString());
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Auth)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TokenCredentials : IGitHubCredentials
    {
        public bool Missing { get; set; }

        public Task<GitHubCredential> GetAsync(GitHubConnectionRef connection, CancellationToken ct) =>
            Missing
                ? throw new GitHubSourceException(GitHubSourceErrorKind.Unauthorized, "none")
                : Task.FromResult(new GitHubCredential(Classic, GitHubAuthKind.Token));

        public void Invalidate(GitHubConnectionRef connection) { }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GitHubConnectionRef TokenRef(string? owner = "tok-owner") =>
        new(Guid.Empty, Guid.Empty, GitHubAuthKind.Token, null, "acme", owner);

    [Fact]
    public async Task Source_lists_the_organizations_repositories_with_the_stored_token_and_pages()
    {
        var page = 0;
        var handler = new Handler(r =>
        {
            page++;
            var count = r.RequestUri!.Query.Contains("&page=1") ? 100 : 2;
            var items = Enumerable.Range(0, count).Select(i => $$"""{"id":{{page * 1000 + i}},"name":"r{{page}}-{{i}}","full_name":"acme/r{{page}}-{{i}}","html_url":"https://x","archived":false}""");
            return Json(HttpStatusCode.OK, "[" + string.Join(",", items) + "]");
        });
        var source = new GitHubSource(new HttpClient(handler), new TokenCredentials());

        var list = await source.ListRepositoriesAsync(TokenRef(), default);

        Assert.Equal(102, list.Repositories.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("/orgs/acme/repos", r.Uri.AbsolutePath);
            Assert.Equal("Bearer " + Classic, r.Auth);
        });
        Assert.Equal(2, handler.Requests.Count);
    }

    private const string IssueJson = """
        {"id":501,"node_id":"I_501","number":7,"title":"T","body":"b <!-- work-board:create:abc -->","state":"open",
         "user":{"id":9,"login":"Tok-Owner","avatar_url":"https://a/9"},
         "created_at":"2026-11-01T09:00:00Z","updated_at":"2026-11-01T09:00:05Z","html_url":"https://github.com/acme/api/issues/7"}
        """;

    private static readonly DateTimeOffset Since = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Duplicate_lookup_for_a_token_connection_matches_the_token_owner_not_the_bot()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, $"[{IssueJson}]"));
        var writer = new GitHubWriter(new HttpClient(handler), new TokenCredentials(),
            Microsoft.Extensions.Options.Options.Create(new GitHubAppOptions { Slug = "tok-owner" }));
        var marker = "<!-- work-board:create:abc -->";

        var repo = new GitHubRepoRef(TokenRef("tok-owner"), "acme", "api");
        Assert.NotNull(await writer.FindCreatedIssueAsync(repo, marker, Since, default));

        var someoneElse = new GitHubRepoRef(TokenRef("another"), "acme", "api");
        Assert.Null(await writer.FindCreatedIssueAsync(someoneElse, marker, Since, default));

        var unknownOwner = new GitHubRepoRef(TokenRef(null), "acme", "api");
        await Assert.ThrowsAsync<GitHubWriteException>(() => writer.FindCreatedIssueAsync(unknownOwner, marker, Since, default));

        var app = new GitHubRepoRef(new GitHubConnectionRef(Guid.Empty, Guid.Empty, GitHubAuthKind.App, 1), "acme", "api");
        Assert.Null(await new GitHubWriter(new HttpClient(handler), new TokenCredentials(),
            Microsoft.Extensions.Options.Options.Create(new GitHubAppOptions { Slug = "app" })).FindCreatedIssueAsync(app, marker, Since, default));
    }

    [Fact]
    public async Task Writing_without_a_usable_token_is_credential_missing()
    {
        var credentials = new TokenCredentials { Missing = true };
        var writer = new GitHubWriter(new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, IssueJson))), credentials,
            Microsoft.Extensions.Options.Options.Create(new GitHubAppOptions()));
        var repo = new GitHubRepoRef(TokenRef(), "acme", "api");

        var missing = await Assert.ThrowsAsync<GitHubWriteException>(() => writer.CloseIssueAsync(repo, 7, IssueCloseReason.Completed, default));
        Assert.Equal(GitHubWriteErrorKind.CredentialMissing, missing.Kind);
        var result = GitHubWriteErrors.ToResult(missing);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("credential_missing", ((ErrorBody)result.Body!).Error);

        credentials.Missing = false;
        var rejected = new GitHubWriter(new HttpClient(new Handler(_ => Json(HttpStatusCode.Unauthorized, "{}"))), credentials,
            Microsoft.Extensions.Options.Options.Create(new GitHubAppOptions()));
        var e = await Assert.ThrowsAsync<GitHubWriteException>(() => rejected.CloseIssueAsync(repo, 7, IssueCloseReason.Completed, default));
        Assert.Equal(GitHubWriteErrorKind.CredentialMissing, e.Kind);
    }

    [Fact]
    public void Sso_header_and_expiry_header_are_parsed()
    {
        Assert.Equal("https://github.com/orgs/acme/sso?authorization_request=abc",
            GitHubTokenClient.ParseSsoUrl("required; url=https://github.com/orgs/acme/sso?authorization_request=abc"));
        Assert.Null(GitHubTokenClient.ParseSsoUrl("required; url=https://evil.example/sso"));
        Assert.Null(GitHubTokenClient.ParseSsoUrl("required"));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero), GitHubTokenClient.ParseExpiry("2026-12-01 10:00:00 UTC"));
        Assert.Null(GitHubTokenClient.ParseExpiry(null));
    }

    [Fact]
    public async Task Token_client_reads_scopes_expiry_membership_and_sso_from_github_answers()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath switch
        {
            "/user" => WithHeaders(Json(HttpStatusCode.OK, """{"id":5,"login":"me"}"""),
                ("X-OAuth-Scopes", "repo, read:org"), ("github-authentication-token-expiration", "2026-12-01 10:00:00 UTC")),
            "/user/memberships/orgs/acme" => Json(HttpStatusCode.OK, """{"state":"active","organization":{"id":77,"login":"Acme"}}"""),
            "/user/memberships/orgs/pending" => Json(HttpStatusCode.OK, """{"state":"pending","organization":{"id":78,"login":"pending"}}"""),
            "/user/memberships/orgs/gone" => Json(HttpStatusCode.NotFound, "{}"),
            "/user/memberships/orgs/sso" => WithHeaders(Json(HttpStatusCode.Forbidden, "{}"),
                ("X-GitHub-SSO", "required; url=https://github.com/orgs/sso/sso?authorization_request=q")),
            "/orgs/acme/repos" => Json(HttpStatusCode.OK, "[]"),
            _ => Json(HttpStatusCode.Unauthorized, "{}"),
        });
        var client = new GitHubTokenClient(new HttpClient(handler));

        var identity = await client.GetIdentityAsync(Classic, default);
        Assert.Equal(new GitHubUser(5, "me"), identity!.User);
        Assert.Equal(["repo", "read:org"], identity.Scopes);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero), identity.ExpiresAt);

        Assert.Equal(new OrgAccess(OrgAccessStatus.Active, 77, "Acme"), await client.GetOrganizationAccessAsync(Classic, "acme", default));
        Assert.Equal(OrgAccessStatus.NotActive, (await client.GetOrganizationAccessAsync(Classic, "pending", default))!.Status);
        Assert.Equal(OrgAccessStatus.NotMember, (await client.GetOrganizationAccessAsync(Classic, "gone", default))!.Status);
        var sso = await client.GetOrganizationAccessAsync(Classic, "sso", default);
        Assert.Equal(OrgAccessStatus.SsoRequired, sso!.Status);
        Assert.Equal("https://github.com/orgs/sso/sso?authorization_request=q", sso.SsoUrl);
        Assert.Null(await client.GetOrganizationAccessAsync(Classic, "unknown", default));
        Assert.All(handler.Requests, r => Assert.Equal("Bearer " + Classic, r.Auth));
    }

    private static HttpResponseMessage WithHeaders(HttpResponseMessage response, params (string Name, string Value)[] headers)
    {
        foreach (var (name, value) in headers)
            response.Headers.Add(name, value);
        return response;
    }
}
