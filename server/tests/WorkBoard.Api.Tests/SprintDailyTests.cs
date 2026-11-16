using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class SprintDailyTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private int _tick;

    private SprintDailyRecorder Recorder => factory.Services.GetRequiredService<SprintDailyRecorder>();

    private MirrorWriter Writer() => new(factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.Sync));

    private async Task AddToSprintAsync(TestTenant t, Guid sprint, params Guid[] issues)
    {
        using var client = factory.CreateClient().WithDevUser(t.ViewerLogin);
        var response = await client.PostAsJsonAsync($"/api/sprints/{sprint}/issues", new { issueIds = issues });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Board size changes write board_fields and a size_set event together, as PATCH /board does.
    private static async Task SetSizeAsync(TestTenant t, Guid issue, string size)
    {
        await t.ExecAsync("update board_fields set size = @size where issue_id = @issue", new { size, issue });
        await t.ExecAsync("insert into issue_events (tenant_id, issue_id, kind, to_value) values (@tid, @issue, 'size_set', @size)",
            new { tid = t.Id, issue, size });
    }

    // Applies one GitHub state for the issue through the sync path.
    private async Task SyncStateAsync(TestTenant t, Guid issue, string state, string? reason)
    {
        var row = (await t.QueryAsync("""
            select i.github_issue_id, i.number, r.github_repo_id, r.name, r.full_name
              from issues i join repositories r on r.id = i.repository_id where i.id = @issue
            """, new { issue })).Single();
        var updated = DateTimeOffset.UtcNow.AddMinutes(1 + ++_tick);
        var source = new SourceIssue((long)row.github_issue_id, "n" + row.github_issue_id, (int)row.number, "issue", null, null,
            state, reason, null, T0, state == "closed" ? updated : (DateTimeOffset?)null, updated, "https://x/" + row.number, null, Array.Empty<SourceLabel>(), Array.Empty<SourceUser>(), null, null);
        var repo = new SourceRepository((long)row.github_repo_id, (string)row.name, (string)row.full_name, "https://x/r", false);
        await Writer().ApplyAsync(t.Id, t.ConnectionId,
            new RepositorySnapshot(repo, [], [], [source], [], [], null), default);
    }

    private sealed record Daily(int ScopeCount, int ScopeWeight, int DoneCount, int DoneWeight);

    private static async Task<List<Daily>> RowsAsync(TestTenant t, Guid sprint, DateOnly day) =>
        (await t.QueryAsync("""
            select scope_count, scope_weight, done_count, done_weight from sprint_daily
             where tenant_id = @tid and sprint_id = @sprint and day = @day
            """, new { tid = t.Id, sprint, day = day.ToDateTime(TimeOnly.MinValue) }))
        .Select(r => new Daily((int)r.scope_count, (int)r.scope_weight, (int)r.done_count, (int)r.done_weight)).ToList();

    // The same numbers rebuilt from issue_events alone: membership from sprint_added / carried_over / sprint_removed,
    // size from size_set, finished and cancelled from closed / reopened. Same-transaction ties put the removal first.
    private static async Task<Daily> ReconstructAsync(TestTenant t, Guid sprint)
    {
        var rows = await t.QueryAsync("""
            with member as (
              select distinct on (issue_id) issue_id,
                     case when kind = 'sprint_removed' then null else sprint_id end as sprint_id
                from issue_events
               where tenant_id = @tid and kind in ('sprint_added', 'sprint_removed', 'carried_over')
               order by issue_id, at desc, (kind = 'sprint_removed') desc
            ), size as (
              select distinct on (issue_id) issue_id, to_value as size
                from issue_events where tenant_id = @tid and kind = 'size_set' order by issue_id, at desc
            ), state as (
              select distinct on (issue_id) issue_id, kind, to_value
                from issue_events where tenant_id = @tid and kind in ('closed', 'reopened') order by issue_id, at desc
            )
            select (case s.size when 'M' then 2 when 'L' then 3 else 1 end) as weight,
                   case when st.kind = 'closed' and st.to_value = 'not_planned' then 'cancelled'
                        when st.kind = 'closed' then 'done' else 'open' end as status
              from member m
              left join size s on s.issue_id = m.issue_id
              left join state st on st.issue_id = m.issue_id
             where m.sprint_id = @sprint
            """, new { tid = t.Id, sprint });
        var live = rows.Where(r => (string)r.status != "cancelled").ToList();
        var done = live.Where(r => (string)r.status == "done").ToList();
        return new Daily(live.Count, live.Sum(r => (int)r.weight), done.Count, done.Sum(r => (int)r.weight));
    }

    private async Task<(Guid Sprint, Guid[] Issues)> SeedAsync(TestTenant t)
    {
        var repo = await t.AddRepoAsync("api");
        var sprint = await t.AddSprintAsync("A");
        var other = await t.AddSprintAsync("B");
        var issues = new Guid[5];
        for (var i = 0; i < issues.Length; i++)
            issues[i] = await t.AddIssueAsync(repo);
        await AddToSprintAsync(t, sprint, issues[0], issues[1], issues[2], issues[3]);
        await AddToSprintAsync(t, other, issues[3]);
        await SetSizeAsync(t, issues[0], "M");
        await SetSizeAsync(t, issues[1], "L");
        return (sprint, issues);
    }

    [DbFact]
    public async Task Repeated_runs_on_one_day_leave_one_row_with_the_latest_values()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var (sprint, issues) = await SeedAsync(t);
        var day = Recorder.Today;

        await Recorder.RecordAsync(day, default, t.Id);
        await Recorder.RecordAsync(day, default, t.Id);
        Assert.Equal([new Daily(3, 6, 0, 0)], await RowsAsync(t, sprint, day));

        await SyncStateAsync(t, issues[0], "closed", "completed");
        await Recorder.RecordAsync(day, default, t.Id);
        Assert.Equal([new Daily(3, 6, 1, 2)], await RowsAsync(t, sprint, day));
        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from sprint_daily where tenant_id = @tid and sprint_id = @sprint",
            new { tid = t.Id, sprint }));
    }

    [DbFact]
    public async Task Recorded_numbers_match_a_reconstruction_from_issue_events()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var (sprint, issues) = await SeedAsync(t);

        await SyncStateAsync(t, issues[0], "closed", "completed");
        await SyncStateAsync(t, issues[1], "closed", "not_planned");
        await SyncStateAsync(t, issues[2], "closed", "completed");
        await SyncStateAsync(t, issues[2], "open", "reopened");
        await Recorder.RecordAsync(Recorder.Today, default, t.Id);

        var expected = await ReconstructAsync(t, sprint);
        Assert.Equal(new Daily(2, 3, 1, 2), expected);
        Assert.Equal([expected], await RowsAsync(t, sprint, Recorder.Today));
    }

    [DbFact]
    public async Task Cancelled_issues_are_in_neither_scope_nor_done()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var (sprint, issues) = await SeedAsync(t);
        await SyncStateAsync(t, issues[1], "closed", "not_planned");

        await Recorder.RecordAsync(Recorder.Today, default, t.Id);

        Assert.Equal([new Daily(2, 3, 0, 0)], await RowsAsync(t, sprint, Recorder.Today));
    }

    [DbFact]
    public async Task Earlier_days_are_not_rewritten_and_inactive_sprints_get_no_row()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var (sprint, _) = await SeedAsync(t);
        var past = await t.AddSprintAsync("Old", "active");
        await t.ExecAsync("update sprints set start_date = current_date - 30, end_date = current_date - 20 where id = @past", new { past });
        var closed = await t.AddSprintAsync("Closed", "closed");
        var yesterday = Recorder.Today.AddDays(-1);
        await t.ExecAsync("insert into sprint_daily (tenant_id, sprint_id, day, scope_count, scope_weight, done_count, done_weight) values (@tid, @sprint, @day, 9, 9, 1, 1)",
            new { tid = t.Id, sprint, day = yesterday.ToDateTime(TimeOnly.MinValue) });

        await Recorder.RecordAsync(Recorder.Today, default, t.Id);

        Assert.Equal([new Daily(9, 9, 1, 1)], await RowsAsync(t, sprint, yesterday));
        Assert.Empty(await RowsAsync(t, past, Recorder.Today));
        Assert.Empty(await RowsAsync(t, closed, Recorder.Today));
    }

    [DbFact]
    public async Task Tenants_are_counted_separately_and_hidden_from_each_other()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var other = await TestTenant.CreateAsync(factory);
        var (sprint, _) = await SeedAsync(t);
        var (otherSprint, _) = await SeedAsync(other);
        await using var disabled = await TestTenant.CreateAsync(factory, sprints: false);
        var offSprint = await disabled.AddSprintAsync("Off");

        foreach (var id in new[] { t.Id, other.Id, disabled.Id })
            await Recorder.RecordAsync(Recorder.Today, default, id);

        Assert.Equal([new Daily(3, 6, 0, 0)], await RowsAsync(t, sprint, Recorder.Today));
        Assert.Equal([new Daily(3, 6, 0, 0)], await RowsAsync(other, otherSprint, Recorder.Today));
        Assert.Empty(await RowsAsync(disabled, offSprint, Recorder.Today));

        var app = factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.App);
        await using var conn = await app.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("select set_config('app.tenant_id', @tid, true)", new { tid = t.Id.ToString() }, tx);
        var visible = (await conn.QueryAsync<Guid>("select sprint_id from sprint_daily where day = @day",
            new { day = Recorder.Today.ToDateTime(TimeOnly.MinValue) }, tx)).ToList();
        Assert.Contains(sprint, visible);
        Assert.DoesNotContain(otherSprint, visible);
        Assert.DoesNotContain(offSprint, visible);
    }

    [DbFact]
    public async Task Sync_records_closed_and_reopened_transitions_without_a_member()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await t.AddRepoAsync("api");
        var issue = await t.AddIssueAsync(repo);

        await SyncStateAsync(t, issue, "open", null);
        await SyncStateAsync(t, issue, "closed", "completed");
        await SyncStateAsync(t, issue, "closed", "completed");
        await SyncStateAsync(t, issue, "open", "reopened");

        var events = await t.QueryAsync(
            "select kind, from_value, to_value, member_id from issue_events where issue_id = @issue order by at, kind desc", new { issue });
        Assert.Equal(["closed:>completed", "reopened:completed>reopened"], events.Select(e => $"{e.kind}:{e.from_value}>{e.to_value}"));
        Assert.All(events, e => Assert.Null((Guid?)e.member_id));
    }

    [DbFact]
    public async Task Carry_over_undo_ignores_sync_recorded_state_events()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await t.AddRepoAsync("api");
        var from = await t.AddSprintAsync("From");
        var to = await t.AddSprintAsync("To");
        var issue = await t.AddIssueAsync(repo);
        await AddToSprintAsync(t, from, issue);

        using var client = factory.CreateClient().WithDevUser(t.ViewerLogin);
        var carry = await client.PostAsJsonAsync($"/api/sprints/{from}/carry-over", new { toSprintId = to });
        Assert.Equal(HttpStatusCode.OK, carry.StatusCode);
        var carryOverId = (await carry.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("carryOverId").GetGuid();

        await SyncStateAsync(t, issue, "closed", "completed");
        await SyncStateAsync(t, issue, "open", "reopened");

        var undo = await client.PostAsync($"/api/sprints/{from}/carry-over/{carryOverId}/undo", null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
    }

    [DbFact]
    public async Task A_close_reason_change_between_syncs_is_recorded_and_matches_the_snapshot()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var (sprint, issues) = await SeedAsync(t);

        await SyncStateAsync(t, issues[0], "closed", "completed");
        await SyncStateAsync(t, issues[0], "closed", "not_planned");
        await Recorder.RecordAsync(Recorder.Today, default, t.Id);

        var events = await t.QueryAsync(
            "select kind, from_value, to_value from issue_events where issue_id = @issue and kind = 'closed' order by at", new { issue = issues[0] });
        Assert.Equal(["closed:>completed", "closed:completed>not_planned"], events.Select(e => $"{e.kind}:{e.from_value}>{e.to_value}"));
        var expected = await ReconstructAsync(t, sprint);
        Assert.Equal(new Daily(2, 4, 0, 0), expected);
        Assert.Equal([expected], await RowsAsync(t, sprint, Recorder.Today));
    }
}
