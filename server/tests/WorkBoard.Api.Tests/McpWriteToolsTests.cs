using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class McpWriteToolsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    private HttpClient Client(string token)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<(string Token, Guid Id)> SeedTokenAsync(TestTenant t, string scope)
    {
        var token = "wbm_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var mid = await t.ScalarAsync<Guid>(
            "select id from members where tenant_id = @tid and github_login = @login", new { tid = t.Id, login = t.ViewerLogin });
        var id = await t.ScalarAsync<Guid>("""
            insert into mcp_clients (tenant_id, owner_member_id, name, token_hash, token_prefix, scope, expires_at)
            values (@tid, @mid, 'test', @hash, @prefix, @scope, now() + interval '30 days')
            returning id
            """, new { tid = t.Id, mid, hash = SHA256.HashData(Encoding.ASCII.GetBytes(token)), prefix = token[..12], scope });
        return (token, id);
    }

    private static Task<HttpResponseMessage> RpcAsync(HttpClient c, string method, object? pars = null)
    {
        object body = pars is null ? new { jsonrpc = "2.0", id = 1, method } : new { jsonrpc = "2.0", id = 1, method, @params = pars };
        return c.PostAsJsonAsync("/mcp", body);
    }

    private static async Task<JsonElement> ResultAsync(HttpResponseMessage r)
    {
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
    }

    private static async Task<List<string>> ToolNamesAsync(HttpClient c) =>
        (await ResultAsync(await RpcAsync(c, "tools/list"))).GetProperty("tools").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToList();

    private static Task<HttpResponseMessage> CallAsync(HttpClient c, string name, object args) =>
        RpcAsync(c, "tools/call", new { name, arguments = args });

    [DbFact]
    public async Task A_read_token_cannot_see_or_run_write_tools()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var (token, _) = await SeedTokenAsync(t, "read");
        var c = Client(token);

        var names = await ToolNamesAsync(c);
        Assert.DoesNotContain("change_card", names);
        Assert.DoesNotContain("add_comment", names);

        // Calling one is "Unknown tool" (a JSON-RPC error), never executed.
        var body = await (await CallAsync(c, "change_card", new { id = Guid.NewGuid(), status = "in_progress" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(-32602, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    [DbFact]
    public async Task A_write_token_lists_the_write_tools()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var (token, _) = await SeedTokenAsync(t, "write");
        var names = await ToolNamesAsync(Client(token));
        Assert.Contains("change_card", names);
        Assert.Contains("add_comment", names);
        Assert.Contains("list_issues", names); // read tools are still there
    }

    [DbFact]
    public async Task Change_card_moves_the_card_and_records_the_mcp_client()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var issue = await t.AddIssueAsync(await t.AddRepoAsync("repo"));
        var (token, clientId) = await SeedTokenAsync(t, "write");

        var result = await ResultAsync(await CallAsync(Client(token), "change_card", new { id = issue.ToString(), status = "in_progress" }));
        Assert.False(result.GetProperty("isError").GetBoolean());

        Assert.Equal("in_progress", await t.ScalarAsync<string?>("select status_override from board_fields where issue_id = @i", new { i = issue }));
        // The audit row names the owner as member and this MCP client as the tool that did it.
        Assert.Equal(clientId, await t.ScalarAsync<Guid?>(
            "select mcp_client_id from audit_log where action = 'issue.board_update' and target_id = @i", new { i = issue }));
    }

    [DbFact]
    public async Task A_write_token_cannot_change_another_companys_card()
    {
        var a = await TestTenant.CreateAsync(_factory);
        var b = await TestTenant.CreateAsync(_factory);
        var bIssue = await b.AddIssueAsync(await b.AddRepoAsync("b-repo"));
        var (token, _) = await SeedTokenAsync(a, "write");

        // Company A's token sees nothing of company B, so the issue is not found and nothing changes.
        var result = await ResultAsync(await CallAsync(Client(token), "change_card", new { id = bIssue.ToString(), status = "in_progress" }));
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(0, await b.ScalarAsync<int>("select count(*) from board_fields where issue_id = @i", new { i = bIssue }));
    }

    [DbFact]
    public async Task Change_card_rejects_a_bad_value_or_no_field()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var issue = await t.AddIssueAsync(await t.AddRepoAsync("repo"));
        var (token, _) = await SeedTokenAsync(t, "write");
        var c = Client(token);

        var bad = await ResultAsync(await CallAsync(c, "change_card", new { id = issue.ToString(), status = "flying" }));
        Assert.True(bad.GetProperty("isError").GetBoolean());

        var empty = await ResultAsync(await CallAsync(c, "change_card", new { id = issue.ToString() }));
        Assert.True(empty.GetProperty("isError").GetBoolean());

        // Closing an issue (done/cancelled) is out of scope for AI tools.
        var closing = await ResultAsync(await CallAsync(c, "change_card", new { id = issue.ToString(), status = "done" }));
        Assert.True(closing.GetProperty("isError").GetBoolean());
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*) from board_fields where issue_id = @i", new { i = issue }));
    }
}
