using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Infrastructure.Writes;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class CreateIssueTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeWriter : IGitHubWriter
    {
        public int Calls;
        public Exception? Fail;
        public bool DropAssignees;
        public string? Actor;
        public List<string> Assignees = [];
        public List<string> Labels = [];
        public TimeSpan Delay;
        public Dictionary<string, long> Ids = [];
        private int _number = 100;

        public async Task<SourceIssue> CreateIssueAsync(GitHubRepoRef repo, string actorLogin, string title, string? body,
            IReadOnlyList<string> labels, IReadOnlyList<string> assignees, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Actor = actorLogin;
            Assignees = [.. assignees];
            Labels = [.. labels];
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);
            if (Fail is not null)
                throw Fail;
            var n = Interlocked.Increment(ref _number);
            var users = DropAssignees ? [] : assignees.Select(a => new SourceUser(Ids.GetValueOrDefault(a, 1), a, null, null)).ToList();
            return new SourceIssue(TestTenant.NextId(), "node" + n, n, title, body, null, "open", null,
                new SourceUser(9, "app[bot]", null, null), DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow,
                $"https://x/{repo.Name}/{n}", null,
                labels.Select(l => new SourceLabel(null, null, l, "ededed", null)).ToList(), users, null, null);
        }

        public Task<SourceIssue> SetAssigneesAsync(GitHubRepoRef repo, int number, IReadOnlyList<string> assignees, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceComment> AddCommentAsync(GitHubRepoRef repo, string actorLogin, int number, string body, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceIssue> CloseIssueAsync(GitHubRepoRef repo, int number, IssueCloseReason reason, CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceIssue> ReopenIssueAsync(GitHubRepoRef repo, int number, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ThrowingMirror(NpgsqlDataSource sync) : MirrorWriter(sync)
    {
        public override Task<MirroredIssue?> ApplyWrittenIssueAsync(
            Guid tenantId, Guid repositoryId, SourceIssue issue, Guid? memberId, IssueStateChange? change, CancellationToken ct) =>
            throw new InvalidOperationException("mirror down");
    }

    private sealed class ThrowingAudit : IAuditLog
    {
        public Task WriteAsync(TenantSession session, string action, string targetType, Guid? targetId, object? before, object? after) =>
            throw new InvalidOperationException("audit down");
    }

    private HttpClient Client(FakeWriter writer, Action<IServiceCollection>? configure = null) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IGitHubWriter>();
            s.AddSingleton<IGitHubWriter>(writer);
            configure?.Invoke(s);
        })).CreateClient();

    private static async Task AddLabelAsync(TestTenant t, Guid repo, string name) =>
        await t.ExecAsync("insert into labels (tenant_id, repository_id, github_label_id, name) values (@tid, @repo, @gid, @name)",
            new { tid = t.Id, repo, gid = TestTenant.NextId(), name });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpClient client, TestTenant t, HttpMethod method, string url, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Dev-User", t.ViewerLogin);
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
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

    private static string Error(JsonElement body) => body.GetProperty("error").GetString()!;

    [DbFact]
    public async Task Created_issue_is_returned_mirrored_and_visible_to_its_tenant_only()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var other = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (login, userId) = await t.AddMemberAsync("alice");
        await AddLabelAsync(t, repo, "Bug");
        var writer = new FakeWriter { Ids = { [login] = userId } };
        using var client = Client(writer);

        var (status, body) = await SendAsync(client, t, HttpMethod.Post, "/api/issues",
            new { repositoryId = repo, title = "  New thing  ", body = "details", labels = new[] { "bug" }, assigneeGithubUserIds = new[] { userId } }, "k1");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("New thing", body.GetProperty("title").GetString());
        Assert.Equal("details", body.GetProperty("body").GetString());
        Assert.Equal("Bug", body.GetProperty("labels")[0].GetProperty("name").GetString());
        Assert.Equal(["Bug"], writer.Labels);
        Assert.Equal(userId, body.GetProperty("assignees")[0].GetProperty("githubUserId").GetInt64());
        Assert.Empty(body.GetProperty("droppedAssignees").EnumerateArray());
        Assert.Equal(t.ViewerLogin, writer.Actor);
        Assert.Equal([login], writer.Assignees);

        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from issues where id = @id", new { id }));
        Assert.Equal(0, await other.ScalarAsync<int>("select count(*)::int from issues where tenant_id = @tid and id = @id", new { tid = other.Id, id }));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, t, HttpMethod.Get, $"/api/issues/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, other, HttpMethod.Get, $"/api/issues/{id}")).Status);
        Assert.Equal(1, await t.ScalarAsync<int>(
            "select count(*)::int from audit_log where tenant_id = @tid and action = 'issue.create' and target_id = @id", new { tid = t.Id, id }));
    }

    [DbFact]
    public async Task Same_key_twice_creates_one_github_issue()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var writer = new FakeWriter();
        using var client = Client(writer);
        var payload = new { repositoryId = repo, title = "once" };

        var first = await SendAsync(client, t, HttpMethod.Post, "/api/issues", payload, "dup");
        var second = await SendAsync(client, t, HttpMethod.Post, "/api/issues", payload, "dup");
        var other = await SendAsync(client, t, HttpMethod.Post, "/api/issues", new { repositoryId = repo, title = "changed" }, "dup");

        Assert.Equal(HttpStatusCode.Created, second.Status);
        Assert.Equal(first.Body.GetProperty("id").GetGuid(), second.Body.GetProperty("id").GetGuid());
        Assert.Equal(1, writer.Calls);
        Assert.Equal("idempotency_key_reused", Error(other.Body));
        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from issues where tenant_id = @tid", new { tid = t.Id }));
    }

    [DbFact]
    public async Task Missing_key_and_invalid_bodies_are_400_without_calling_github()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var writer = new FakeWriter();
        using var client = Client(writer);

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, t, HttpMethod.Post, "/api/issues", new { repositoryId = repo, title = "x" })).Status);
        foreach (var bad in new object[]
        {
            new { repositoryId = repo, title = "   " },
            new { repositoryId = repo, title = new string('x', 257) },
            new { title = "x" },
            new { repositoryId = repo, title = "x", status = "cancelled" },
            new { repositoryId = repo, title = "x", priority = "huge" },
            new { repositoryId = repo, title = "x", size = "XL" },
            new { repositoryId = repo, title = "x", labels = new[] { " " } },
        })
        {
            var (status, body) = await SendAsync(client, t, HttpMethod.Post, "/api/issues", bad, Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("invalid_request", Error(body));
        }
        Assert.Equal(0, writer.Calls);
    }

    [DbFact]
    public async Task Archived_unknown_and_foreign_repositories_are_refused()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var other = await TestTenant.CreateAsync(factory);
        var archived = await ConnectedRepoAsync(t, "old", archived: true);
        var foreign = await ConnectedRepoAsync(other);
        var writer = new FakeWriter();
        using var client = Client(writer);

        var a = await SendAsync(client, t, HttpMethod.Post, "/api/issues", new { repositoryId = archived, title = "x" }, "a");
        Assert.Equal(HttpStatusCode.Conflict, a.Status);
        Assert.Equal("repository_archived", Error(a.Body));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, t, HttpMethod.Post, "/api/issues", new { repositoryId = foreign, title = "x" }, "b")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, t, HttpMethod.Post, "/api/issues", new { repositoryId = Guid.NewGuid(), title = "x" }, "c")).Status);
        Assert.Equal(0, writer.Calls);
    }

    [DbFact]
    public async Task Assignee_who_is_not_a_tenant_member_is_400()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var other = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (_, outsider) = await other.AddMemberAsync("bob");
        var writer = new FakeWriter();
        using var client = Client(writer);

        var (status, body) = await SendAsync(client, t, HttpMethod.Post, "/api/issues",
            new { repositoryId = repo, title = "x", assigneeGithubUserIds = new[] { outsider } }, "k");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_request", Error(body));
        Assert.Equal(0, writer.Calls);
    }

    [DbFact]
    public async Task Dropped_assignees_are_reported()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var (_, userId) = await t.AddMemberAsync("carol");
        using var client = Client(new FakeWriter { DropAssignees = true });

        var (status, body) = await SendAsync(client, t, HttpMethod.Post, "/api/issues",
            new { repositoryId = repo, title = "x", assigneeGithubUserIds = new[] { userId } }, "k");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal([userId], body.GetProperty("droppedAssignees").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Empty(body.GetProperty("assignees").EnumerateArray());
    }

    [DbTheory]
    [InlineData(GitHubWriteErrorKind.NotGranted, false, HttpStatusCode.Forbidden, "github_write_not_granted")]
    [InlineData(GitHubWriteErrorKind.AssigneeNotCollaborator, false, HttpStatusCode.UnprocessableEntity, "assignee_not_collaborator")]
    [InlineData(GitHubWriteErrorKind.NotFound, false, HttpStatusCode.NotFound, "not_found")]
    [InlineData(GitHubWriteErrorKind.Unavailable, false, HttpStatusCode.BadGateway, "github_unavailable")]
    [InlineData(GitHubWriteErrorKind.Unavailable, true, HttpStatusCode.GatewayTimeout, "github_unavailable")]
    public async Task Github_errors_leave_no_rows(GitHubWriteErrorKind kind, bool noResponse, HttpStatusCode expected, string code)
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var writer = new FakeWriter { Fail = new GitHubWriteException(kind, "boom", noResponse: noResponse) };
        using var client = Client(writer);

        var (status, body) = await SendAsync(client, t, HttpMethod.Post, "/api/issues", new { repositoryId = repo, title = "x" }, "k");

        Assert.Equal(expected, status);
        Assert.Equal(code, Error(body));
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from issues where tenant_id = @tid", new { tid = t.Id }));
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from audit_log where tenant_id = @tid and action = 'issue.create'", new { tid = t.Id }));

        var retry = await SendAsync(client, t, HttpMethod.Post, "/api/issues", new { repositoryId = repo, title = "x" }, "k");
        Assert.Equal(noResponse ? HttpStatusCode.Conflict : expected, retry.Status);
    }

    [DbFact]
    public async Task Board_fields_are_applied_with_events()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        using var client = Client(new FakeWriter());

        var (status, body) = await SendAsync(client, t, HttpMethod.Post, "/api/issues",
            new { repositoryId = repo, title = "x", status = "in_progress", priority = "high", size = "M" }, "k");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("in_progress", body.GetProperty("status").GetString());
        Assert.Equal("high", body.GetProperty("priority").GetString());
        Assert.Equal("M", body.GetProperty("size").GetString());
        var id = body.GetProperty("id").GetGuid();
        var events = (await t.QueryAsync("select kind, to_value from issue_events where issue_id = @id order by kind", new { id }))
            .Select(r => $"{r.kind}:{r.to_value}").ToList();
        Assert.Equal(["priority_set:high", "size_set:M", "status_set:in_progress"], events);
    }

    [DbFact]
    public async Task Options_list_creatable_repositories_labels_and_members_of_the_tenant_only()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var other = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t, "api");
        var old = await ConnectedRepoAsync(t, "old", archived: true);
        await ConnectedRepoAsync(other, "secret");
        await t.ExecAsync("insert into labels (tenant_id, repository_id, github_label_id, name, color) values (@tid, @repo, @gid, 'bug', 'ff0000')",
            new { tid = t.Id, repo, gid = TestTenant.NextId() });
        await other.ExecAsync("insert into labels (tenant_id, repository_id, github_label_id, name) select @tid, id, @gid, 'foreign' from repositories where tenant_id = @tid",
            new { tid = other.Id, gid = TestTenant.NextId() });
        using var client = Client(new FakeWriter());

        var (status, body) = await SendAsync(client, t, HttpMethod.Get, "/api/issues/new-options");

        Assert.Equal(HttpStatusCode.OK, status);
        var repos = body.GetProperty("repositories").EnumerateArray().ToList();
        Assert.Equal([repo, old], repos.Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal([false, true], repos.Select(r => r.GetProperty("archived").GetBoolean()));
        Assert.Equal(["bug"], repos[0].GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        Assert.Equal([t.ViewerLogin], body.GetProperty("assignees").EnumerateArray().Select(m => m.GetProperty("githubLogin").GetString()));
    }

    [DbFact]
    public async Task Unknown_labels_are_400_with_their_names()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        await AddLabelAsync(t, repo, "bug");
        var writer = new FakeWriter();
        using var client = Client(writer);

        var (status, body) = await SendAsync(client, t, HttpMethod.Post, "/api/issues",
            new { repositoryId = repo, title = "x", labels = new[] { "BUG", "nope", "Other" } }, "k");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_request", Error(body));
        Assert.Equal(["nope", "Other"], body.GetProperty("unknownLabels").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0, writer.Calls);
    }

    [DbTheory]
    [InlineData("mirror")]
    [InlineData("finish")]
    public async Task Local_failure_after_github_create_completes_the_key_and_retry_replays(string failing)
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var writer = new FakeWriter();
        using var client = Client(writer, s =>
        {
            if (failing == "mirror")
            {
                s.RemoveAll<MirrorWriter>();
                s.AddSingleton<MirrorWriter>(sp => new ThrowingMirror(sp.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.Sync)));
            }
            else
            {
                s.RemoveAll<IAuditLog>();
                s.AddScoped<IAuditLog, ThrowingAudit>();
            }
        });
        var payload = new { repositoryId = repo, title = "x" };

        var first = await SendAsync(client, t, HttpMethod.Post, "/api/issues", payload, "k");
        var retry = await SendAsync(client, t, HttpMethod.Post, "/api/issues", payload, "k");

        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        Assert.True(first.Body.GetProperty("mirrorPending").GetBoolean());
        Assert.Equal($"org-{t.Suffix}/api", first.Body.GetProperty("repository").GetString());
        Assert.Equal(101, first.Body.GetProperty("number").GetInt32());
        Assert.Contains("https://x/api/", first.Body.GetProperty("htmlUrl").GetString());
        Assert.Equal(HttpStatusCode.Accepted, retry.Status);
        Assert.Equal(1, writer.Calls);
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from audit_log where tenant_id = @tid and action = 'issue.create'", new { tid = t.Id }));
    }

    [DbFact]
    public async Task Parallel_double_submit_with_one_key_calls_github_once()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var repo = await ConnectedRepoAsync(t);
        var writer = new FakeWriter { Delay = TimeSpan.FromMilliseconds(500) };
        using var client = Client(writer);
        var payload = new { repositoryId = repo, title = "x" };

        var results = await Task.WhenAll(
            SendAsync(client, t, HttpMethod.Post, "/api/issues", payload, "p"),
            SendAsync(client, t, HttpMethod.Post, "/api/issues", payload, "p"));

        Assert.Equal(1, writer.Calls);
        Assert.Contains(results, r => r.Status == HttpStatusCode.Created);
        Assert.All(results, r => Assert.True(
            r.Status == HttpStatusCode.Created || (r.Status == HttpStatusCode.Conflict && Error(r.Body) == "request_in_progress")));
        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from issues where tenant_id = @tid", new { tid = t.Id }));
    }
}
