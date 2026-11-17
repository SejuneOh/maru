using Microsoft.AspNetCore.Mvc.Testing;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class TrendTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly TimeZoneInfo Seoul = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");

    private static DateTime SeoulToday() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Seoul).Date;

    private static DateTime MondayUtc(int weeksAgo)
    {
        var today = SeoulToday();
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7) - 7 * weeksAgo);
        return TimeZoneInfo.ConvertTimeToUtc(monday, Seoul);
    }

    private static Task<int> DailyAsync(TestTenant t, Guid sprint, int dayOffset, int scope, int done, int scopeW, int doneW) =>
        t.ExecAsync("""
            insert into sprint_daily (tenant_id, sprint_id, day, scope_count, scope_weight, done_count, done_weight)
            values (@tid, @sprint, (now() at time zone 'Asia/Seoul')::date + @dayOffset, @scope, @scopeW, @done, @doneW)
            """, new { tid = t.Id, sprint, dayOffset, scope, scopeW, done, doneW });

    [DbFact]
    public async Task Sprint_trend_lists_recorded_days_with_remaining_work()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var sprint = await t.AddSprintAsync("now");
        await DailyAsync(t, sprint, -2, scope: 6, done: 0, scopeW: 10, doneW: 0);
        await DailyAsync(t, sprint, 0, scope: 7, done: 3, scopeW: 11, doneW: 5);

        var trend = (await t.OverviewAsync()).GetProperty("sprint").GetProperty("trend").EnumerateArray().ToList();

        Assert.Equal(2, trend.Count);
        Assert.Equal(SeoulToday().AddDays(-2).ToString("yyyy-MM-dd"), trend[0].GetProperty("day").GetString());
        Assert.Equal(6, trend[0].GetProperty("scope").GetInt32());
        Assert.Equal(6, trend[0].GetProperty("remaining").GetInt32());
        Assert.Equal(7, trend[1].GetProperty("scope").GetInt32());
        Assert.Equal(4, trend[1].GetProperty("remaining").GetInt32());
        Assert.Equal(11, trend[1].GetProperty("scopeWeight").GetInt32());
        Assert.Equal(6, trend[1].GetProperty("remainingWeight").GetInt32());
    }

    [DbFact]
    public async Task Sprint_trend_is_empty_before_the_first_record_and_ignores_other_sprints()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var current = await t.AddSprintAsync("now");
        var other = await t.AddSprintAsync("other", "closed");
        await DailyAsync(t, other, 0, scope: 3, done: 1, scopeW: 3, doneW: 1);

        var sprint = (await t.OverviewAsync()).GetProperty("sprint");

        Assert.Equal(current.ToString(), sprint.GetProperty("id").GetString());
        Assert.Equal(0, sprint.GetProperty("trend").GetArrayLength());
    }

    [DbFact]
    public async Task Done_by_week_counts_completed_closes_per_seoul_week_for_eight_weeks()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await t.AddRepoAsync("r");
        var otherRepo = await t.AddRepoAsync("o");
        var project = await t.AddProjectAsync("a", repo);
        var quiet = await t.AddProjectAsync("b", otherRepo);

        await t.AddIssueAsync(repo, closed: true, closedAt: MondayUtc(0));                          // this week, at the boundary
        await t.AddIssueAsync(repo, closed: true, closedAt: MondayUtc(0).AddSeconds(-1));           // last week
        await t.AddIssueAsync(repo, closed: true, closedAt: MondayUtc(7).AddHours(1));              // oldest week shown
        await t.AddIssueAsync(repo, closed: true, closedAt: MondayUtc(7).AddSeconds(-1));           // before the window
        var cancelled = await t.AddIssueAsync(repo, closed: true, closedAt: MondayUtc(0).AddHours(2));
        await t.ExecAsync("update issues set state_reason = 'not_planned' where id = @cancelled", new { cancelled });
        var reclosed = await t.AddIssueAsync(repo, closed: true, closedAt: MondayUtc(0).AddHours(3));
        await t.ExecAsync("update issues set state_reason = 'completed' where id = @reclosed", new { reclosed });
        await t.AddIssueAsync(repo);                                                                  // open

        var result = await t.GetAsync("/api/projects/done-by-week");

        var weeks = result.GetProperty("weeks").EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Equal(8, weeks.Count);
        Assert.Equal(TimeZoneInfo.ConvertTimeFromUtc(MondayUtc(0), Seoul).ToString("yyyy-MM-dd"), weeks[^1]);
        Assert.Equal(TimeZoneInfo.ConvertTimeFromUtc(MondayUtc(7), Seoul).ToString("yyyy-MM-dd"), weeks[0]);

        var byProject = result.GetProperty("projects").EnumerateArray()
            .ToDictionary(p => p.GetProperty("projectId").GetString()!, p => p.GetProperty("done").EnumerateArray().Select(n => n.GetInt32()).ToList());
        Assert.Equal([1, 0, 0, 0, 0, 0, 1, 2], byProject[project.ToString()]);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0], byProject[quiet.ToString()]);
    }

    [DbFact]
    public async Task Done_by_week_is_tenant_scoped()
    {
        await using var a = await TestTenant.CreateAsync(factory);
        await using var b = await TestTenant.CreateAsync(factory);
        var repo = await b.AddRepoAsync("r");
        await b.AddProjectAsync("b", repo);
        await b.AddIssueAsync(repo, closed: true);

        var result = await a.GetAsync("/api/projects/done-by-week");

        Assert.Equal(0, result.GetProperty("projects").GetArrayLength());
    }
}
