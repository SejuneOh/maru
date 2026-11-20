using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class McpClientTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    private HttpClient Client(string login) => _factory.CreateClient().WithDevUser(login);

    private static async Task<string> AdminAsync(TestTenant t, string name = "admin")
    {
        var (login, userId) = await t.AddMemberAsync(name);
        await t.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_user_id = @userId", new { tid = t.Id, userId });
        return login;
    }

    private static async Task<HttpResponseMessage> CreateAsync(
        HttpClient c, string name = "tool", string? scope = null, int? days = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/mcp-clients")
        {
            Content = JsonContent.Create(new { name, scope, expiresInDays = days }),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await c.SendAsync(req);
    }

    private static async Task<(Guid Id, string Token)> CreatedAsync(HttpClient c, string name = "tool", string? scope = null)
    {
        var r = await CreateAsync(c, name, scope);
        r.EnsureSuccessStatusCode();
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("client").GetProperty("id").GetGuid(), body.GetProperty("token").GetString()!);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    [DbFact]
    public async Task Create_shows_token_once_and_stores_only_the_hash()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var r = await CreateAsync(Client(t.ViewerLogin), "my tool");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        Assert.StartsWith("wbm_", token);
        Assert.Equal("read", body.GetProperty("client").GetProperty("scope").GetString());

        // The plaintext is nowhere in the row; only its hash and a short visible prefix are.
        var storedPlain = await t.ScalarAsync<int>(
            "select count(*) from mcp_clients where encode(token_hash, 'hex') = @plain or token_prefix = @plain",
            new { plain = token });
        Assert.Equal(0, storedPlain);
        var prefix = body.GetProperty("client").GetProperty("tokenPrefix").GetString()!;
        Assert.StartsWith(prefix, token);
        Assert.True(prefix.Length < token.Length);

        // The list never returns the token again.
        var list = await (await Client(t.ViewerLogin).GetAsync("/api/mcp-clients")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(list[0].TryGetProperty("token", out _));
    }

    [DbFact]
    public async Task Owner_sees_and_revokes_only_their_own_tokens()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var (otherLogin, _) = await t.AddMemberAsync("other");
        var (mine, _) = await CreatedAsync(Client(t.ViewerLogin), "mine");
        var (theirs, _) = await CreatedAsync(Client(otherLogin), "theirs");

        var list = await (await Client(t.ViewerLogin).GetAsync("/api/mcp-clients")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(mine, list[0].GetProperty("id").GetGuid());

        // Cannot revoke another member's token.
        var denied = await Client(t.ViewerLogin).DeleteAsync($"/api/mcp-clients/{theirs}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Null(await t.ScalarAsync<DateTime?>("select revoked_at from mcp_clients where id = @id", new { id = theirs }));

        // Can revoke their own.
        var ok = await Client(t.ViewerLogin).DeleteAsync($"/api/mcp-clients/{mine}");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.NotNull(await t.ScalarAsync<DateTime?>("select revoked_at from mcp_clients where id = @id", new { id = mine }));
    }

    [DbFact]
    public async Task Admin_lists_and_revokes_every_token_in_the_company()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var admin = await AdminAsync(t);
        var (memberToken, _) = await CreatedAsync(Client(t.ViewerLogin), "member tool");

        var all = await (await Client(admin).GetAsync("/api/mcp-clients?all=true")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(all.EnumerateArray(), e => e.GetProperty("id").GetGuid() == memberToken);
        Assert.True(all.EnumerateArray().First(e => e.GetProperty("id").GetGuid() == memberToken)
            .GetProperty("owner").GetProperty("githubLogin").GetString()!.Length > 0);

        var revoke = await Client(admin).DeleteAsync($"/api/mcp-clients/{memberToken}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.NotNull(await t.ScalarAsync<DateTime?>("select revoked_at from mcp_clients where id = @id", new { id = memberToken }));
    }

    [DbFact]
    public async Task Another_company_cannot_see_or_revoke_the_tokens()
    {
        var a = await TestTenant.CreateAsync(_factory);
        var b = await TestTenant.CreateAsync(_factory);
        var bAdmin = await AdminAsync(b);
        var (aToken, _) = await CreatedAsync(Client(a.ViewerLogin), "a tool");

        // Admin of company B sees none of company A's tokens.
        var listB = await (await Client(bAdmin).GetAsync("/api/mcp-clients?all=true")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(listB.EnumerateArray(), e => e.GetProperty("id").GetGuid() == aToken);

        // And cannot revoke it.
        var revoke = await Client(bAdmin).DeleteAsync($"/api/mcp-clients/{aToken}");
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);
        Assert.Null(await a.ScalarAsync<DateTime?>("select revoked_at from mcp_clients where id = @id", new { id = aToken }));
    }

    [DbFact]
    public async Task Removing_a_member_revokes_their_tokens()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var admin = await AdminAsync(t);
        var (token, _) = await CreatedAsync(Client(t.ViewerLogin), "leaving tool");
        var memberId = await t.ScalarAsync<Guid>(
            "select id from members where tenant_id = @tid and github_login = @login", new { tid = t.Id, login = t.ViewerLogin });

        var removed = await Client(admin).DeleteAsync($"/api/members/{memberId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.NotNull(await t.ScalarAsync<DateTime?>("select revoked_at from mcp_clients where id = @id", new { id = token }));
    }

    [DbFact]
    public async Task Write_scope_is_accepted_but_bad_scope_expiry_and_name_are_rejected()
    {
        var t = await TestTenant.CreateAsync(_factory);
        var (_, _) = await CreatedAsync(Client(t.ViewerLogin), "writer", "write");

        Assert.Equal("invalid_scope", await ErrorAsync(await CreateAsync(Client(t.ViewerLogin), "x", "admin")));
        Assert.Equal("invalid_expiry", await ErrorAsync(await CreateAsync(Client(t.ViewerLogin), "x", "read", 91)));
        Assert.Equal("invalid_expiry", await ErrorAsync(await CreateAsync(Client(t.ViewerLogin), "x", "read", 0)));
        Assert.Equal("invalid_name", await ErrorAsync(await CreateAsync(Client(t.ViewerLogin), "   ")));
    }
}
