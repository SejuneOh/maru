using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkBoard.Api.Features.Issues;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Writes;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class AssigneesAndCommentsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private static readonly SourceUser Bot = new(9, "app[bot]", null, null);

    private sealed class FakeWriter : IGitHubWriter
    {
        public long GithubId;
        public int Number;
        public Dictionary<string, long> Ids = [];
        public List<string> SetCalls = [];
        public bool DropAssignees;
        public int CommentCalls;
        public string? Actor;
        public string? CommentBody;
        public string? Marker;
        public int FindCalls;
        public DateTimeOffset? FindSince;
        public SourceComment? Existing;
        public Exception? FindFail;
        public Exception? Fail;
        private int _tick;
        private long _commentId = TestTenant.NextId() * 10;

        public Task<SourceIssue> SetAssigneesAsync(GitHubRepoRef repo, int number, IReadOnlyList<string> assignees, CancellationToken ct)
        {
            SetCalls.Add(string.Join(",", assignees));
            if (Fail is not null)
                throw Fail;
            var users = DropAssignees ? [] : assignees.Select(a => new SourceUser(Ids.GetValueOrDefault(a, 1), a, null, null)).ToList();
            return Task.FromResult(new SourceIssue(GithubId, "n", Number, "t", null, null, "open", null, null,
                DateTimeOffset.UtcNow.AddDays(-1), null, DateTimeOffset.UtcNow.AddSeconds(++_tick), "https://x/1", null, [], users, null, null));
        }

        public Task<SourceComment> AddCommentAsync(GitHubRepoRef repo, string actorLogin, int number, string body, CancellationToken ct, string? marker = null)
        {
            Interlocked.Increment(ref CommentCalls);
            Actor = actorLogin;
            CommentBody = body;
            Marker = marker;
            if (Fail is not null)
                throw Fail;
            return Task.FromResult(Comment(number, body + GitHubWriter.Footer(actorLogin) + "\n" + marker));
        }

        public SourceComment Comment(int number, string body) =>
            new(Interlocked.Increment(ref _commentId), number, Bot, body, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "https://x/c");

        public Task<SourceComment?> FindCreatedCommentAsync(GitHubRepoRef repo, int number, string marker, DateTimeOffset since, CancellationToken ct)
        {
            FindCalls++;
            FindSince = since;
            return FindFail is not null ? throw FindFail : Task.FromResult(Existing);
        }

        public Task<SourceIssue> CreateIssueAsync(GitHubRepoRef repo, string actorLogin, string title, string? body,
            IReadOnlyList<string> labels, IReadOnlyList<string> assignees, CancellationToken ct, string? marker = null) =>
            throw new NotSupportedException();

        public Task<SourceIssue?> FindCreatedIssueAsync(GitHubRepoRef repo, string marker, DateTimeOffset since, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SourceIssue> CloseIssueAsync(GitHubRepoRef repo, int number, IssueCloseReason reason, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SourceIssue> ReopenIssueAsync(GitHubRepoRef repo, int number, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed record Card(TestTenant Tenant, Guid Repo, Guid Issue, FakeWriter Writer, HttpClient Client);

    private async Task<Card> CardAsync(TestTenant t, bool archived = false)
    {
        await t.ExecAsync("update github_connections set installation_id = @installation where id = @id",
            new { id = t.ConnectionId, installation = TestTenant.NextId() });
        var repo = await t.AddRepoAsync("r" + Guid.NewGuid().ToString("N")[..6]);
        await t.ExecAsync("update repositories set full_name = 'org/' || name, archived = @archived where id = @repo", new { repo, archived });
        var issue = await t.AddIssueAsync(repo);
        var writer = new FakeWriter
        {
            GithubId = await t.ScalarAsync<long>("select github_issue_id from issues where id = @issue", new { issue }),
            Number = await t.ScalarAsync<int>("select number from issues where id = @issue", new { issue }),
        };
        var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IGitHubWriter>();
            s.AddSingleton<IGitHubWriter>(writer);
        }));
        _hosts.Add(host);
        return new Card(t, repo, issue, writer, host.CreateClient());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        Card c, HttpMethod method, string url, object? body, string? key = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Dev-User", c.Tenant.ViewerLogin);
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await c.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text));
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> AssignAsync(Card c, long[] ids, string? key = "k", Guid? issue = null) =>
        SendAsync(c, HttpMethod.Patch, $"/api/issues/{issue ?? c.Issue}", new { assigneeGithubUserIds = ids }, key);

    private static Task<(HttpStatusCode Status, JsonElement Body)> CommentAsync(Card c, string body, string? key = "k") =>
        SendAsync(c, HttpMethod.Post, $"/api/issues/{c.Issue}/comments", new { body }, key);

    private static string Err(JsonElement e) => e.GetProperty("error").GetString()!;

    private static Task<int> CountAsync(TestTenant t, string table, Guid issue) =>
        t.ScalarAsync<int>($"select count(*)::int from {table} where issue_id = @issue", new { issue });

    private static Task<int> AuditAsync(TestTenant t, string action) =>
        t.ScalarAsync<int>("select count(*)::int from audit_log where tenant_id = @tid and action = @action", new { tid = t.Id, action });

    [DbFact]
    public async Task Assignees_are_replaced_on_github_then_in_the_mirror_with_an_audit_row()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        var (alice, aliceId) = await t.AddMemberAsync("alice");
        var (bob, bobId) = await t.AddMemberAsync("bob");
        c.Writer.Ids[alice] = aliceId;
        c.Writer.Ids[bob] = bobId;
        await t.AssignAsync(c.Issue, aliceId);

        var (status, body) = await AssignAsync(c, [bobId, aliceId]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([$"{alice},{bob}"], c.Writer.SetCalls);
        Assert.Equal([aliceId, bobId], body.GetProperty("assignees").EnumerateArray().Select(a => a.GetProperty("githubUserId").GetInt64()).Order());
        Assert.Empty(body.GetProperty("droppedAssignees").EnumerateArray());
        Assert.Equal(2, await CountAsync(t, "issue_assignees", c.Issue));
        var audit = (await t.QueryAsync("select before::text as b, after::text as a from audit_log where tenant_id = @tid and action = 'issue.assignees_update'",
            new { tid = t.Id })).Single();
        Assert.Contains(alice, (string)audit.b);
        Assert.DoesNotContain(bob, (string)audit.b);
        Assert.Contains(bob, (string)audit.a);

        var (cleared, clearedBody) = await AssignAsync(c, [], "k2");
        Assert.Equal(HttpStatusCode.OK, cleared);
        Assert.Empty(clearedBody.GetProperty("assignees").EnumerateArray());
        Assert.Equal(0, await CountAsync(t, "issue_assignees", c.Issue));
    }

    [DbFact]
    public async Task Assignees_outside_the_tenant_and_bad_bodies_are_400_without_calling_github()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var other = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        var (_, outsider) = await other.AddMemberAsync("eve");

        Assert.Equal(HttpStatusCode.BadRequest, (await AssignAsync(c, [outsider])).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await AssignAsync(c, Enumerable.Range(1, 11).Select(i => (long)i).ToArray(), "k2")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(c, HttpMethod.Patch, $"/api/issues/{c.Issue}", new { title = "x" }, "k3")).Status);
        Assert.Empty(c.Writer.SetCalls);
    }

    [DbFact]
    public async Task Issue_of_another_tenant_is_404_and_archived_repository_is_409()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        await using var other = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        var foreign = await CardAsync(other);
        var archived = await CardAsync(t, archived: true);

        Assert.Equal(HttpStatusCode.NotFound, (await AssignAsync(c, [], "a", foreign.Issue)).Status);
        var (status, body) = await AssignAsync(archived, [], "b");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("repository_archived", Err(body));
        var (commentStatus, commentBody) = await CommentAsync(archived, "hi", "c");
        Assert.Equal(HttpStatusCode.Conflict, commentStatus);
        Assert.Equal("repository_archived", Err(commentBody));
        Assert.Empty(c.Writer.SetCalls);
        Assert.Empty(archived.Writer.SetCalls);
        Assert.Equal(0, archived.Writer.CommentCalls);
    }

    [DbFact]
    public async Task Assignees_github_did_not_apply_are_reported()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        var (_, carolId) = await t.AddMemberAsync("carol");
        c.Writer.DropAssignees = true;

        var (status, body) = await AssignAsync(c, [carolId]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([carolId], body.GetProperty("droppedAssignees").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Empty(body.GetProperty("assignees").EnumerateArray());
    }

    [DbFact]
    public async Task Github_422_on_assignees_is_assignee_not_collaborator_and_changes_nothing()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        var (_, carolId) = await t.AddMemberAsync("carol");
        var (_, daveId) = await t.AddMemberAsync("dave");
        await t.AssignAsync(c.Issue, daveId);
        c.Writer.Fail = new GitHubWriteException(GitHubWriteErrorKind.AssigneeNotCollaborator, "422");

        var (status, body) = await AssignAsync(c, [carolId]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("assignee_not_collaborator", Err(body));
        Assert.Equal(daveId, await t.ScalarAsync<long>("select github_user_id from issue_assignees where issue_id = @i", new { i = c.Issue }));
        Assert.Equal(0, await AuditAsync(t, "issue.assignees_update"));
    }

    [DbFact]
    public async Task Comment_is_posted_by_the_app_for_the_caller_and_shown_as_theirs()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);

        var (status, body) = await CommentAsync(c, "  looks good  ");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(t.ViewerLogin, c.Writer.Actor);
        Assert.Equal("looks good", c.Writer.CommentBody);
        Assert.StartsWith("<!-- work-board:comment:", c.Writer.Marker);
        Assert.Equal("looks good", body.GetProperty("body").GetString());
        Assert.Equal(t.ViewerLogin, body.GetProperty("author").GetProperty("githubLogin").GetString());
        Assert.False(body.GetProperty("mirrorPending").GetBoolean());
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(1, await CountAsync(t, "comments", c.Issue));
        Assert.Equal(1, await AuditAsync(t, "issue.comment"));

        var shown = (await t.GetAsync($"/api/issues/{c.Issue}")).GetProperty("comments").EnumerateArray().Single();
        Assert.Equal(id, shown.GetProperty("id").GetGuid());
        Assert.Equal("looks good", shown.GetProperty("body").GetString());
        Assert.Equal(t.ViewerLogin, shown.GetProperty("author").GetProperty("githubLogin").GetString());
        Assert.Equal("viewer", shown.GetProperty("author").GetProperty("displayName").GetString());
    }

    [DbFact]
    public async Task Footer_typed_by_a_person_does_not_change_the_author()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        var (mallory, malloryId) = await t.AddMemberAsync("mallory");
        var text = "hi" + GitHubWriter.Footer(t.ViewerLogin);
        await t.ExecAsync("insert into github_users (tenant_id, github_user_id, login) values (@tid, @id, @login)",
            new { tid = t.Id, id = malloryId, login = mallory });
        await t.ExecAsync("""
            insert into comments (tenant_id, issue_id, github_comment_id, author_github_id, body, github_created_at, github_updated_at)
            values (@tid, @issue, @gid, @author, @text, now(), now())
            """, new { tid = t.Id, issue = c.Issue, gid = TestTenant.NextId(), author = malloryId, text });

        var shown = (await t.GetAsync($"/api/issues/{c.Issue}")).GetProperty("comments").EnumerateArray().Single();

        Assert.Equal(mallory, shown.GetProperty("author").GetProperty("githubLogin").GetString());
        Assert.Equal(text, shown.GetProperty("body").GetString());
    }

    [DbFact]
    public async Task Comment_needs_a_key_and_a_non_blank_body()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);

        Assert.Equal(HttpStatusCode.BadRequest, (await CommentAsync(c, "hi", key: null)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CommentAsync(c, "   ", "k1")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CommentAsync(c, new string('x', 60_001), "k2")).Status);
        Assert.Equal(0, c.Writer.CommentCalls);
    }

    [DbFact]
    public async Task Same_key_twice_posts_one_comment()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);

        var first = await CommentAsync(c, "once");
        var second = await CommentAsync(c, "once");
        var other = await CommentAsync(c, "different");

        Assert.Equal(HttpStatusCode.Created, second.Status);
        Assert.Equal(first.Body.GetProperty("id").GetGuid(), second.Body.GetProperty("id").GetGuid());
        Assert.Equal("idempotency_key_reused", Err(other.Body));
        Assert.Equal(1, c.Writer.CommentCalls);
        Assert.Equal(1, await CountAsync(t, "comments", c.Issue));
    }

    [DbTheory]
    [InlineData(GitHubWriteErrorKind.NotGranted, false, HttpStatusCode.Forbidden, "github_write_not_granted")]
    [InlineData(GitHubWriteErrorKind.InvalidRequest, false, HttpStatusCode.UnprocessableEntity, "invalid_request")]
    [InlineData(GitHubWriteErrorKind.Unavailable, true, HttpStatusCode.GatewayTimeout, "github_unavailable")]
    public async Task Github_errors_on_a_comment_leave_no_rows(GitHubWriteErrorKind kind, bool noResponse, HttpStatusCode expected, string code)
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        c.Writer.Fail = new GitHubWriteException(kind, "boom", noResponse: noResponse);

        var (status, body) = await CommentAsync(c, "hi");

        Assert.Equal(expected, status);
        Assert.Equal(code, Err(body));
        Assert.Equal(0, await CountAsync(t, "comments", c.Issue));
        Assert.Equal(0, await AuditAsync(t, "issue.comment"));
        var retry = await CommentAsync(c, "hi");
        Assert.Equal(noResponse ? HttpStatusCode.Conflict : expected, retry.Status);
        if (noResponse)
            Assert.Equal("request_in_progress", Err(retry.Body));
    }

    private static async Task SeedStalePendingAsync(Card c, string key, string text)
    {
        var t = c.Tenant;
        var memberId = await t.ScalarAsync<Guid>("select id from members where tenant_id = @tid and github_login = @login",
            new { tid = t.Id, login = t.ViewerLogin });
        var hash = Idempotency.HashRequest(memberId, "POST", $"/api/issues/{c.Issue}/comments",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { body = text })));
        await t.ExecAsync("""
            insert into idempotency_keys (tenant_id, key, member_id, request_hash, state, reservation_id, created_at)
            values (@tid, @key, @memberId, @hash, 'pending', gen_random_uuid(), now() - interval '10 minutes')
            """, new { tid = t.Id, key, memberId, hash });
    }

    [DbFact]
    public async Task Takeover_that_finds_the_marked_comment_does_not_post_again()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        await SeedStalePendingAsync(c, "k", "hi");
        c.Writer.Existing = c.Writer.Comment(c.Writer.Number, "hi" + GitHubWriter.Footer(t.ViewerLogin));

        var (status, body) = await CommentAsync(c, "hi");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(0, c.Writer.CommentCalls);
        Assert.Equal(1, c.Writer.FindCalls);
        Assert.True(c.Writer.FindSince < DateTimeOffset.UtcNow.AddMinutes(-14));
        Assert.Equal(1, await CountAsync(t, "comments", c.Issue));
        Assert.Equal("hi", body.GetProperty("body").GetString());
    }

    [DbFact]
    public async Task Takeover_that_finds_nothing_posts_once_and_a_failed_lookup_posts_nothing()
    {
        await using var t = await TestTenant.CreateAsync(factory);
        var c = await CardAsync(t);
        await SeedStalePendingAsync(c, "k", "hi");
        await SeedStalePendingAsync(c, "k2", "hi");

        var (status, _) = await CommentAsync(c, "hi");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(1, c.Writer.CommentCalls);

        c.Writer.FindFail = new GitHubWriteException(GitHubWriteErrorKind.Unavailable, "502");
        var (failed, body) = await CommentAsync(c, "hi", "k2");
        Assert.Equal(HttpStatusCode.GatewayTimeout, failed);
        Assert.Equal("github_unavailable", Err(body));
        Assert.Equal(1, c.Writer.CommentCalls);
        Assert.Equal("pending", await t.ScalarAsync<string>("select state from idempotency_keys where tenant_id = @tid and key = 'k2'", new { tid = t.Id }));
    }

    [Theory]
    [InlineData("hi\n\n---\n_작성: `@carol` (work-board)_", "hi", "carol")]
    [InlineData("hi\r\n\r\n---\r\n_작성: `@carol-2` (work-board)_\r\n<!-- work-board:comment:0123abcd -->", "hi", "carol-2")]
    [InlineData("\n\n---\n_작성: `@carol` (work-board)_\n<!-- work-board:create:ab -->\n", "", "carol")]
    public void Footer_is_stripped_and_names_the_member(string body, string expected, string login)
    {
        var own = BoardFooter.Strip(body);
        Assert.NotNull(own);
        Assert.Equal(expected, own.Value.Body);
        Assert.Equal(login, own.Value.Login);
    }

    [Theory]
    [InlineData("hi")]
    [InlineData("hi\n\n---\n_작성: `@carol` (work-board)_ and more")]
    public void Body_without_a_trailing_footer_is_left_alone(string body) => Assert.Null(BoardFooter.Strip(body));
}
