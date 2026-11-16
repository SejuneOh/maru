using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class BoardFieldsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeWriter : IGitHubWriter
    {
        public List<string> Calls { get; } = [];
        public Exception? Failure { get; set; }
        public long GithubId { get; set; }
        public int Number { get; set; }

        private SourceIssue Issue(string state, string? reason) =>
            new(GithubId, "n", Number, "t", null, null, state, reason, null, DateTimeOffset.UtcNow.AddDays(-1),
                state == "closed" ? DateTimeOffset.UtcNow : null, DateTimeOffset.UtcNow, "https://x/1", null, [], [], null, null);

        public Task<SourceIssue> CloseIssueAsync(GitHubRepoRef repo, int number, IssueCloseReason reason, CancellationToken ct)
        {
            Calls.Add("close:" + reason);
            return Failure is null
                ? Task.FromResult(Issue("closed", reason == IssueCloseReason.NotPlanned ? "not_planned" : "completed"))
                : Task.FromException<SourceIssue>(Failure);
        }

        public Task<SourceIssue> ReopenIssueAsync(GitHubRepoRef repo, int number, CancellationToken ct)
        {
            Calls.Add("reopen");
            return Failure is null ? Task.FromResult(Issue("open", "reopened")) : Task.FromException<SourceIssue>(Failure);
        }

        public Task<SourceIssue> CreateIssueAsync(GitHubRepoRef repo, string actorLogin, string title, string? body,
            IReadOnlyList<string> labels, IReadOnlyList<string> assignees, CancellationToken ct) => throw new NotSupportedException();

        public Task<SourceIssue> SetAssigneesAsync(GitHubRepoRef repo, int number, IReadOnlyList<string> assignees, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SourceComment> AddCommentAsync(GitHubRepoRef repo, string actorLogin, int number, string body, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed record Card(TestTenant Tenant, Guid Issue, FakeWriter Writer);

    private async Task<Card> CardAsync(TestTenant t, bool closed = false, string? reason = null, bool archived = false)
    {
        await t.ExecAsync("update github_connections set installation_id = @installation where id = @id",
            new { id = t.ConnectionId, installation = TestTenant.NextId() });
        var repo = await t.AddRepoAsync("r" + Guid.NewGuid().ToString("N")[..6]);
        if (archived)
            await t.ExecAsync("update repositories set archived = true where id = @repo", new { repo });
        var issue = await t.AddIssueAsync(repo, closed: closed);
        if (reason is not null)
            await t.ExecAsync("update issues set state_reason = @reason where id = @issue", new { reason, issue });
        var githubId = await t.ScalarAsync<long>("select github_issue_id from issues where id = @issue", new { issue });
        var number = await t.ScalarAsync<int>("select number from issues where id = @issue", new { issue });
        return new Card(t, issue, new FakeWriter { GithubId = githubId, Number = number });
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PatchAsync(
        Card c, object? body, string? key = null, Guid? issue = null, string? raw = null)
    {
        using var client = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IGitHubWriter>();
            s.AddSingleton<IGitHubWriter>(c.Writer);
        })).CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/issues/{issue ?? c.Issue}/board");
        request.Headers.Add("X-Dev-User", c.Tenant.ViewerLogin);
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        request.Content = new StringContent(raw ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text));
    }

    private static async Task<List<string>> EventsAsync(TestTenant t, Guid issue) =>
        (await t.QueryAsync("select kind, from_value, to_value from issue_events where issue_id = @issue", new { issue }))
            .Select(r => $"{r.kind}:{r.from_value}>{r.to_value}").Order().ToList();

    private static string Err(JsonElement e) => e.GetProperty("error").GetString()!;

    [DbFact]
    public async Task Open_statuses_set_and_clear_the_override_with_events_and_audit()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);

        var (status, body) = await PatchAsync(c, new { status = "in_progress" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("in_progress", body.GetProperty("status").GetString());
        await PatchAsync(c, new { status = "in_progress" });
        await PatchAsync(c, new { status = "in_review" });
        var (_, cleared) = await PatchAsync(c, new { status = (string?)null });
        Assert.Equal("todo", cleared.GetProperty("status").GetString());
        await PatchAsync(c, new { status = (string?)null });

        Assert.Equal(["status_cleared:in_review>", "status_set:>in_progress", "status_set:in_progress>in_review"], await EventsAsync(t, c.Issue));
        Assert.Equal(3, await t.ScalarAsync<int>("select count(*)::int from audit_log where target_id = @i and action = 'issue.board_update'", new { i = c.Issue }));
        Assert.Empty(c.Writer.Calls);
    }

    [DbFact]
    public async Task Done_and_cancelled_close_on_github_once_and_double_close_is_a_noop()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var done = await CardAsync(t);
        var (status, body) = await PatchAsync(done, new { status = "done" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("done", "closed"), (body.GetProperty("status").GetString(), body.GetProperty("state").GetString()));
        var (again, _) = await PatchAsync(done, new { status = "done" });
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Equal(["close:Completed"], done.Writer.Calls);
        Assert.Equal(["closed:>completed"], await EventsAsync(t, done.Issue));

        var cancelled = await CardAsync(t);
        var (_, cb) = await PatchAsync(cancelled, new { status = "cancelled" });
        Assert.Equal("cancelled", cb.GetProperty("status").GetString());
        Assert.Equal(["close:NotPlanned"], cancelled.Writer.Calls);
    }

    [DbFact]
    public async Task Moving_a_closed_issue_to_an_open_status_reopens_it_and_sets_the_override()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t, closed: true, reason: "completed");

        var (status, body) = await PatchAsync(c, new { status = "in_progress" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("in_progress", "open"), (body.GetProperty("status").GetString(), body.GetProperty("state").GetString()));
        Assert.Equal(["reopen"], c.Writer.Calls);
        Assert.Equal(["reopened:completed>reopened", "status_set:>in_progress"], await EventsAsync(t, c.Issue));

        await PatchAsync(c, new { status = "in_review" });
        Assert.Equal(["reopen"], c.Writer.Calls);
    }

    [DbFact]
    public async Task Github_failure_leaves_the_database_unchanged()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        c.Writer.Failure = new GitHubWriteException(GitHubWriteErrorKind.Unavailable, "502", sideEffectPossible: true);

        var (status, body) = await PatchAsync(c, new { status = "done", priority = "high" });
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal("github_unavailable", Err(body));
        Assert.Equal("open", await t.ScalarAsync<string>("select state from issues where id = @i", new { i = c.Issue }));
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from board_fields where issue_id = @i", new { i = c.Issue }));
        Assert.Empty(await EventsAsync(t, c.Issue));
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from audit_log where target_id = @i", new { i = c.Issue }));
    }

    [DbFact]
    public async Task Priority_and_size_follow_tenant_settings_and_validate_values()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);

        var (status, body) = await PatchAsync(c, new { priority = "urgent" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("urgent", body.GetProperty("priority").GetString());
        var size = await PatchAsync(c, new { size = "M" });
        Assert.Equal(HttpStatusCode.Conflict, size.Status);
        Assert.Equal("field_disabled", Err(size.Body));

        await t.ExecAsync("update tenant_settings set size_enabled = true where tenant_id = @tid", new { tid = t.Id });
        Assert.Equal("M", (await PatchAsync(c, new { size = "M" })).Body.GetProperty("size").GetString());
        var (_, clear) = await PatchAsync(c, new { priority = (string?)null });
        Assert.Equal(JsonValueKind.Null, clear.GetProperty("priority").ValueKind);
        Assert.Equal(["priority_set:>urgent", "priority_set:urgent>", "size_set:>M"], await EventsAsync(t, c.Issue));

        await t.ExecAsync("update tenant_settings set priority_enabled = false where tenant_id = @tid", new { tid = t.Id });
        Assert.Equal("field_disabled", Err((await PatchAsync(c, new { priority = (string?)null })).Body));

        foreach (var bad in new object[] { new { priority = "huge" }, new { size = "XL" }, new { status = "blocked" }, new { }, new { size = 3 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(c, bad)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(c, null, raw: "[1]")).Status);
    }

    [DbFact]
    public async Task Expected_updated_at_must_match_the_board_row()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        await PatchAsync(c, new { priority = "low" });
        var at = DateTime.SpecifyKind(
            await t.ScalarAsync<DateTime>("select updated_at from board_fields where issue_id = @i", new { i = c.Issue }), DateTimeKind.Utc);

        var stale = await PatchAsync(c, new { priority = "high", expectedUpdatedAt = at.AddSeconds(-5).ToString("O") });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("conflict", Err(stale.Body));
        var ok = await PatchAsync(c, new { priority = "high", expectedUpdatedAt = at.ToString("O") });
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("high", await t.ScalarAsync<string>("select priority from board_fields where issue_id = @i", new { i = c.Issue }));
    }

    [DbFact]
    public async Task Other_tenants_issue_is_404_and_archived_repository_is_refused_before_github()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var other = await TestTenant.CreateAsync(factory);
        var mine = await CardAsync(t, archived: true);
        var theirs = await CardAsync(other);

        var cross = await PatchAsync(mine, new { status = "done" }, issue: theirs.Issue);
        Assert.Equal(HttpStatusCode.NotFound, cross.Status);
        Assert.Equal("open", await other.ScalarAsync<string>("select state from issues where id = @i", new { i = theirs.Issue }));

        var archived = await PatchAsync(mine, new { status = "done" });
        Assert.Equal(HttpStatusCode.Conflict, archived.Status);
        Assert.Equal("repository_archived", Err(archived.Body));
        Assert.Empty(mine.Writer.Calls);
    }

    [DbFact]
    public async Task Same_idempotency_key_replays_without_a_second_github_call()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);

        var first = await PatchAsync(c, new { status = "done" }, key: "k1");
        var replay = await PatchAsync(c, new { status = "done" }, key: "k1");
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.Equal(first.Body.GetProperty("id").GetString(), replay.Body.GetProperty("id").GetString());
        Assert.Equal("done", replay.Body.GetProperty("status").GetString());
        Assert.Equal(["close:Completed"], c.Writer.Calls);
        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from issue_events where issue_id = @i and kind = 'closed'", new { i = c.Issue }));

        var reused = await PatchAsync(c, new { status = "cancelled" }, key: "k1");
        Assert.Equal("idempotency_key_reused", Err(reused.Body));
    }
}
