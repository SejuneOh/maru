using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class IssueFilterTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient ClientFor(string login)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", login);
        return client;
    }

    [DbTheory]
    [InlineData(0, HttpStatusCode.OK)]
    [InlineData(365, HttpStatusCode.OK)]
    [InlineData(-1, HttpStatusCode.BadRequest)]
    [InlineData(366, HttpStatusCode.BadRequest)]
    [InlineData(int.MaxValue, HttpStatusCode.BadRequest)]
    public async Task Done_within_days_is_bounded_to_0_through_365(int days, HttpStatusCode expected)
    {
        using var client = ClientFor("haneul-dev");

        var response = await client.GetAsync($"/api/issues?doneWithinDays={days}");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
            Assert.Equal("""{"error":"invalid_doneWithinDays"}""", await response.Content.ReadAsStringAsync());
    }

    private static List<string> Ids(JsonElement issues) =>
        issues.EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();

    private static async Task<(TestTenant T, Guid Repo)> NewTenant(WebApplicationFactory<Program> factory)
    {
        var t = await TestTenant.CreateAsync(factory);
        return (t, await t.AddRepoAsync("r"));
    }

    [DbTheory]
    [InlineData("todo", HttpStatusCode.OK)]
    [InlineData("todo,in_progress,in_review,done", HttpStatusCode.OK)]
    [InlineData("todo, done", HttpStatusCode.OK)]
    [InlineData("blocked", HttpStatusCode.BadRequest)]
    [InlineData("todo,blocked", HttpStatusCode.BadRequest)]
    [InlineData("TODO", HttpStatusCode.BadRequest)]
    [InlineData(",", HttpStatusCode.BadRequest)]
    public async Task Status_accepts_only_known_values(string status, HttpStatusCode expected)
    {
        using var client = ClientFor("haneul-dev");

        var response = await client.GetAsync($"/api/issues?status={Uri.EscapeDataString(status)}");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
            Assert.Equal("""{"error":"invalid_status"}""", await response.Content.ReadAsStringAsync());
    }

    [DbFact]
    public async Task Status_filter_keeps_only_listed_statuses()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        var todo = await t.AddIssueAsync(repo);
        var progress = await t.AddIssueAsync(repo);
        await t.BoardAsync(progress, statusOverride: "in_progress");
        var review = await t.AddIssueAsync(repo);
        await t.AddPullRequestAsync(repo, review, review: true);
        var done = await t.AddIssueAsync(repo, closed: true);

        Assert.Equal([todo.ToString()], Ids(await t.GetAsync("/api/issues?status=todo")));
        Assert.Equal(new[] { progress.ToString(), review.ToString() }.Order(),
            Ids(await t.GetAsync("/api/issues?status=in_progress,in_review")).Order());
        Assert.Equal([done.ToString()], Ids(await t.GetAsync("/api/issues?status=done")));
    }

    [DbFact]
    public async Task Stale_keeps_only_open_issues_idle_for_seven_days()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        await t.AddIssueAsync(repo, idle: TimeSpan.FromDays(6) + TimeSpan.FromHours(23));
        var stale = await t.AddIssueAsync(repo, idle: TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        await t.AddIssueAsync(repo, closed: true, closedAt: DateTime.UtcNow, idle: TimeSpan.FromDays(40));

        Assert.Equal([stale.ToString()], Ids(await t.GetAsync("/api/issues?stale=true")));
        Assert.Equal(3, (await t.GetAsync("/api/issues?stale=false")).GetArrayLength());
    }

    [DbFact]
    public async Task Person_and_assignee_mean_the_same_and_person_wins()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        var (ann, annId) = await t.AddMemberAsync("ann");
        var (bob, bobId) = await t.AddMemberAsync("bob");
        var annIssue = await t.AddIssueAsync(repo, assignee: annId);
        var bobIssue = await t.AddIssueAsync(repo, assignee: bobId);

        Assert.Equal([annIssue.ToString()], Ids(await t.GetAsync($"/api/issues?person={ann}")));
        Assert.Equal([annIssue.ToString()], Ids(await t.GetAsync($"/api/issues?assignee={ann}")));
        Assert.Equal([bobIssue.ToString()], Ids(await t.GetAsync($"/api/issues?person={bob}&assignee={ann}")));
    }

    [DbFact]
    public async Task Sprint_filter_and_combination_with_status()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        var sprint = await t.AddSprintAsync("s");
        var inSprint = await t.AddIssueAsync(repo);
        await t.BoardAsync(inSprint, sprint: sprint);
        var started = await t.AddIssueAsync(repo);
        await t.BoardAsync(started, sprint: sprint, statusOverride: "in_progress");
        await t.AddIssueAsync(repo);

        Assert.Equal(new[] { inSprint.ToString(), started.ToString() }.Order(),
            Ids(await t.GetAsync($"/api/issues?sprint={sprint}")).Order());
        Assert.Equal([started.ToString()], Ids(await t.GetAsync($"/api/issues?sprint={sprint}&status=in_progress")));
        Assert.Empty(Ids(await t.GetAsync($"/api/issues?sprint={Guid.NewGuid()}")));
    }

    [DbFact]
    public async Task Scope_all_includes_old_closed_issues_and_default_does_not()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        var old = await t.AddIssueAsync(repo, closed: true, closedAt: DateTime.UtcNow.AddDays(-100));
        var recent = await t.AddIssueAsync(repo, closed: true, closedAt: DateTime.UtcNow.AddDays(-1));
        var open = await t.AddIssueAsync(repo);

        Assert.Equal(new[] { recent.ToString(), open.ToString() }.Order(), Ids(await t.GetAsync("/api/issues")).Order());
        Assert.Equal(new[] { old.ToString(), recent.ToString(), open.ToString() }.Order(),
            Ids(await t.GetAsync("/api/issues?scope=all")).Order());
        Assert.Equal(new[] { old.ToString(), recent.ToString() }.Order(),
            Ids(await t.GetAsync("/api/issues?scope=all&status=done")).Order());
    }

    [DbFact]
    public async Task Default_board_keeps_done_for_fourteen_days()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        var within = await t.AddIssueAsync(repo, closed: true, closedAt: DateTime.UtcNow.AddDays(-10));
        var past = await t.AddIssueAsync(repo, closed: true, closedAt: DateTime.UtcNow.AddDays(-15));

        var ids = Ids(await t.GetAsync("/api/issues"));
        Assert.Contains(within.ToString(), ids);
        Assert.DoesNotContain(past.ToString(), ids);
    }

    [DbFact]
    public async Task Label_filter_by_name_spans_repositories()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        var repo2 = await t.AddRepoAsync("second");
        var a = await t.AddIssueAsync(repo);
        var b = await t.AddIssueAsync(repo2);
        var other = await t.AddIssueAsync(repo);
        // The same label name exists once per repository.
        var l1 = await t.ScalarAsync<Guid>(
            "insert into labels (tenant_id, repository_id, github_label_id, name) values (@tid, @repo, @gid, 'bug') returning id",
            new { tid = t.Id, repo, gid = 9001 });
        var l2 = await t.ScalarAsync<Guid>(
            "insert into labels (tenant_id, repository_id, github_label_id, name) values (@tid, @repo, @gid, 'bug') returning id",
            new { tid = t.Id, repo = repo2, gid = 9002 });
        await t.ExecAsync("insert into issue_labels (tenant_id, issue_id, label_id) values (@tid, @a, @l1), (@tid, @b, @l2)",
            new { tid = t.Id, a, b, l1, l2 });

        var ids = Ids(await t.GetAsync("/api/issues?label=bug")).Order().ToArray();
        Assert.Equal(new[] { a.ToString(), b.ToString() }.Order(), ids);
        Assert.DoesNotContain(other.ToString(), ids);
    }

    [DbFact]
    public async Task Labels_endpoint_lists_each_name_once()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        var repo2 = await t.AddRepoAsync("second");
        await t.ExecAsync(
            "insert into labels (tenant_id, repository_id, github_label_id, name) values (@tid, @r1, 9100, 'bug'), (@tid, @r2, 9101, 'bug')",
            new { tid = t.Id, r1 = repo, r2 = repo2 });

        var res = await t.GetAsync("/api/labels");
        var names = res.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.Single(names, n => n == "bug");
    }

    [DbFact]
    public async Task Repository_filter_keeps_only_that_repository()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        var repo2 = await t.AddRepoAsync("second");
        var here = await t.AddIssueAsync(repo);
        var elsewhere = await t.AddIssueAsync(repo2);

        var ids = Ids(await t.GetAsync($"/api/issues?repository={repo}"));
        Assert.Equal([here.ToString()], ids);
        Assert.DoesNotContain(elsewhere.ToString(), ids);
    }

    [DbFact]
    public async Task Repositories_endpoint_lists_unarchived()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        var archived = await t.AddRepoAsync("old");
        await t.ExecAsync("update repositories set archived = true where id = @id", new { id = archived });

        var res = await t.GetAsync("/api/repositories");
        var names = res.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain("old", names);
    }

    [DbFact]
    public async Task Unknown_scope_is_rejected()
    {
        using var client = ClientFor("haneul-dev");

        var response = await client.GetAsync("/api/issues?scope=everything");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"error":"invalid_scope"}""", await response.Content.ReadAsStringAsync());
    }

    [DbFact]
    public async Task Done_since_week_keeps_issues_closed_since_monday_midnight_in_seoul()
    {
        var (t, repo) = await NewTenant(factory);
        await using var _ = t;
        var kst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, kst);
        var monday = TimeZoneInfo.ConvertTimeToUtc(local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)), kst);
        await t.AddIssueAsync(repo, closed: true, closedAt: monday.AddSeconds(-1));
        var atStart = await t.AddIssueAsync(repo, closed: true, closedAt: monday);
        var after = await t.AddIssueAsync(repo, closed: true, closedAt: monday.AddSeconds(1));
        await t.AddIssueAsync(repo);

        var expected = new[] { atStart.ToString(), after.ToString() }.Order();
        Assert.Equal(expected, Ids(await t.GetAsync("/api/issues?doneSince=week")).Order());
        Assert.Equal(expected, Ids(await t.GetAsync("/api/issues?doneSince=week&status=done")).Order());
        Assert.Equal(expected, Ids(await t.GetAsync("/api/issues?doneSince=week&doneWithinDays=0")).Order());
    }

    [DbFact]
    public async Task Unknown_done_since_is_rejected()
    {
        using var client = ClientFor("haneul-dev");

        var response = await client.GetAsync("/api/issues?doneSince=month");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"error":"invalid_doneSince"}""", await response.Content.ReadAsStringAsync());
    }
}
