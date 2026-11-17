using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using WorkBoard.Api.Features.MeetingNotes;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class ActionItemIssueTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeWriter : IGitHubWriter
    {
        public int Calls;
        public int FindCalls;
        public Exception? Fail;
        public Exception? FindFail;
        public SourceIssue? Existing;
        public TimeSpan Delay;
        public string? Title;
        public string? Body;
        public string? Marker;
        public List<string> Assignees = [];
        public List<string> FindRepos = [];
        public DateTimeOffset? FindSince;
        public bool DropAssignees;
        private int _number = 200;

        public async Task<SourceIssue> CreateIssueAsync(GitHubRepoRef repo, string actorLogin, string title, string? body,
            IReadOnlyList<string> labels, IReadOnlyList<string> assignees, CancellationToken ct, string? marker = null)
        {
            Interlocked.Increment(ref Calls);
            (Title, Body, Marker, Assignees) = (title, body, marker, [.. assignees]);
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);
            if (Fail is not null)
                throw Fail;
            var n = Interlocked.Increment(ref _number);
            var users = DropAssignees ? [] : assignees.Select(a => new SourceUser(TestTenant.NextId(), a, null, null)).ToList();
            return Issue(n, title, users);
        }

        public Task<SourceIssue?> FindCreatedIssueAsync(GitHubRepoRef repo, string marker, DateTimeOffset since, CancellationToken ct)
        {
            Interlocked.Increment(ref FindCalls);
            lock (FindRepos)
                FindRepos.Add(repo.Name);
            FindSince = since;
            Marker = marker;
            return FindFail is not null ? throw FindFail : Task.FromResult(Existing);
        }

        public Task<SourceIssue> SetAssigneesAsync(GitHubRepoRef repo, int number, IReadOnlyList<string> assignees, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceComment> AddCommentAsync(GitHubRepoRef repo, string actorLogin, int number, string body, CancellationToken ct, string? marker = null) => throw new NotSupportedException();
        public Task<SourceComment?> FindCreatedCommentAsync(GitHubRepoRef repo, int number, string marker, DateTimeOffset since, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceIssue> CloseIssueAsync(GitHubRepoRef repo, int number, IssueCloseReason reason, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceIssue> ReopenIssueAsync(GitHubRepoRef repo, int number, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ThrowingMirror(NpgsqlDataSource sync) : MirrorWriter(sync)
    {
        public override Task<MirroredIssue?> ApplyWrittenIssueAsync(
            Guid tenantId, Guid repositoryId, SourceIssue issue, Guid? memberId, IssueStateChange? change, CancellationToken ct) =>
            throw new InvalidOperationException("mirror down");
    }

    private static SourceIssue Issue(int n, string title, List<SourceUser>? users = null) =>
        new(TestTenant.NextId(), "node" + n, n, title, "b", null, "open", null,
            new SourceUser(9, "app[bot]", null, null), DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow,
            $"https://x/api/{n}", null, [], users ?? [], null, null);

    private HttpClient Client(FakeWriter writer, string? baseUrl = "https://board.example.test/", Action<IServiceCollection>? configure = null) =>
        factory.WithWebHostBuilder(b =>
        {
            if (baseUrl is not null)
                b.UseSetting("Web:BaseUrl", baseUrl);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IGitHubWriter>();
                s.AddSingleton<IGitHubWriter>(writer);
                configure?.Invoke(s);
            });
        }).CreateClient();

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient client, TestTenant t, Guid item, object? body, string? key, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/meeting-action-items/{item}/issue");
        request.Headers.Add("X-Dev-User", t.ViewerLogin);
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text));
    }

    private static async Task<Guid> ConnectedRepoAsync(TestTenant t, string name = "api", bool archived = false)
    {
        await t.ExecAsync("update github_connections set installation_id = @i where id = @id and installation_id is null",
            new { i = TestTenant.NextId(), id = t.ConnectionId });
        var repo = await t.AddRepoAsync(name);
        await t.ExecAsync("update repositories set full_name = @fullName, archived = @archived where id = @repo",
            new { fullName = $"org-{t.Suffix}/{name}", archived, repo });
        return repo;
    }

    private static async Task<(Guid Note, Guid Item)> ItemAsync(TestTenant t, string text = "알림 설계")
    {
        var note = await t.AddNoteAsync("주간 [회의]");
        return (note, await t.AddActionItemAsync(note, text: text));
    }

    private static Task<Guid?> LinkedIssueAsync(TestTenant t, Guid item) =>
        t.ScalarAsync<Guid?>("select issue_id from meeting_action_items where id = @item", new { item });

    private static string Error(JsonElement body) => body.GetProperty("error").GetString()!;

    // An earlier request's claim on the item that never finished, old enough to be taken over.
    private static Task SeedStaleClaimAsync(TestTenant t, Guid item, Guid repo, string key = "old", int ageMinutes = 10) =>
        t.ExecAsync("""
            update meeting_action_items
               set issue_claim_key = @key, issue_claim_at = now() - make_interval(mins => @age),
                   issue_claim_first_at = now() - make_interval(mins => @age), issue_claim_repository_id = @repo
             where id = @item
            """, new { item, repo, key, age = ageMinutes });

    [DbFact]
    public async Task Creates_the_issue_with_note_link_due_date_and_owner_and_links_the_item()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (owner, _) = await t.AddMemberAsync("owner");
        var (note, item) = await ItemAsync(t);
        await t.ExecAsync("""
            update meeting_action_items set due_date = '2026-11-20',
                   assignee_member_id = (select id from members where tenant_id = @tid and github_login = @owner)
             where id = @item
            """, new { tid = t.Id, owner, item });
        var writer = new FakeWriter();
        using var client = Client(writer);

        var (status, body) = await PostAsync(client, t, item, new { repositoryId = repo }, "k1");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.True(body.GetProperty("created").GetBoolean());
        var linked = body.GetProperty("actionItem");
        Assert.Equal(item, linked.GetProperty("id").GetGuid());
        Assert.Equal(201, linked.GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal("open", linked.GetProperty("issue").GetProperty("state").GetString());
        Assert.Equal("api", linked.GetProperty("issue").GetProperty("repositoryName").GetString());
        Assert.Equal("알림 설계", writer.Title);
        Assert.Contains($"[주간 \\[회의\\]](https://board.example.test/notes/view?id={note})", writer.Body);
        Assert.Contains("마감: 2026-11-20", writer.Body);
        Assert.Equal([owner], writer.Assignees);
        Assert.Equal("<!-- work-board:create:" + ActionItemIssues.MarkerId(t.Id, item) + " -->", writer.Marker);
        Assert.NotNull(await LinkedIssueAsync(t, item));
        Assert.Equal(1, await t.ScalarAsync<int>(
            "select count(*)::int from audit_log where tenant_id = @tid and action = 'meeting_action_item.issue' and target_id = @item",
            new { tid = t.Id, item }));
        Assert.Equal(0, await t.ScalarAsync<int>(
            "select count(*)::int from meeting_action_items where id = @item and issue_claim_key is not null", new { item }));

        var read = await t.GetAsync($"/api/meeting-notes/{note}");
        Assert.Equal(201, read.GetProperty("actionItems")[0].GetProperty("issue").GetProperty("number").GetInt32());
        Assert.False(read.GetProperty("actionItems")[0].GetProperty("creatingIssue").GetBoolean());
    }

    [DbFact]
    public async Task Without_a_configured_base_url_the_link_uses_the_origin_and_without_either_there_is_none()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (note, item) = await ItemAsync(t);
        var (_, second) = await ItemAsync(t);
        var writer = new FakeWriter();
        using var client = Client(writer, baseUrl: null);

        await PostAsync(client, t, item, new { repositoryId = repo }, "a", origin: "http://localhost:3196");
        Assert.Contains($"(http://localhost:3196/notes/view?id={note})", writer.Body);

        await PostAsync(client, t, second, new { repositoryId = repo }, "b");
        Assert.DoesNotContain("](", writer.Body);
        Assert.StartsWith("회의록: 주간 \\[회의\\] (", writer.Body);
    }

    [DbFact]
    public async Task Long_text_is_cut_for_the_title_and_kept_whole_in_the_body()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var text = new string('가', 400);
        var (_, item) = await ItemAsync(t, text);
        var writer = new FakeWriter();
        using var client = Client(writer);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, t, item, new { repositoryId = repo }, "k")).Status);
        Assert.Equal(256, writer.Title!.Length);
        Assert.EndsWith("…", writer.Title);
        Assert.StartsWith(text + "\n\n회의록:", writer.Body);
    }

    [DbFact]
    public async Task A_second_click_with_a_new_key_returns_the_existing_issue()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (_, item) = await ItemAsync(t);
        var writer = new FakeWriter();
        using var client = Client(writer);

        var first = await PostAsync(client, t, item, new { repositoryId = repo }, "k1");
        var second = await PostAsync(client, t, item, new { repositoryId = repo }, "k2");
        var replay = await PostAsync(client, t, item, new { repositoryId = repo }, "k1");

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.False(second.Body.GetProperty("created").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        var issue = first.Body.GetProperty("actionItem").GetProperty("issue").GetProperty("id").GetGuid();
        Assert.Equal(issue, second.Body.GetProperty("actionItem").GetProperty("issue").GetProperty("id").GetGuid());
        Assert.Equal(1, writer.Calls);
        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from issues where tenant_id = @tid", new { tid = t.Id }));
    }

    [DbFact]
    public async Task Parallel_clicks_with_different_keys_call_github_once()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (_, item) = await ItemAsync(t);
        var writer = new FakeWriter { Delay = TimeSpan.FromMilliseconds(500) };
        using var client = Client(writer);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            PostAsync(client, t, item, new { repositoryId = repo }, $"p{i}")));

        Assert.Equal(1, writer.Calls);
        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        Assert.All(results, r => Assert.True(r.Status == HttpStatusCode.Created || r.Status == HttpStatusCode.OK
            || (r.Status == HttpStatusCode.Conflict && Error(r.Body) == "request_in_progress")));
        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from issues where tenant_id = @tid", new { tid = t.Id }));
    }

    [DbFact]
    public async Task A_stale_claim_is_taken_over_and_the_earlier_issue_is_found_instead_of_created()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var other = await ConnectedRepoAsync(t, "web");
        var (_, item) = await ItemAsync(t);
        await SeedStaleClaimAsync(t, item, repo);
        var writer = new FakeWriter { Existing = Issue(77, "알림 설계") };
        using var client = Client(writer);

        var (status, body) = await PostAsync(client, t, item, new { repositoryId = other }, "new-key");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(77, body.GetProperty("actionItem").GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal("api", body.GetProperty("actionItem").GetProperty("issue").GetProperty("repositoryName").GetString());
        Assert.Equal(0, writer.Calls);
        Assert.Equal(["api"], writer.FindRepos);
        Assert.Equal("<!-- work-board:create:" + ActionItemIssues.MarkerId(t.Id, item) + " -->", writer.Marker);
        Assert.True(writer.FindSince < DateTimeOffset.UtcNow.AddMinutes(-14));
    }

    [DbFact]
    public async Task A_takeover_that_finds_nothing_looks_in_both_repositories_then_creates()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var other = await ConnectedRepoAsync(t, "web");
        var (_, item) = await ItemAsync(t);
        await SeedStaleClaimAsync(t, item, repo);
        var writer = new FakeWriter();
        using var client = Client(writer);

        var (status, _) = await PostAsync(client, t, item, new { repositoryId = other }, "new-key");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(["api", "web"], writer.FindRepos);
        Assert.Equal(1, writer.Calls);
    }

    [DbFact]
    public async Task A_takeover_whose_lookup_fails_creates_nothing_and_keeps_the_claim()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (_, item) = await ItemAsync(t);
        await SeedStaleClaimAsync(t, item, repo);
        var writer = new FakeWriter { FindFail = new GitHubWriteException(GitHubWriteErrorKind.Unavailable, "down") };
        using var client = Client(writer);

        var (status, body) = await PostAsync(client, t, item, new { repositoryId = repo }, "k");

        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        Assert.Equal("github_unavailable", Error(body));
        Assert.Equal(0, writer.Calls);
        Assert.Equal("k", await t.ScalarAsync<string>("select issue_claim_key from meeting_action_items where id = @item", new { item }));
        var other = await PostAsync(client, t, item, new { repositoryId = repo }, "k2");
        Assert.Equal(HttpStatusCode.Conflict, other.Status);
        Assert.Equal("request_in_progress", Error(other.Body));
    }

    [DbFact]
    public async Task A_refusal_frees_the_item_and_an_unknown_outcome_holds_it()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (_, item) = await ItemAsync(t);
        var writer = new FakeWriter { Fail = new GitHubWriteException(GitHubWriteErrorKind.NotGranted, "no") };
        using var client = Client(writer);

        var refused = await PostAsync(client, t, item, new { repositoryId = repo }, "a");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("github_write_not_granted", Error(refused.Body));
        Assert.Null(await t.ScalarAsync<string?>("select issue_claim_key from meeting_action_items where id = @item", new { item }));

        writer.Fail = new GitHubWriteException(GitHubWriteErrorKind.Unavailable, "5xx", sideEffectPossible: true);
        var unknown = await PostAsync(client, t, item, new { repositoryId = repo }, "b");
        Assert.Equal(HttpStatusCode.BadGateway, unknown.Status);
        var blocked = await PostAsync(client, t, item, new { repositoryId = repo }, "c");
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Equal("request_in_progress", Error(blocked.Body));
        Assert.Equal(2, writer.Calls);

        // Once the claim is stale, the next request looks for the issue before creating.
        await t.ExecAsync("update meeting_action_items set issue_claim_at = now() - interval '10 minutes' where id = @item", new { item });
        writer.Fail = null;
        writer.Existing = Issue(88, "알림 설계");
        var found = await PostAsync(client, t, item, new { repositoryId = repo }, "d");
        Assert.Equal(HttpStatusCode.Created, found.Status);
        Assert.Equal(88, found.Body.GetProperty("actionItem").GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal(2, writer.Calls);
    }

    [DbFact]
    public async Task Mirror_failure_answers_202_and_a_later_request_links_the_issue_once_mirrored()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (note, item) = await ItemAsync(t);
        var writer = new FakeWriter();
        using (var failing = Client(writer, configure: s =>
        {
            s.RemoveAll<MirrorWriter>();
            s.AddSingleton<MirrorWriter>(sp => new ThrowingMirror(sp.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.Sync)));
        }))
        {
            var (status, body) = await PostAsync(failing, t, item, new { repositoryId = repo }, "k");
            Assert.Equal(HttpStatusCode.Accepted, status);
            Assert.Equal(201, body.GetProperty("number").GetInt32());
            Assert.Equal(item, body.GetProperty("actionItemId").GetGuid());
        }

        var read = await t.GetAsync($"/api/meeting-notes/{note}");
        var pending = read.GetProperty("actionItems")[0];
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("issue").ValueKind);
        Assert.Equal(201, pending.GetProperty("pendingIssue").GetProperty("number").GetInt32());

        using var client = Client(writer);
        var stillPending = await PostAsync(client, t, item, new { repositoryId = repo }, "k2");
        Assert.Equal(HttpStatusCode.Accepted, stillPending.Status);

        await t.ExecAsync("""
            insert into issues (tenant_id, repository_id, github_issue_id, number, title, state, github_created_at, github_updated_at)
            values (@tid, @repo, @gid, 201, 'x', 'open', now(), now())
            """, new { tid = t.Id, repo, gid = TestTenant.NextId() });
        var linked = await PostAsync(client, t, item, new { repositoryId = repo }, "k3");
        Assert.Equal(HttpStatusCode.OK, linked.Status);
        Assert.Equal(201, linked.Body.GetProperty("actionItem").GetProperty("issue").GetProperty("number").GetInt32());
        Assert.NotNull(await LinkedIssueAsync(t, item));
        Assert.Equal(1, writer.Calls);
    }

    [DbFact]
    public async Task Other_tenants_items_and_repositories_are_404_and_bad_requests_400()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var other = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var archived = await ConnectedRepoAsync(t, "old", archived: true);
        var foreignRepo = await ConnectedRepoAsync(other);
        var (_, item) = await ItemAsync(t);
        var (_, foreignItem) = await ItemAsync(other);
        var writer = new FakeWriter();
        using var client = Client(writer);

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, t, foreignItem, new { repositoryId = repo }, "a")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, t, item, new { repositoryId = foreignRepo }, "b")).Status);
        var a = await PostAsync(client, t, item, new { repositoryId = archived }, "c");
        Assert.Equal(HttpStatusCode.Conflict, a.Status);
        Assert.Equal("repository_archived", Error(a.Body));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, t, item, new { repositoryId = repo }, null)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, t, item, new { }, "d")).Status);
        Assert.Equal(0, writer.Calls);
        Assert.Null(await other.ScalarAsync<Guid?>("select issue_id from meeting_action_items where id = @foreignItem", new { foreignItem }));
    }

    [DbFact]
    public async Task Dropped_owner_is_reported()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (_, ownerId) = await t.AddMemberAsync("owner");
        var (_, item) = await ItemAsync(t);
        await t.ExecAsync("""
            update meeting_action_items set assignee_member_id = (select id from members where tenant_id = @tid and github_user_id = @ownerId)
             where id = @item
            """, new { tid = t.Id, ownerId, item });
        using var client = Client(new FakeWriter { DropAssignees = true });

        var (status, body) = await PostAsync(client, t, item, new { repositoryId = repo }, "k");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal([ownerId], body.GetProperty("droppedAssignees").EnumerateArray().Select(e => e.GetInt64()));
    }

    [DbFact]
    public async Task An_item_being_turned_into_an_issue_cannot_be_removed_from_its_note_until_the_claim_is_stale()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (note, item) = await ItemAsync(t);
        await SeedStaleClaimAsync(t, item, repo, ageMinutes: 0);
        var read = await t.GetAsync($"/api/meeting-notes/{note}");
        Assert.True(read.GetProperty("actionItems")[0].GetProperty("creatingIssue").GetBoolean());
        using var client = Client(new FakeWriter());

        async Task<(HttpStatusCode, JsonElement)> RemoveAllAsync(string expected)
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/meeting-notes/{note}");
            request.Headers.Add("X-Dev-User", t.ViewerLogin);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                title = "주간", heldAt = "2026-11-12T10:00:00+09:00", bodyMd = "", decisions = Array.Empty<string>(),
                attendeeIds = Array.Empty<Guid>(), actionItems = Array.Empty<object>(), expectedUpdatedAt = expected,
            }), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            return (response.StatusCode, JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()));
        }

        var (status, body) = await RemoveAllAsync(read.GetProperty("updatedAt").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("action_item_linked", Error(body));

        await t.ExecAsync("update meeting_action_items set issue_claim_at = now() - interval '10 minutes' where id = @item", new { item });
        Assert.False((await t.GetAsync($"/api/meeting-notes/{note}")).GetProperty("actionItems")[0].GetProperty("creatingIssue").GetBoolean());
        var (removed, _) = await RemoveAllAsync(read.GetProperty("updatedAt").GetString()!);
        Assert.Equal(HttpStatusCode.OK, removed);
    }
}
