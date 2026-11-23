using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class McpEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    private HttpClient Client(string? token = null, WebApplicationFactory<Program>? factory = null)
    {
        var c = (factory ?? _factory).CreateClient();
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<string> SeedTokenAsync(TestTenant t, string scope = "read", int expiresInDays = 30)
    {
        var token = "wbm_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var mid = await t.ScalarAsync<Guid>(
            "select id from members where tenant_id = @tid and github_login = @login", new { tid = t.Id, login = t.ViewerLogin });
        await t.ExecAsync("""
            insert into mcp_clients (tenant_id, owner_member_id, name, token_hash, token_prefix, scope, expires_at)
            values (@tid, @mid, 'test', @hash, @prefix, @scope, now() + make_interval(days => @days))
            """, new { tid = t.Id, mid, hash = SHA256.HashData(Encoding.ASCII.GetBytes(token)), prefix = token[..12], scope, days = expiresInDays });
        return token;
    }

    private static Task<HttpResponseMessage> RpcAsync(HttpClient c, string method, object? pars = null, object? id = null)
    {
        object body = pars is null
            ? new { jsonrpc = "2.0", id = id ?? 1, method }
            : new { jsonrpc = "2.0", id = id ?? 1, method, @params = pars };
        return c.PostAsJsonAsync("/mcp", body);
    }

    private static async Task<JsonElement> ResultAsync(HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
    }

    // The data inside a tools/call result: content[0].text is JSON of { warning, data }.
    private static async Task<(bool IsError, JsonElement Data, string Text)> ToolAsync(HttpResponseMessage r)
    {
        var result = await ResultAsync(r);
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        var isError = result.GetProperty("isError").GetBoolean();
        var data = isError ? default : JsonDocument.Parse(text).RootElement.GetProperty("data");
        return (isError, data, text);
    }

    [DbFact]
    public async Task Initialize_and_tools_list_work_with_a_valid_token()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var c = Client(await SeedTokenAsync(t));

        var init = await ResultAsync(await RpcAsync(c, "initialize"));
        Assert.False(string.IsNullOrEmpty(init.GetProperty("protocolVersion").GetString()));
        Assert.True(init.GetProperty("capabilities").TryGetProperty("tools", out _));

        var tools = await ResultAsync(await RpcAsync(c, "tools/list"));
        var names = tools.GetProperty("tools").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("list_issues", names);
        Assert.Contains("get_issue", names);
        Assert.Contains("search", names);
    }

    [DbFact]
    public async Task List_issues_returns_data_under_an_untrusted_warning()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var repo = await t.AddRepoAsync("repo");
        await t.AddIssueAsync(repo, title: "mine-only");
        var c = Client(await SeedTokenAsync(t));

        var r = await RpcAsync(c, "tools/call", new { name = "list_issues", arguments = new { } });
        var (isError, data, _) = await ToolAsync(r);
        Assert.False(isError);
        Assert.Contains(data.EnumerateArray(), e => e.GetProperty("title").GetString() == "mine-only");

        // The warning is present so the model treats returned text as data, not instructions.
        var result = await ResultAsync(await RpcAsync(c, "tools/call", new { name = "list_issues", arguments = new { } }));
        var env = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;
        Assert.True(env.TryGetProperty("warning", out var w) && w.GetString()!.Length > 0);
    }

    [DbFact]
    public async Task No_token_or_a_bad_token_is_unauthorized()
    {
        var t = await TestTenant.CreateAsync(_factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(Client(), "initialize")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(Client("wbm_notarealtoken"), "initialize")).StatusCode);

        // Revoked and expired tokens are rejected.
        var revoked = await SeedTokenAsync(t);
        await t.ExecAsync("update mcp_clients set revoked_at = now() where token_prefix = @p", new { p = revoked[..12] });
        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(Client(revoked), "initialize")).StatusCode);

        var expired = await SeedTokenAsync(t, expiresInDays: 30);
        await t.ExecAsync("update mcp_clients set expires_at = now() - interval '1 day' where token_prefix = @p", new { p = expired[..12] });
        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(Client(expired), "initialize")).StatusCode);
    }

    [DbFact]
    public async Task X_dev_user_is_never_honoured_on_mcp()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", t.ViewerLogin); // no bearer token
        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(c, "initialize")).StatusCode);
    }

    [DbFact]
    public async Task A_disallowed_host_or_origin_is_forbidden_before_work()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var token = await SeedTokenAsync(t);

        var badHost = Client(token);
        badHost.DefaultRequestHeaders.Host = "evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await RpcAsync(badHost, "initialize")).StatusCode);

        var badOrigin = Client(token);
        badOrigin.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await RpcAsync(badOrigin, "initialize")).StatusCode);
    }

    [DbFact]
    public async Task A_token_sees_only_its_own_company()
    {
        var a = await TestTenant.CreateAsync(_factory);
        var b = await TestTenant.CreateAsync(_factory);
        await a.AddIssueAsync(await a.AddRepoAsync("a-repo"), title: "company-a-issue");
        await b.AddIssueAsync(await b.AddRepoAsync("b-repo"), title: "company-b-issue");

        var c = Client(await SeedTokenAsync(a));
        var (_, data, _) = await ToolAsync(await RpcAsync(c, "tools/call", new { name = "list_issues", arguments = new { } }));
        var titles = data.EnumerateArray().Select(e => e.GetProperty("title").GetString()).ToList();
        Assert.Contains("company-a-issue", titles);
        Assert.DoesNotContain("company-b-issue", titles);
    }

    [DbFact]
    public async Task Get_issue_of_another_company_is_not_found()
    {
        var a = await TestTenant.CreateAsync(_factory);
        var b = await TestTenant.CreateAsync(_factory);
        var bIssue = await b.AddIssueAsync(await b.AddRepoAsync("b-repo"), title: "secret");

        var c = Client(await SeedTokenAsync(a));
        var (isError, _, text) = await ToolAsync(await RpcAsync(c, "tools/call",
            new { name = "get_issue", arguments = new { id = bIssue.ToString() } }));
        Assert.True(isError);
        Assert.Contains("not found", text);
    }

    [DbFact]
    public async Task A_token_whose_owner_left_the_company_is_rejected()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var token = await SeedTokenAsync(t);
        await t.ExecAsync("update members set left_at = now() where tenant_id = @tid and github_login = @login",
            new { tid = t.Id, login = t.ViewerLogin });
        Assert.Equal(HttpStatusCode.Unauthorized, (await RpcAsync(Client(token), "initialize")).StatusCode);
    }

    [DbFact]
    public async Task Notifications_get_202_and_unknown_methods_get_a_json_rpc_error()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var c = Client(await SeedTokenAsync(t));

        var note = await c.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", method = "notifications/initialized" });
        Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);

        var unknown = await c.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 7, method = "no/such/method" });
        unknown.EnsureSuccessStatusCode();
        var body = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(-32601, body.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(7, body.GetProperty("id").GetInt32());
    }

    [DbFact]
    public async Task A_result_over_the_size_cap_is_refused()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var token = await SeedTokenAsync(t);
        var tiny = _factory.WithWebHostBuilder(b => b.UseSetting("Mcp:MaxResponseBytes", "10"));

        var r = await RpcAsync(Client(token, tiny), "tools/call", new { name = "get_overview", arguments = new { } });
        var (isError, _, text) = await ToolAsync(r);
        Assert.True(isError);
        Assert.Contains("result_too_large", text);
    }
}
