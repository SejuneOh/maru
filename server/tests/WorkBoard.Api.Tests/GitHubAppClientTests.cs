using System.Net;
using System.Security.Cryptography;
using System.Text;
using WorkBoard.Api.Infrastructure.GitHub;

namespace WorkBoard.Api.Tests;

public class GitHubAppClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Auth)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeTokens : IGitHubInstallationTokens
    {
        public int Invalidations { get; private set; }
        private int _mints;

        public Task<string> GetAsync(long installationId, CancellationToken ct) => Task.FromResult($"tok-{++_mints}");

        public void Invalidate(long installationId) => Invalidations++;
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (GitHubAppClient Client, FakeHandler Handler, FakeTokens Tokens) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        using var rsa = RSA.Create(2048);
        File.WriteAllText(Path.Combine(dir, "app.pem"), rsa.ExportRSAPrivateKeyPem());
        var options = Microsoft.Extensions.Options.Options.Create(new GitHubAppOptions { AppId = 1, PrivateKeyPath = "app.pem" });
        var key = new GitHubAppKey(options, new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = dir });
        var handler = new FakeHandler(respond);
        var tokens = new FakeTokens();
        return (new GitHubAppClient(new HttpClient(handler), options, key, tokens), handler, tokens);
    }

    private static string Membership(long userId, string state, string role) =>
        $$$"""{"state":"{{{state}}}","role":"{{{role}}}","user":{"id":{{{userId}}},"login":"alice"}}""";

    [Theory]
    [InlineData(7, "active", "admin", OrgMembership.Owner)]
    [InlineData(7, "active", "member", OrgMembership.Member)]
    [InlineData(7, "pending", "admin", OrgMembership.NotMember)]
    [InlineData(8, "active", "admin", OrgMembership.NotMember)]
    public async Task Membership_counts_only_an_active_one_of_the_same_user_id(long returnedId, string state, string role, OrgMembership expected)
    {
        var (client, handler, _) = Make(_ => Json(HttpStatusCode.OK, Membership(returnedId, state, role)));

        var result = await client.GetOrganizationMembershipAsync(5, "test-org", 7, "alice", CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.Equal("https://api.github.com/orgs/test-org/memberships/alice", handler.Requests.Single().Uri.ToString());
        Assert.Equal("Bearer tok-1", handler.Requests.Single().Auth);
    }

    [Fact]
    public async Task Membership_404_is_not_a_member_and_403_is_not_granted()
    {
        var (missing, _, _) = Make(_ => Json(HttpStatusCode.NotFound, "{}"));
        var (forbidden, _, _) = Make(_ => Json(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}"""));

        Assert.Equal(OrgMembership.NotMember, await missing.GetOrganizationMembershipAsync(5, "test-org", 7, "alice", CancellationToken.None));
        Assert.Equal(OrgMembership.NotGranted, await forbidden.GetOrganizationMembershipAsync(5, "test-org", 7, "alice", CancellationToken.None));
    }

    [Fact]
    public async Task Rate_limited_403_is_an_error_not_a_missing_permission()
    {
        var (client, _, _) = Make(_ =>
        {
            var r = Json(HttpStatusCode.Forbidden, "{}");
            r.Headers.Add("x-ratelimit-remaining", "0");
            return r;
        });

        var e = await Assert.ThrowsAsync<GitHubSourceException>(() =>
            client.GetOrganizationMembershipAsync(5, "test-org", 7, "alice", CancellationToken.None));
        Assert.Equal(GitHubSourceErrorKind.RateLimited, e.Kind);
    }

    [Fact]
    public async Task Membership_retries_once_with_a_new_token_after_401()
    {
        var calls = 0;
        var (client, handler, tokens) = Make(_ => ++calls == 1
            ? Json(HttpStatusCode.Unauthorized, "{}")
            : Json(HttpStatusCode.OK, Membership(7, "active", "member")));

        var result = await client.GetOrganizationMembershipAsync(5, "test-org", 7, "alice", CancellationToken.None);

        Assert.Equal(OrgMembership.Member, result);
        Assert.Equal(1, tokens.Invalidations);
        Assert.Equal("Bearer tok-2", handler.Requests[1].Auth);
    }

    [Fact]
    public async Task Invalid_logins_never_reach_github()
    {
        var (client, handler, _) = Make(_ => Json(HttpStatusCode.OK, Membership(7, "active", "admin")));

        Assert.Equal(OrgMembership.NotMember, await client.GetOrganizationMembershipAsync(5, "../user", 7, "alice", CancellationToken.None));
        Assert.Equal(OrgMembership.NotMember, await client.GetOrganizationMembershipAsync(5, "test-org", 7, "a/b", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Members_are_paged_and_null_without_the_permission()
    {
        var page1 = "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => $"{{\"id\":{i},\"login\":\"u{i}\"}}")) + "]";
        var (client, handler, _) = Make(r => Json(HttpStatusCode.OK, r.RequestUri!.Query.EndsWith("&page=1") ? page1 : """[{"id":101,"login":"u101"}]"""));
        var (denied, _, _) = Make(_ => Json(HttpStatusCode.Forbidden, "{}"));

        var members = await client.ListOrganizationMembersAsync(5, "test-org", CancellationToken.None);

        Assert.Equal(101, members!.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(await denied.ListOrganizationMembersAsync(5, "test-org", CancellationToken.None));
    }

    [Fact]
    public async Task App_installations_are_listed_with_the_app_jwt()
    {
        var (client, handler, _) = Make(_ => Json(HttpStatusCode.OK, """
            [{"id":9,"account":{"id":3,"login":"test-org","type":"Organization"},"repository_selection":"all",
              "created_at":"2026-11-10T01:02:03Z"},
             {"id":10,"account":null,"repository_selection":"all","created_at":"2026-11-10T01:02:03Z"}]
            """));

        var list = await client.ListAppInstallationsAsync(CancellationToken.None);

        var only = Assert.Single(list);
        Assert.Equal(new GitHubAppInstallation(9, 3, "test-org", "Organization", "all",
            new DateTimeOffset(2026, 11, 10, 1, 2, 3, TimeSpan.Zero)), only);
        Assert.StartsWith("https://api.github.com/app/installations?", handler.Requests.Single().Uri.ToString());
        Assert.DoesNotContain("tok-", handler.Requests.Single().Auth);
    }
}
