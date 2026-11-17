using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class ConnectGithubTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Code = "good-code";

    private sealed class FakeGitHub : IGitHubAppClient
    {
        public GitHubUser? User { get; set; }
        public List<long> Installations { get; set; } = [];
        public GitHubInstallation? Installation { get; set; }
        public bool CodeValid { get; set; } = true;
        public bool InstallationsRejected { get; set; }
        public Exception? Throw { get; set; }

        public Task<string?> ExchangeCodeAsync(string code, CancellationToken ct) =>
            Throw is { } e ? Task.FromException<string?>(e) : Task.FromResult(CodeValid && code == Code ? "user-token" : null);

        public Task<GitHubUser?> GetUserAsync(string userToken, CancellationToken ct) => Task.FromResult(User);

        public Task<IReadOnlyList<long>?> ListUserInstallationIdsAsync(string userToken, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<long>?>(InstallationsRejected ? null : Installations);

        public Exception? ThrowOnInstallation { get; set; }

        public Task<GitHubInstallation?> GetInstallationAsync(long installationId, CancellationToken ct) =>
            ThrowOnInstallation is { } e ? Task.FromException<GitHubInstallation?>(e) : Task.FromResult(Installation);

        public List<GitHubAppInstallation> AppInstallations { get; set; } = [];

        // By installation id; anything not listed is NotMember.
        public Dictionary<long, OrgMembership> Memberships { get; set; } = [];
        public List<(long InstallationId, string Org, long UserId, string Login)> MembershipCalls { get; } = [];

        // Null answers as an installation without "Members: read".
        public List<GitHubUser>? OrgMembers { get; set; } = [];

        public Task<IReadOnlyList<GitHubAppInstallation>> ListAppInstallationsAsync(CancellationToken ct) =>
            Throw is { } e ? Task.FromException<IReadOnlyList<GitHubAppInstallation>>(e)
                : Task.FromResult<IReadOnlyList<GitHubAppInstallation>>(AppInstallations);

        public Task<OrgMembership> GetOrganizationMembershipAsync(
            long installationId, string organization, long userId, string login, CancellationToken ct)
        {
            lock (MembershipCalls)
                MembershipCalls.Add((installationId, organization, userId, login));
            return Task.FromResult(Memberships.GetValueOrDefault(installationId, OrgMembership.NotMember));
        }

        public Task<IReadOnlyList<GitHubUser>?> ListOrganizationMembersAsync(long installationId, string organization, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GitHubUser>?>(OrgMembers);
    }

    private readonly WebApplicationFactory<Program> _factory;

    public ConnectGithubTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient Client(FakeGitHub fake, string login, bool configured = true) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:App:AppId"] = configured ? "123" : "0",
                ["GitHub:App:ClientId"] = configured ? "client" : "",
                ["GitHub:App:ClientSecret"] = configured ? "secret" : "",
                ["GitHub:App:PrivateKeyPath"] = configured ? "unused.pem" : "",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IGitHubAppClient>();
                s.AddSingleton<IGitHubAppClient>(fake);
            });
        }).CreateClient().WithDevUser(login);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, long installationId, string code = Code) =>
        client.PostAsJsonAsync("/api/connections/github", new { code, installationId });

    private static async Task<string?> ErrorAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    // An admin who owns a personal GitHub account that has an App installation.
    private static async Task<(string Login, FakeGitHub Fake, long InstallationId, long AccountId)> AdminAsync(TestTenant tenant)
    {
        var (login, userId) = await tenant.AddMemberAsync("admin");
        await tenant.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_user_id = @userId",
            new { tid = tenant.Id, userId });
        var installationId = TestTenant.NextId();
        var accountId = userId;
        var fake = new FakeGitHub
        {
            User = new GitHubUser(userId, login),
            Installations = [installationId],
            Installation = new GitHubInstallation(accountId, login, "User", "selected"),
        };
        return (login, fake, installationId, accountId);
    }

    [DbFact]
    public async Task Admin_connects_personal_account_and_the_row_is_stored()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, accountId) = await AdminAsync(tenant);

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(accountId, body.GetProperty("accountId").GetInt64());
        Assert.Equal("user", body.GetProperty("accountType").GetString());
        Assert.Equal("selected", body.GetProperty("repositorySelection").GetString());
        Assert.Equal(installationId, body.GetProperty("installationId").GetInt64());
        var stored = await tenant.ScalarAsync<long>(
            "select installation_id from github_connections where tenant_id = @tid and account_id = @accountId",
            new { tid = tenant.Id, accountId });
        Assert.Equal(installationId, stored);
        await tenant.ExecAsync("delete from github_connections where account_id = @accountId", new { accountId });
    }

    [DbFact]
    public async Task Reconnecting_the_same_account_updates_the_row()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, accountId) = await AdminAsync(tenant);
        await PostAsync(Client(fake, login), installationId);
        var newInstallation = TestTenant.NextId();
        fake.Installations = [newInstallation];
        fake.Installation = new GitHubInstallation(accountId, login, "User", "all");

        var response = await PostAsync(Client(fake, login), newInstallation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await tenant.ScalarAsync<int>(
            "select count(*) from github_connections where account_id = @accountId", new { accountId });
        Assert.Equal(1, rows);
        var selection = await tenant.ScalarAsync<string>(
            "select repository_selection from github_connections where account_id = @accountId", new { accountId });
        Assert.Equal("all", selection);
        await tenant.ExecAsync("delete from github_connections where account_id = @accountId", new { accountId });
    }

    [DbFact]
    public async Task Member_who_is_not_admin_gets_403()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var fake = new FakeGitHub();

        var response = await PostAsync(Client(fake, tenant.ViewerLogin), 1);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("admin_only", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Token_of_another_github_user_gets_403_not_your_account()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, _) = await AdminAsync(tenant);
        fake.User = new GitHubUser(TestTenant.NextId(), "someone-else");

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not_your_account", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Installation_outside_the_users_list_gets_403()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, _) = await AdminAsync(tenant);
        fake.Installations = [installationId + 1];

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not_your_installation", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Personal_installation_of_another_account_gets_403_not_your_account()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, _) = await AdminAsync(tenant);
        fake.Installation = new GitHubInstallation(TestTenant.NextId(), "someone-else", "User", "all");

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not_your_account", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Invalid_code_gets_400()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, _) = await AdminAsync(tenant);

        var response = await PostAsync(Client(fake, login), installationId, "bad");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_code", await ErrorAsync(response));
    }

    // The admin's login and id, a fake whose installation is an organization with the given membership, and its id.
    private static async Task<(string Login, long UserId, FakeGitHub Fake, long InstallationId, long OrgId)> OrgOwnerAsync(
        TestTenant tenant, OrgMembership membership = OrgMembership.Owner)
    {
        var (login, fake, installationId, userId) = await AdminAsync(tenant);
        var orgId = TestTenant.NextId();
        fake.Installation = new GitHubInstallation(orgId, "test-org-" + tenant.Suffix, "Organization", "all");
        fake.Memberships[installationId] = membership;
        return (login, userId, fake, installationId, orgId);
    }

    [DbFact]
    public async Task Organization_owner_connects_and_members_are_imported()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, userId, fake, installationId, orgId) = await OrgOwnerAsync(tenant);
        var (_, stays) = await tenant.AddMemberAsync("stays");
        fake.OrgMembers = [new GitHubUser(userId, login), new GitHubUser(stays, "stays"), new GitHubUser(TestTenant.NextId(), "outsider")];

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("organization", body.GetProperty("accountType").GetString());
        Assert.Equal(orgId, body.GetProperty("accountId").GetInt64());
        Assert.Equal(3, body.GetProperty("orgMemberCount").GetInt32());
        Assert.Equal((installationId, fake.Installation!.AccountLogin, userId, login), fake.MembershipCalls.Single());
        Assert.Equal(0, await tenant.ScalarAsync<long>(
            "select count(*) from members where tenant_id = @tid and github_login = 'outsider'", new { tid = tenant.Id }));
        var list = await Client(fake, login).GetFromJsonAsync<JsonElement>("/api/connections");
        Assert.Equal(3, list.EnumerateArray().Single(c => c.GetProperty("accountId").GetInt64() == orgId)
            .GetProperty("orgMemberCount").GetInt32());
    }

    [DbFact]
    public async Task Import_marks_a_member_missing_from_the_organization_as_removed()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, userId, fake, installationId, _) = await OrgOwnerAsync(tenant);
        var first = await PostAsync(Client(fake, login), installationId);
        var connectionId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, left) = await tenant.AddMemberAsync("left");
        await tenant.ExecAsync(
            "insert into github_org_members (tenant_id, connection_id, github_user_id) values (@tid, @connectionId, @left)",
            new { tid = tenant.Id, connectionId, left });
        fake.OrgMembers = [new GitHubUser(userId, login)];

        var again = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.NotNull(await tenant.ScalarAsync<DateTime?>(
            "select removed_at from members where tenant_id = @tid and github_user_id = @left", new { tid = tenant.Id, left }));
        Assert.Null(await tenant.ScalarAsync<DateTime?>(
            "select removed_at from members where tenant_id = @tid and github_user_id = @userId", new { tid = tenant.Id, userId }));
    }

    [DbFact]
    public async Task Organization_without_members_permission_still_connects_without_a_count()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, _, fake, installationId, _) = await OrgOwnerAsync(tenant);
        fake.OrgMembers = null;

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("orgMemberCount").ValueKind);
    }

    [DbTheory]
    [InlineData(OrgMembership.Member, 403, "not_org_owner")]
    [InlineData(OrgMembership.NotMember, 403, "not_org_member")]
    [InlineData(OrgMembership.NotGranted, 422, "org_members_not_granted")]
    public async Task Organization_installation_needs_an_owner(OrgMembership membership, int status, string error)
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, _, fake, installationId, orgId) = await OrgOwnerAsync(tenant, membership);

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(error, await ErrorAsync(response));
        Assert.Equal(0, await tenant.ScalarAsync<long>(
            "select count(*) from github_connections where account_id = @orgId", new { orgId }));
    }

    [DbFact]
    public async Task Enterprise_installation_gets_422()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, _) = await AdminAsync(tenant);
        fake.Installation = new GitHubInstallation(TestTenant.NextId(), "some-enterprise", "Enterprise", "all");

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("unsupported_account", await ErrorAsync(response));
    }

    private static Task<HttpResponseMessage> CheckAsync(HttpClient client, long? accountId = null) =>
        client.PostAsJsonAsync("/api/connections/github/request/check", new { accountId });

    private static GitHubAppInstallation OrgInstall(long id, long orgId, string login, DateTimeOffset createdAt) =>
        new(id, orgId, login, "Organization", "selected", createdAt);

    [DbFact]
    public async Task Request_is_stored_once_and_shown_to_members()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, _, _) = await AdminAsync(tenant);
        var client = Client(fake, login);

        var first = await client.PostAsync("/api/connections/github/request", null);
        var second = await client.PostAsync("/api/connections/github/request", null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var state = await Client(fake, tenant.ViewerLogin).GetFromJsonAsync<JsonElement>("/api/connections/github/request");
        Assert.Equal(login, state.GetProperty("request").GetProperty("requestedByLogin").GetString());
        Assert.Equal(1, await tenant.ScalarAsync<long>(
            "select count(*) from github_connection_requests where tenant_id = @tid", new { tid = tenant.Id }));
        await using var other = await TestTenant.CreateAsync(_factory);
        var elsewhere = await Client(fake, other.ViewerLogin).GetFromJsonAsync<JsonElement>("/api/connections/github/request");
        Assert.Equal(JsonValueKind.Null, elsewhere.GetProperty("request").ValueKind);

        var cancel = await client.DeleteAsync("/api/connections/github/request");
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/connections/github/request");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("request").ValueKind);
    }

    [DbFact]
    public async Task Request_and_check_need_an_admin()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var client = Client(new FakeGitHub(), tenant.ViewerLogin);

        var create = await client.PostAsync("/api/connections/github/request", null);
        var check = await CheckAsync(client);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("admin_only", await ErrorAsync(create));
        Assert.Equal(HttpStatusCode.Forbidden, check.StatusCode);
    }

    [DbFact]
    public async Task Check_without_a_request_is_404()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, _, _) = await AdminAsync(tenant);

        var response = await CheckAsync(Client(fake, login));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no_request", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Check_stays_pending_until_the_owner_approves_then_connects()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, userId, fake, _, _) = await OrgOwnerAsync(tenant);
        var client = Client(fake, login);
        await client.PostAsync("/api/connections/github/request", null);
        var orgId = TestTenant.NextId();
        var installationId = TestTenant.NextId();
        var olderInstall = TestTenant.NextId();
        fake.AppInstallations = [OrgInstall(olderInstall, TestTenant.NextId(), "old-org", DateTimeOffset.UtcNow.AddDays(-3))];
        fake.Memberships[olderInstall] = OrgMembership.Member;

        var pending = await CheckAsync(client);

        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal("pending", (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Empty(fake.MembershipCalls);

        fake.AppInstallations.Add(OrgInstall(installationId, orgId, "approved-org", DateTimeOffset.UtcNow));
        fake.Memberships[installationId] = OrgMembership.Member;
        fake.OrgMembers = [new GitHubUser(userId, login)];
        var connected = await CheckAsync(client);

        Assert.Equal(HttpStatusCode.Created, connected.StatusCode);
        var body = await connected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("connected", body.GetProperty("status").GetString());
        var connection = body.GetProperty("connection");
        Assert.Equal(orgId, connection.GetProperty("accountId").GetInt64());
        Assert.Equal("organization", connection.GetProperty("accountType").GetString());
        Assert.Equal(installationId, connection.GetProperty("installationId").GetInt64());
        Assert.Equal(1, connection.GetProperty("orgMemberCount").GetInt32());
        Assert.Equal((installationId, "approved-org", userId, login), fake.MembershipCalls.Single());
        var request = (await tenant.QueryAsync(
            "select resolved_at, connection_id, account_login from github_connection_requests where tenant_id = @tid",
            new { tid = tenant.Id })).Single();
        Assert.NotNull(request.resolved_at);
        Assert.Equal(connection.GetProperty("id").GetGuid(), (Guid)request.connection_id);
        Assert.Equal("approved-org", (string)request.account_login);
    }

    [DbFact]
    public async Task Check_ignores_organizations_the_admin_does_not_belong_to()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, _, _) = await AdminAsync(tenant);
        var client = Client(fake, login);
        await client.PostAsync("/api/connections/github/request", null);
        var installationId = TestTenant.NextId();
        var orgId = TestTenant.NextId();
        fake.AppInstallations = [OrgInstall(installationId, orgId, "strangers", DateTimeOffset.UtcNow)];

        var response = await CheckAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pending", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(0, await tenant.ScalarAsync<long>(
            "select count(*) from github_connections where account_id = @orgId", new { orgId }));
    }

    [DbFact]
    public async Task Check_cannot_take_an_installation_connected_to_another_tenant()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        await using var other = await TestTenant.CreateAsync(_factory);
        var (login, fake, _, _) = await AdminAsync(tenant);
        var client = Client(fake, login);
        await client.PostAsync("/api/connections/github/request", null);
        var installationId = TestTenant.NextId();
        var orgId = TestTenant.NextId();
        await other.ExecAsync("""
            insert into github_connections (tenant_id, account_id, account_login, account_type, installation_id)
            values (@tid, @orgId, 'taken-org', 'organization', @installationId)
            """, new { tid = other.Id, orgId, installationId });
        fake.AppInstallations = [OrgInstall(installationId, orgId, "taken-org", DateTimeOffset.UtcNow)];
        fake.Memberships[installationId] = OrgMembership.Owner;

        var response = await CheckAsync(client);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("account_connected", await ErrorAsync(response));
        Assert.Empty(fake.MembershipCalls);
        Assert.Equal(other.Id, await tenant.ScalarAsync<Guid>(
            "select tenant_id from github_connections where account_id = @orgId", new { orgId }));
    }

    [DbFact]
    public async Task Check_with_two_approved_organizations_asks_which_one()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, _, _) = await AdminAsync(tenant);
        var client = Client(fake, login);
        await client.PostAsync("/api/connections/github/request", null);
        var (a, b) = (TestTenant.NextId(), TestTenant.NextId());
        var (orgA, orgB) = (TestTenant.NextId(), TestTenant.NextId());
        fake.AppInstallations = [OrgInstall(a, orgA, "org-a", DateTimeOffset.UtcNow), OrgInstall(b, orgB, "org-b", DateTimeOffset.UtcNow)];
        fake.Memberships[a] = OrgMembership.Member;
        fake.Memberships[b] = OrgMembership.Member;

        var ambiguous = await CheckAsync(client);

        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        var body = await ambiguous.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("multiple_organizations", body.GetProperty("error").GetString());
        Assert.Equal(["org-a", "org-b"], body.GetProperty("organizations").EnumerateArray().Select(e => e.GetProperty("login").GetString()!).Order());

        var picked = await CheckAsync(client, orgB);

        Assert.Equal(HttpStatusCode.Created, picked.StatusCode);
        Assert.Equal(orgB, (await picked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connection").GetProperty("accountId").GetInt64());
    }

    [DbFact]
    public async Task Check_without_members_permission_is_422()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, _, _) = await AdminAsync(tenant);
        var client = Client(fake, login);
        await client.PostAsync("/api/connections/github/request", null);
        var installationId = TestTenant.NextId();
        fake.AppInstallations = [OrgInstall(installationId, TestTenant.NextId(), "no-perm", DateTimeOffset.UtcNow)];
        fake.Memberships[installationId] = OrgMembership.NotGranted;

        var response = await CheckAsync(client);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("org_members_not_granted", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Owner_connecting_directly_closes_the_open_request()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, _, fake, installationId, _) = await OrgOwnerAsync(tenant);
        var client = Client(fake, login);
        await client.PostAsync("/api/connections/github/request", null);

        var response = await PostAsync(client, installationId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var state = await client.GetFromJsonAsync<JsonElement>("/api/connections/github/request");
        Assert.Equal(JsonValueKind.Null, state.GetProperty("request").ValueKind);
    }

    [DbFact]
    public async Task Account_connected_to_another_tenant_gets_409()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        await using var other = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, accountId) = await AdminAsync(tenant);
        await other.ExecAsync(
            "insert into github_connections (tenant_id, account_id, account_login, account_type) values (@tid, @accountId, 'taken', 'user')",
            new { tid = other.Id, accountId });

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("account_connected", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Missing_app_configuration_gets_503()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, _) = await AdminAsync(tenant);

        var response = await PostAsync(Client(fake, login, configured: false), installationId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("github_app_not_configured", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Unauthenticated_request_gets_401()
    {
        using var client = _factory.CreateClient();

        var response = await PostAsync(client, 1);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task App_jwt_is_rs256_issued_by_the_app_id_and_expires_within_ten_minutes()
    {
        using var rsa = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;

        var jwt = new JsonWebToken(GitHubAppJwt.Create(123, rsa, now));

        Assert.Equal("RS256", jwt.Alg);
        Assert.Equal("123", jwt.Issuer);
        var lifetime = jwt.ValidTo - jwt.IssuedAt;
        Assert.True(lifetime <= TimeSpan.FromMinutes(10));
        Assert.True(jwt.IssuedAt <= now.UtcDateTime);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(jwt.EncodedToken, new TokenValidationParameters
        {
            IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false)),
            ValidIssuer = "123",
            ValidateAudience = false,
        });
        Assert.True(result.IsValid);
    }

    public static TheoryData<string> GitHubFailures => ["unreachable", "timeout", "not-json"];

    [DbTheory]
    [MemberData(nameof(GitHubFailures))]
    public async Task GitHub_failures_are_502(string failure)
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, _) = await AdminAsync(tenant);
        fake.Throw = failure switch
        {
            "unreachable" => new HttpRequestException("down"),
            "timeout" => new TaskCanceledException("timed out"),
            _ => new JsonException("html page"),
        };

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("github_unavailable", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Unreadable_private_key_is_503_not_configured()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, _) = await AdminAsync(tenant);
        fake.ThrowOnInstallation = new GitHubAppKeyException("bad key", new IOException("missing"));

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("github_app_not_configured", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Rejected_installation_list_is_invalid_code()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, installationId, _) = await AdminAsync(tenant);
        fake.InstallationsRejected = true;

        var response = await PostAsync(Client(fake, login), installationId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_code", await ErrorAsync(response));
    }

    [DbFact]
    public async Task Missing_installation_id_is_invalid_request()
    {
        await using var tenant = await TestTenant.CreateAsync(_factory);
        var (login, fake, _, _) = await AdminAsync(tenant);

        var response = await Client(fake, login).PostAsJsonAsync("/api/connections/github", new { code = Code });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await ErrorAsync(response));
    }

    [Theory]
    [InlineData("does-not-exist.pem", null)]
    [InlineData("not-a-key.pem", "hello")]
    public void Private_key_that_cannot_be_read_raises_a_key_error(string file, string? content)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        if (content is not null)
            File.WriteAllText(Path.Combine(dir, file), content);
        var key = new GitHubAppKey(
            Microsoft.Extensions.Options.Options.Create(new GitHubAppOptions { PrivateKeyPath = file }),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = dir });

        Assert.Throws<GitHubAppKeyException>(() => key.Get());
    }

    [Fact]
    public void Private_key_is_read_from_a_pem_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        using var rsa = RSA.Create(2048);
        File.WriteAllText(Path.Combine(dir, "app.pem"), rsa.ExportRSAPrivateKeyPem());
        var key = new GitHubAppKey(
            Microsoft.Extensions.Options.Options.Create(new GitHubAppOptions { PrivateKeyPath = "app.pem" }),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = dir });

        Assert.Same(key.Get(), key.Get());
    }
}

internal static class ClientExtensions
{
    public static HttpClient WithDevUser(this HttpClient client, string login)
    {
        client.DefaultRequestHeaders.Add("X-Dev-User", login);
        return client;
    }
}
