using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

// A repository with sync_enabled = false is hidden from the board, but sprint bookkeeping still counts its issues.
public class HiddenRepositoryTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private MirrorWriter Writer() => new(factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.Sync));

    private static async Task HideAsync(TestTenant t, Guid repo) =>
        await t.ExecAsync("update repositories set sync_enabled = false where id = @repo", new { repo });

    [DbFact]
    public async Task Overview_still_loads_and_leaves_out_everything_of_a_deselected_repository()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var shown = await t.AddRepoAsync("shown");
        var hidden = await t.AddRepoAsync("hidden");
        var visibleIssue = await t.AddIssueAsync(shown, title: "visible");
        var hiddenUnassigned = await t.AddIssueAsync(hidden, title: "hidden-unassigned");
        var hiddenStale = await t.AddIssueAsync(hidden, idle: TimeSpan.FromDays(60), title: "hidden-stale");
        var hiddenReview = await t.AddIssueAsync(hidden, title: "hidden-review");
        await t.AddPullRequestAsync(hidden, hiddenReview, review: true);
        var hiddenClosed = await t.AddIssueAsync(hidden, closed: true);
        var note = await t.AddNoteAsync("standup");
        var item = await t.AddActionItemAsync(note, hiddenUnassigned);
        await t.ExecAsync("insert into comments (tenant_id, issue_id, github_comment_id, body, github_created_at, github_updated_at) " +
                          "values (@tid, @issue, @gid, 'hidden-comment', now(), now())",
            new { tid = t.Id, issue = hiddenUnassigned, gid = TestTenant.NextId() });
        await HideAsync(t, hidden);

        var overview = await t.OverviewAsync();

        var text = overview.GetRawText();
        Assert.Contains(visibleIssue.ToString(), text);
        foreach (var id in new[] { hiddenUnassigned, hiddenStale, hiddenReview, hiddenClosed })
            Assert.DoesNotContain(id.ToString(), text);
        Assert.DoesNotContain("hidden-comment", text);
        Assert.DoesNotContain(item.ToString(), text);
        Assert.Equal(1, overview.GetProperty("counts").GetProperty("open").GetInt32());
        Assert.Equal(1, overview.GetProperty("attention").GetProperty("totals").GetProperty("unassigned").GetInt32());

        var search = await t.GetAsync("/api/search?q=hidden-comment");
        Assert.DoesNotContain(hiddenUnassigned.ToString(), search.GetRawText());
    }

    [DbFact]
    public async Task Meeting_note_action_items_of_a_hidden_issue_still_load()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var hidden = await t.AddRepoAsync("hidden");
        var issue = await t.AddIssueAsync(hidden);
        var note = await t.AddNoteAsync("standup");
        await t.AddActionItemAsync(note, issue);
        await HideAsync(t, hidden);

        var body = await t.GetAsync($"/api/meeting-notes/{note}");

        Assert.Contains("hidden", body.GetRawText());
    }

    [DbFact]
    public async Task Carry_over_undo_and_daily_scope_still_count_issues_of_a_deselected_repository()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var hidden = await t.AddRepoAsync("hidden");
        var issue = await t.AddIssueAsync(hidden);
        var s1 = await t.AddSprintAsync("One");
        var s2 = await t.AddSprintAsync("Two", "planned");
        await t.BoardAsync(issue, sprint: s1);
        await HideAsync(t, hidden);

        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        await factory.Services.GetRequiredService<SprintDailyRecorder>().RecordAsync(day, default, t.Id);
        Assert.Equal(1, await t.ScalarAsync<int>(
            "select scope_count from sprint_daily where tenant_id = @tid and sprint_id = @s1", new { tid = t.Id, s1 }));

        using var client = factory.CreateClient().WithDevUser(t.ViewerLogin);
        var carry = await client.PostAsJsonAsync($"/api/sprints/{s1}/carry-over", new { toSprintId = s2 });
        Assert.Equal(HttpStatusCode.OK, carry.StatusCode);
        var body = await carry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("movedCount").GetInt32());
        Assert.Equal(s2, await t.ScalarAsync<Guid>("select sprint_id from board_fields where issue_id = @issue", new { issue }));

        var undo = await client.PostAsync($"/api/sprints/{s1}/carry-over/{body.GetProperty("carryOverId").GetGuid()}/undo", null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(s1, await t.ScalarAsync<Guid>("select sprint_id from board_fields where issue_id = @issue", new { issue }));
    }

    private static SourceRepository Source(long id, string name) => new(id, name, "acme/" + name, "https://x/" + name, false);

    private static async Task<bool> EnabledAsync(TestTenant t, long githubRepoId) =>
        await t.ScalarAsync<bool>("select sync_enabled from repositories where tenant_id = @tid and github_repo_id = @id",
            new { tid = t.Id, id = githubRepoId });

    private static async Task<Guid> AddConnectionAsync(TestTenant t, string kind)
    {
        var id = Guid.NewGuid();
        await t.ExecAsync("insert into github_connections (id, tenant_id, account_id, account_login, account_type, auth_kind) " +
                          "values (@id, @tid, @account, @login, 'organization', @kind)",
            new { id, tid = t.Id, account = TestTenant.NextId(), login = "o-" + id.ToString("N")[..8], kind });
        return id;
    }

    [DbFact]
    public async Task Moving_a_repository_from_a_token_to_an_app_connection_enables_it()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var token = await AddConnectionAsync(t, "token");
        var app = await AddConnectionAsync(t, "app");
        var repo = Source(TestTenant.NextId(), "api");

        await Writer().UpsertRepositoriesAsync(t.Id, token, [repo], default);
        Assert.False(await EnabledAsync(t, repo.Id));
        await Writer().UpsertRepositoriesAsync(t.Id, app, [repo], default);

        Assert.True(await EnabledAsync(t, repo.Id));
    }

    [DbFact]
    public async Task Moving_a_repository_from_an_app_to_a_token_connection_disables_it_until_imported()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var token = await AddConnectionAsync(t, "token");
        var app = await AddConnectionAsync(t, "app");
        var repo = Source(TestTenant.NextId(), "api");

        await Writer().UpsertRepositoriesAsync(t.Id, app, [repo], default);
        Assert.True(await EnabledAsync(t, repo.Id));
        await Writer().UpsertRepositoriesAsync(t.Id, token, [repo], default);
        Assert.False(await EnabledAsync(t, repo.Id));

        await t.ExecAsync("update repositories set sync_enabled = true where tenant_id = @tid and github_repo_id = @id", new { tid = t.Id, id = repo.Id });
        await Writer().UpsertRepositoriesAsync(t.Id, token, [repo], default);
        Assert.True(await EnabledAsync(t, repo.Id));
    }
}
