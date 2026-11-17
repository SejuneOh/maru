using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class OrganizationImportTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeOrg : IGitHubOrgReader, IGitHubSource
    {
        public List<OrgRepository> Repos { get; set; } = [];
        public List<OrgTeam> Teams { get; set; } = [];
        public List<OrgMemberInfo> Members { get; set; } = [];
        public Dictionary<string, long[]> TeamRepos { get; set; } = [];
        public Exception? Throw { get; set; }
        public int Calls;
        public List<string> Reads { get; } = [];
        public List<GitHubConnectionRef> Refs { get; } = [];

        private T Hit<T>(T value)
        {
            Interlocked.Increment(ref Calls);
            return Throw is { } e ? throw e : value;
        }

        public Task<OrgInfo> GetOrgInfoAsync(GitHubConnectionRef c, CancellationToken ct)
        {
            Refs.Add(c);
            return Task.FromResult(Hit(new OrgInfo(1, c.AccountLogin!, "Org", "About", "https://x", null)));
        }

        public Task<Capped<OrgRepository>> ReadOrgRepositoriesAsync(GitHubConnectionRef c, CancellationToken ct) =>
            Task.FromResult(Hit(new Capped<OrgRepository>(Repos, false)));

        public Task<Capped<OrgTeam>> ReadOrgTeamsAsync(GitHubConnectionRef c, CancellationToken ct) =>
            Task.FromResult(Hit(new Capped<OrgTeam>(Teams, false)));

        public Task<Capped<OrgMemberInfo>> ReadOrgMembersAsync(GitHubConnectionRef c, CancellationToken ct) =>
            Task.FromResult(Hit(new Capped<OrgMemberInfo>(Members, false)));

        public Task<Capped<long>> ReadTeamRepositoryIdsAsync(GitHubConnectionRef c, string slug, CancellationToken ct) =>
            Task.FromResult(Hit(new Capped<long>(TeamRepos.GetValueOrDefault(slug, []), false)));

        public Task<int> CountLabelsAsync(GitHubConnectionRef c, string fullName, CancellationToken ct) => Task.FromResult(Hit(3));

        public Task<int> CountMilestonesAsync(GitHubConnectionRef c, string fullName, CancellationToken ct) => Task.FromResult(Hit(2));

        public Task<RepositoryList> ListRepositoriesAsync(GitHubConnectionRef c, CancellationToken ct) =>
            Task.FromResult(new RepositoryList(
                Repos.Select(r => new SourceRepository(r.Id, r.Name, r.FullName, r.HtmlUrl, r.Archived)).ToList(), null));

        public Task<RepositorySnapshot> ReadRepositoryAsync(
            GitHubConnectionRef c, string owner, string name, DateTimeOffset? since, CancellationToken ct)
        {
            lock (Reads)
                Reads.Add($"{owner}/{name}");
            var r = Repos.Single(x => x.FullName == $"{owner}/{name}");
            return Task.FromResult(new RepositorySnapshot(
                new SourceRepository(r.Id, r.Name, r.FullName, r.HtmlUrl, r.Archived), [], [], [], [], [], null));
        }
    }

    private readonly WebApplicationFactory<Program> _factory;

    public OrganizationImportTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient Client(FakeOrg fake, string login) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:Enabled"] = "false" }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IGitHubOrgReader>();
                s.AddSingleton<IGitHubOrgReader>(fake);
                s.RemoveAll<IGitHubSource>();
                s.AddSingleton<IGitHubSource>(fake);
            });
        }).CreateClient().WithDevUser(login);

    private static async Task<(string Login, long UserId)> AdminAsync(TestTenant t)
    {
        var (login, userId) = await t.AddMemberAsync("admin");
        await t.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_user_id = @userId", new { tid = t.Id, userId });
        return (login, userId);
    }

    private static OrgRepository Repo(string org, string name, bool archived = false) =>
        new(TestTenant.NextId(), name, $"{org}/{name}", "https://x/" + name, archived, true, null);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string?> ErrorAsync(HttpResponseMessage r) => (await JsonAsync(r)).GetProperty("error").GetString();

    private async Task<(TestTenant T, FakeOrg Fake, HttpClient Admin)> SetupAsync()
    {
        var t = await TestTenant.CreateAsync(_factory);
        await t.ExecAsync("update github_connections set auth_kind = 'token', store_bodies = false where id = @id", new { id = t.ConnectionId });
        var (login, _) = await AdminAsync(t);
        var org = "org-" + t.Suffix;
        var fake = new FakeOrg
        {
            Repos = [Repo(org, "api"), Repo(org, "web"), Repo(org, "old", archived: true)],
            Teams = [new OrgTeam(10, "Platform Team", "platform", null, "closed"), new OrgTeam(11, "Design", "design", null, "closed")],
        };
        return (t, fake, Client(fake, login));
    }

    private static string Url(TestTenant t, string tail) => $"/api/connections/{t.ConnectionId}/organization/{tail}";

    [DbFact]
    public async Task Preview_lists_the_organization_and_marks_members_already_on_the_board()
    {
        var (t, fake, admin) = await SetupAsync();
        await using var _ = t;
        var (_, boardUserId) = await t.AddMemberAsync("alice");
        fake.Members = [new OrgMemberInfo(boardUserId, "alice-" + t.Suffix, null), new OrgMemberInfo(TestTenant.NextId(), "outsider", null)];

        var response = await admin.GetAsync(Url(t, "preview"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("org-" + t.Suffix, body.GetProperty("organization").GetProperty("login").GetString());
        Assert.Equal(3, body.GetProperty("repositories").GetArrayLength());
        Assert.Equal(2, body.GetProperty("teams").GetArrayLength());
        var members = body.GetProperty("members").EnumerateArray().ToList();
        Assert.True(members.Single(m => m.GetProperty("login").GetString()!.StartsWith("alice")).GetProperty("onBoard").GetBoolean());
        Assert.False(members.Single(m => m.GetProperty("login").GetString() == "outsider").GetProperty("onBoard").GetBoolean());
        var counts = body.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("countedRepositories").GetInt32());
        Assert.Equal(6, counts.GetProperty("labels").GetInt32());
        Assert.Equal(4, counts.GetProperty("milestones").GetInt32());
        Assert.All(body.GetProperty("repositories").EnumerateArray(), r => Assert.False(r.GetProperty("selected").GetBoolean()));
        Assert.Equal(GitHubAuthKind.Token, fake.Refs[0].Kind);
    }

    [DbFact]
    public async Task Only_admins_can_preview_import_and_update()
    {
        var (t, fake, _) = await SetupAsync();
        await using var _t = t;
        using var viewer = Client(fake, t.ViewerLogin);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Url(t, "preview"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync(Url(t, "import"), new { repositories = new[] { 1L } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PutAsJsonAsync(Url(t, "repositories"), new { repositories = new[] { 1L } })).StatusCode);
        Assert.Equal(0, fake.Calls);
    }

    [DbFact]
    public async Task Another_tenant_gets_404_for_a_connection_it_does_not_own()
    {
        var (owner, fake, _) = await SetupAsync();
        await using var _o = owner;
        await using var other = await TestTenant.CreateAsync(_factory);
        var (otherAdmin, _) = await AdminAsync(other);
        using var intruder = Client(fake, otherAdmin);
        var repoId = fake.Repos[0].Id;

        var preview = await intruder.GetAsync(Url(owner, "preview"));
        var import = await intruder.PostAsJsonAsync(Url(owner, "import"), new { repositories = new[] { repoId } });
        var update = await intruder.PutAsJsonAsync(Url(owner, "repositories"), new { repositories = new[] { repoId } });

        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, import.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal("not_found", await ErrorAsync(preview));
        Assert.Equal(0, fake.Calls);
        Assert.Equal(0, await owner.ScalarAsync<int>("select count(*)::int from repositories where tenant_id = @tid", new { tid = owner.Id }));
    }

    [DbFact]
    public async Task App_connections_and_unknown_ids_are_404()
    {
        var (t, _, admin) = await SetupAsync();
        await using var _ = t;
        await t.ExecAsync("update github_connections set auth_kind = 'app' where id = @id", new { id = t.ConnectionId });

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Url(t, "preview"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/connections/{Guid.NewGuid()}/organization/preview")).StatusCode);
    }

    [DbFact]
    public async Task Import_selects_repositories_creates_team_projects_and_stores_members_only_as_references()
    {
        var (t, fake, admin) = await SetupAsync();
        await using var _ = t;
        var (api, web) = (fake.Repos[0], fake.Repos[1]);
        fake.TeamRepos["platform"] = [api.Id, web.Id, fake.Repos[2].Id];
        fake.TeamRepos["design"] = [fake.Repos[2].Id];
        var memberId = TestTenant.NextId();
        fake.Members = [new OrgMemberInfo(memberId, "newcomer-" + t.Suffix, "https://a/1")];
        var membersBefore = await t.ScalarAsync<int>("select count(*)::int from members where tenant_id = @tid", new { tid = t.Id });

        var response = await admin.PostAsJsonAsync(Url(t, "import"),
            new { repositories = new[] { api.Id }, teamProjects = new[] { "platform", "design" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(1, body.GetProperty("selected").GetInt32());
        var project = Assert.Single(body.GetProperty("projects").EnumerateArray());
        Assert.Equal("Platform Team", project.GetProperty("name").GetString());
        Assert.Equal(1, project.GetProperty("repositoryCount").GetInt32());
        Assert.True(project.GetProperty("created").GetBoolean());
        Assert.Equal(["design"], body.GetProperty("skippedTeams").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(1, await t.ScalarAsync<int>(
            "select count(*)::int from repositories where tenant_id = @tid and sync_enabled", new { tid = t.Id }));
        Assert.Equal(1, await t.ScalarAsync<int>(
            "select count(*)::int from github_users where tenant_id = @tid and github_user_id = @memberId", new { tid = t.Id, memberId }));
        Assert.Equal(membersBefore, await t.ScalarAsync<int>("select count(*)::int from members where tenant_id = @tid", new { tid = t.Id }));
        Assert.Equal(1, await t.ScalarAsync<int>(
            "select count(*)::int from audit_log where tenant_id = @tid and action = 'connection.org_import'", new { tid = t.Id }));
    }

    [DbFact]
    public async Task Import_reuses_a_project_with_the_same_name()
    {
        var (t, fake, admin) = await SetupAsync();
        await using var _ = t;
        var existing = await t.AddProjectAsync("Platform Team");
        fake.TeamRepos["platform"] = [fake.Repos[0].Id];

        var body = await JsonAsync(await admin.PostAsJsonAsync(Url(t, "import"),
            new { repositories = new[] { fake.Repos[0].Id }, teamProjects = new[] { "platform" } }));

        var project = body.GetProperty("projects")[0];
        Assert.Equal(existing, project.GetProperty("id").GetGuid());
        Assert.False(project.GetProperty("created").GetBoolean());
    }

    [DbFact]
    public async Task Import_rejects_bad_input()
    {
        var (t, fake, admin) = await SetupAsync();
        await using var _ = t;
        var known = fake.Repos[0].Id;

        var empty = await admin.PostAsJsonAsync(Url(t, "import"), new { repositories = Array.Empty<long>() });
        var missing = await admin.PostAsJsonAsync(Url(t, "import"), new { });
        var unknownRepo = await admin.PostAsJsonAsync(Url(t, "import"), new { repositories = new[] { known, 999999999L } });
        var unknownTeam = await admin.PostAsJsonAsync(Url(t, "import"), new { repositories = new[] { known }, teamProjects = new[] { "nope" } });
        var badSlug = await admin.PostAsJsonAsync(Url(t, "import"), new { repositories = new[] { known }, teamProjects = new[] { "a b/c" } });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badSlug.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownRepo.StatusCode);
        Assert.Equal("unknown_repository", await ErrorAsync(unknownRepo));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownTeam.StatusCode);
        Assert.Equal("unknown_team", await ErrorAsync(unknownTeam));
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from repositories where tenant_id = @tid", new { tid = t.Id }));
    }

    [DbFact]
    public async Task GitHub_failures_map_to_documented_errors()
    {
        var (t, fake, admin) = await SetupAsync();
        await using var _ = t;

        fake.Throw = new GitHubSourceException(GitHubSourceErrorKind.RateLimited, "limit");
        var limited = await admin.GetAsync(Url(t, "preview"));
        fake.Throw = new GitHubSourceException(GitHubSourceErrorKind.Unauthorized, "gone");
        var revoked = await admin.GetAsync(Url(t, "preview"));
        fake.Throw = new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "down");
        var down = await admin.PutAsJsonAsync(Url(t, "repositories"), new { repositories = new[] { fake.Repos[0].Id } });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("github_rate_limited", await ErrorAsync(limited));
        Assert.Equal(HttpStatusCode.Conflict, revoked.StatusCode);
        Assert.Equal("credential_missing", await ErrorAsync(revoked));
        Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
        Assert.Equal("github_unavailable", await ErrorAsync(down));
    }

    private static async Task WaitIdleAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            var status = await client.GetFromJsonAsync<JsonElement>("/api/sync/status");
            if (!status.GetProperty("running").GetBoolean())
                return;
        }
        throw new TimeoutException("sync did not finish");
    }

    [DbFact]
    public async Task Sync_reads_only_selected_repositories_and_deselected_ones_are_kept_but_hidden()
    {
        var (t, fake, admin) = await SetupAsync();
        await using var _ = t;
        var (api, web) = (fake.Repos[0], fake.Repos[1]);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(Url(t, "import"), new { repositories = new[] { api.Id } })).StatusCode);

        await admin.PostAsync("/api/sync/run", null);
        await WaitIdleAsync(admin);

        Assert.Equal([api.FullName], fake.Reads.ToArray());
        var rows = await t.QueryAsync("select github_repo_id, sync_enabled from repositories where tenant_id = @tid", new { tid = t.Id });
        Assert.Equal(3, rows.Count);
        Assert.Equal([api.Id], rows.Where(r => r.sync_enabled).Select(r => (long)r.github_repo_id).ToArray());

        var apiRepo = await t.ScalarAsync<Guid>("select id from repositories where tenant_id = @tid and github_repo_id = @id", new { tid = t.Id, id = api.Id });
        var issue = await t.AddIssueAsync(apiRepo, title: "visible-" + t.Suffix);
        var project = await t.AddProjectAsync("p-" + t.Suffix, apiRepo);
        var listed = await admin.GetFromJsonAsync<JsonElement>("/api/issues");
        Assert.Contains(listed.EnumerateArray(), i => i.GetProperty("id").GetGuid() == issue);

        var swap = await admin.PutAsJsonAsync(Url(t, "repositories"), new { repositories = new[] { web.Id } });
        Assert.Equal(HttpStatusCode.OK, swap.StatusCode);
        var result = await JsonAsync(swap);
        Assert.Equal(1, result.GetProperty("selected").GetInt32());
        Assert.Equal(1, result.GetProperty("deselected").GetInt32());
        fake.Reads.Clear();
        await admin.PostAsync("/api/sync/run", null);
        await WaitIdleAsync(admin);
        Assert.Equal([web.FullName], fake.Reads.ToArray());
        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from issues where id = @issue", new { issue }));
        listed = await admin.GetFromJsonAsync<JsonElement>("/api/issues");
        Assert.DoesNotContain(listed.EnumerateArray(), i => i.GetProperty("id").GetGuid() == issue);

        await admin.PutAsJsonAsync(Url(t, "repositories"), new { repositories = new[] { api.Id, web.Id } });
        listed = await admin.GetFromJsonAsync<JsonElement>("/api/issues");
        Assert.Contains(listed.EnumerateArray(), i => i.GetProperty("id").GetGuid() == issue);

        var projectRepos = (await admin.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray()
            .Where(p => p.GetProperty("id").GetGuid() == project)
            .SelectMany(p => p.GetProperty("repositories").EnumerateArray()).Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(apiRepo, projectRepos);
        await admin.PutAsJsonAsync(Url(t, "repositories"), new { repositories = new[] { web.Id } });
        projectRepos = (await admin.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray()
            .Where(p => p.GetProperty("id").GetGuid() == project)
            .SelectMany(p => p.GetProperty("repositories").EnumerateArray()).Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(apiRepo, projectRepos);
        await admin.PutAsJsonAsync(Url(t, "repositories"), new { repositories = new[] { api.Id, web.Id } });

        var none = await admin.PutAsJsonAsync(Url(t, "repositories"), new { repositories = Array.Empty<long>() });
        Assert.Equal(0, (await JsonAsync(none)).GetProperty("selected").GetInt32());
    }

    // ---- GitHub reader -------------------------------------------------------------------------

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubCredentials : IGitHubCredentials
    {
        public Task<GitHubCredential> GetAsync(GitHubConnectionRef connection, CancellationToken ct) =>
            Task.FromResult(new GitHubCredential("ghp_0123456789abcdefghijklmnopqrstuvwxyz", GitHubAuthKind.Token));

        public void Invalidate(GitHubConnectionRef connection) { }
    }

    private static HttpResponseMessage Ok(string json, string? link = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (link is not null)
            r.Headers.TryAddWithoutValidation("Link", link);
        return r;
    }

    private static GitHubConnectionRef Ref() => new(Guid.Empty, Guid.Empty, GitHubAuthKind.Token, null, "acme", null);

    [Fact]
    public async Task Reader_counts_from_the_last_page_link_and_falls_back_to_the_body()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/labels")
            ? Ok("""[{"id":1}]""", """<https://api.github.com/repositories/1/labels?per_page=1&page=2>; rel="next", <https://api.github.com/repositories/1/labels?per_page=1&page=17>; rel="last" """)
            : Ok("[]"));
        var reader = new GitHubSource(new HttpClient(handler), new StubCredentials());

        Assert.Equal(17, await reader.CountLabelsAsync(Ref(), "acme/api", default));
        Assert.Equal(0, await reader.CountMilestonesAsync(Ref(), "acme/api", default));
        Assert.Contains(handler.Requests, u => u.Query.Contains("state=all"));
    }

    [Fact]
    public async Task Reader_caps_pages_and_reports_truncation()
    {
        var handler = new Handler(_ =>
            Ok("[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $$"""{"id":{{i}},"login":"u{{i}}"}""")) + "]"));
        var reader = new GitHubSource(new HttpClient(handler), new StubCredentials());

        var members = await reader.ReadOrgMembersAsync(Ref(), default);

        Assert.True(members.Truncated);
        Assert.Equal(IGitHubOrgReader.MaxPages, handler.Requests.Count);
        Assert.All(handler.Requests, u => Assert.StartsWith("/orgs/acme/members", u.AbsolutePath));
    }
}
