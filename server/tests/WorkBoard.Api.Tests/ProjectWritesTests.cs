using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class ProjectWritesTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        string login, HttpMethod method, string url, object? body = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Dev-User", login);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text));
    }

    private static async Task<(TestTenant Tenant, string Admin, Guid Repo)> SetupAsync(WebApplicationFactory<Program> factory)
    {
        var t = await TestTenant.CreateAsync(factory);
        var (admin, _) = await t.AddMemberAsync("boss");
        await t.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_login = @login",
            new { tid = t.Id, login = admin });
        var repo = await t.AddRepoAsync("alpha");
        await t.ExecAsync("update repositories set sync_enabled = true where id = @id", new { id = repo });
        return (t, admin, repo);
    }

    [DbFact]
    public async Task Admin_creates_project_with_repositories()
    {
        var (t, admin, repo) = await SetupAsync(factory);
        await using var _ = t;

        var (status, created) = await SendAsync(admin, HttpMethod.Post, "/api/projects",
            new { name = "Platform", repositoryIds = new[] { repo } });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Platform", created.GetProperty("name").GetString());

        var list = await t.GetAsync("/api/projects", admin);
        var found = list.EnumerateArray().First(p => p.GetProperty("name").GetString() == "Platform");
        Assert.Equal(repo.ToString(), found.GetProperty("repositories")[0].GetProperty("id").GetString());
    }

    [DbFact]
    public async Task Non_admin_cannot_create()
    {
        var (t, _, repo) = await SetupAsync(factory);
        await using var _ = t;
        var (status, _) = await SendAsync(t.ViewerLogin, HttpMethod.Post, "/api/projects",
            new { name = "Nope", repositoryIds = new[] { repo } });
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [DbFact]
    public async Task Duplicate_name_and_bad_repo_are_rejected()
    {
        var (t, admin, repo) = await SetupAsync(factory);
        await using var _ = t;
        await SendAsync(admin, HttpMethod.Post, "/api/projects", new { name = "Dup", repositoryIds = new[] { repo } });

        var (dup, _) = await SendAsync(admin, HttpMethod.Post, "/api/projects", new { name = "Dup" });
        Assert.Equal(HttpStatusCode.Conflict, dup);

        var (bad, _) = await SendAsync(admin, HttpMethod.Post, "/api/projects",
            new { name = "Other", repositoryIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
    }

    [DbFact]
    public async Task Admin_updates_and_deletes()
    {
        var (t, admin, repo) = await SetupAsync(factory);
        await using var _ = t;
        var repo2 = await t.AddRepoAsync("beta");
        await t.ExecAsync("update repositories set sync_enabled = true where id = @id", new { id = repo2 });

        var (_, created) = await SendAsync(admin, HttpMethod.Post, "/api/projects",
            new { name = "Edit", repositoryIds = new[] { repo } });
        var id = Guid.Parse(created.GetProperty("id").GetString()!);

        var (patch, body) = await SendAsync(admin, HttpMethod.Patch, $"/api/projects/{id}",
            new { name = "Edited", repositoryIds = new[] { repo2 } });
        Assert.Equal(HttpStatusCode.OK, patch);
        Assert.Equal("Edited", body.GetProperty("name").GetString());
        Assert.Equal(repo2.ToString(), body.GetProperty("repositories")[0].GetProperty("id").GetString());

        var (del, _) = await SendAsync(admin, HttpMethod.Delete, $"/api/projects/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del);
        var gone = await t.GetAsync("/api/projects", admin);
        Assert.DoesNotContain(gone.EnumerateArray(), p => p.GetProperty("name").GetString() == "Edited");
    }
}
