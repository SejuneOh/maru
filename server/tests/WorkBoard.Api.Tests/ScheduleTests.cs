using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class ScheduleTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly TimeZoneInfo Seoul = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");

    private static DateTime SeoulToday() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Seoul).Date;

    private static string Day(int offset) => SeoulToday().AddDays(offset).ToString("yyyy-MM-dd");

    /// <summary>GitHub stores a due date as a time on that day; 08:00 UTC is the same calendar day in Seoul.</summary>
    private static DateTime DueUtc(int offset) => DateTime.SpecifyKind(SeoulToday().AddDays(offset).AddHours(8), DateTimeKind.Utc);

    private static async Task<Guid> AddMilestoneAsync(
        TestTenant t, Guid repo, int number, string title, string state = "open", DateTime? dueOn = null, DateTime? closedAt = null)
    {
        var id = Guid.NewGuid();
        await t.ExecAsync("""
            insert into milestones (id, tenant_id, repository_id, github_milestone_id, number, title, state, due_on, closed_at, html_url)
            values (@id, @tid, @repo, @gid, @number, @title, @state, @dueOn, @closedAt, @url)
            """, new
        {
            id, tid = t.Id, repo, gid = TestTenant.NextId(), number, title, state, dueOn, closedAt,
            url = $"https://github.com/o/r/milestone/{number}",
        });
        return id;
    }

    private static async Task<Guid> AddSprintAsync(TestTenant t, string name, int start, int end, string state = "active")
    {
        var id = Guid.NewGuid();
        await t.ExecAsync("""
            insert into sprints (id, tenant_id, name, start_date, end_date, state)
            values (@id, @tid, @name, cast(@start as date), cast(@end as date), @state)
            """, new { id, tid = t.Id, name, start = Day(start), end = Day(end), state });
        return id;
    }

    private static Task<int> MilestoneOfAsync(TestTenant t, Guid issue, Guid milestone) =>
        t.ExecAsync("update issues set milestone_id = @milestone where id = @issue", new { issue, milestone });

    private static Dictionary<string, JsonElement> ByName(JsonElement list, string key) =>
        list.EnumerateArray().ToDictionary(e => e.GetProperty(key).GetString()!);

    [DbFact]
    public async Task Milestones_come_from_the_project_repositories_with_seoul_dates_and_overdue_days()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await t.AddRepoAsync("r");
        var other = await t.AddRepoAsync("o");
        var project = await t.AddProjectAsync("p", repo);

        var late = await AddMilestoneAsync(t, repo, 1, "late", dueOn: DueUtc(-3));
        await AddMilestoneAsync(t, repo, 2, "soon", dueOn: DueUtc(4));
        await AddMilestoneAsync(t, repo, 3, "undated");
        await AddMilestoneAsync(t, repo, 4, "shipped", "closed", DueUtc(-10), closedAt: DateTime.UtcNow.AddDays(-9));
        await AddMilestoneAsync(t, repo, 5, "ancient", "closed", DueUtc(-200), closedAt: DateTime.UtcNow.AddDays(-190));
        await AddMilestoneAsync(t, other, 1, "elsewhere", dueOn: DueUtc(1));

        await MilestoneOfAsync(t, await t.AddIssueAsync(repo), late);
        await MilestoneOfAsync(t, await t.AddIssueAsync(repo, closed: true), late);
        var cancelled = await t.AddIssueAsync(repo, closed: true);
        await t.ExecAsync("update issues set state_reason = 'not_planned' where id = @cancelled", new { cancelled });
        await MilestoneOfAsync(t, cancelled, late);

        var result = await t.GetAsync($"/api/projects/{project}/schedule");

        Assert.Equal(Day(0), result.GetProperty("today").GetString());
        var milestones = result.GetProperty("milestones");
        Assert.Equal(["late", "shipped", "soon", "undated"],
            milestones.EnumerateArray().Select(m => m.GetProperty("title").GetString()).Order());
        var m = ByName(milestones, "title");
        Assert.Equal(Day(-3), m["late"].GetProperty("dueDate").GetString());
        Assert.Equal(3, m["late"].GetProperty("overdueDays").GetInt32());
        Assert.Equal(2, m["late"].GetProperty("issueCount").GetInt32());
        Assert.Equal(1, m["late"].GetProperty("doneCount").GetInt32());
        Assert.Equal("r", m["late"].GetProperty("repository").GetProperty("name").GetString());
        Assert.Equal(0, m["soon"].GetProperty("overdueDays").GetInt32());
        Assert.Equal(0, m["shipped"].GetProperty("overdueDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, m["undated"].GetProperty("dueDate").ValueKind);
        Assert.Equal(0, m["undated"].GetProperty("overdueDays").GetInt32());
    }

    [DbFact]
    public async Task Sprints_count_only_the_project_issues_and_flag_ended_ones_with_unfinished_work()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await t.AddRepoAsync("r");
        var other = await t.AddRepoAsync("o");
        var project = await t.AddProjectAsync("p", repo);

        var now = await AddSprintAsync(t, "now", -2, 5);
        await AddSprintAsync(t, "next", 6, 12, "planned");
        var leftover = await AddSprintAsync(t, "leftover", -16, -3);
        var finished = await AddSprintAsync(t, "finished", -30, -17);
        await AddSprintAsync(t, "unrelated", -40, -31);

        await t.BoardAsync(await t.AddIssueAsync(repo), sprint: now);
        await t.BoardAsync(await t.AddIssueAsync(repo, closed: true), sprint: now);
        await t.BoardAsync(await t.AddIssueAsync(other), sprint: now);
        await t.BoardAsync(await t.AddIssueAsync(repo), sprint: leftover);
        await t.BoardAsync(await t.AddIssueAsync(repo, closed: true), sprint: finished);

        var result = await t.GetAsync($"/api/projects/{project}/schedule");

        Assert.True(result.GetProperty("sprintsEnabled").GetBoolean());
        var sprints = result.GetProperty("sprints");
        Assert.Equal(["finished", "leftover", "now", "next"],
            sprints.EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        var s = ByName(sprints, "name");
        Assert.Equal(2, s["now"].GetProperty("issueCount").GetInt32());
        Assert.Equal(1, s["now"].GetProperty("doneCount").GetInt32());
        Assert.Equal("active", s["now"].GetProperty("state").GetString());
        Assert.Equal(0, s["now"].GetProperty("overdueDays").GetInt32());
        Assert.Equal("planned", s["next"].GetProperty("state").GetString());
        Assert.Equal(Day(6), s["next"].GetProperty("startDate").GetString());
        Assert.Equal("closed", s["leftover"].GetProperty("state").GetString());
        Assert.Equal(3, s["leftover"].GetProperty("overdueDays").GetInt32());
        Assert.Equal(1, s["leftover"].GetProperty("unfinishedCount").GetInt32());
        Assert.Equal(0, s["finished"].GetProperty("overdueDays").GetInt32());
    }

    [DbFact]
    public async Task Sprints_are_empty_when_the_tenant_has_them_off()
    {
        await using var t = await TestTenant.CreateAsync(factory, sprints: false);
        var project = await t.AddProjectAsync("p");
        await AddSprintAsync(t, "now", -2, 5);

        var result = await t.GetAsync($"/api/projects/{project}/schedule");

        Assert.False(result.GetProperty("sprintsEnabled").GetBoolean());
        Assert.Equal(0, result.GetProperty("sprints").GetArrayLength());
        Assert.Equal(0, result.GetProperty("milestones").GetArrayLength());
    }

    [DbFact]
    public async Task Another_tenants_project_is_not_found()
    {
        await using var a = await TestTenant.CreateAsync(factory);
        await using var b = await TestTenant.CreateAsync(factory);
        var project = await b.AddProjectAsync("b");

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", a.ViewerLogin);
        var response = await client.GetAsync($"/api/projects/{project}/schedule");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
