using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using WorkBoard.Api.Infrastructure.Credentials;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;

namespace WorkBoard.Api.Tests.Fakes;

// Key version 2, so the global re-encryption test (current version 2) leaves these rows alone.
public sealed class TestKeys : ICredentialKeys
{
    private readonly CredentialKeyring _ring = new(2, new Dictionary<int, byte[]> { [2] = new byte[CredentialKeyring.KeySize] });
    public CredentialKeyring Get() => _ring;
}

// Answers the owner membership check a sync run of a token connection makes, and the identity check of PUT github-token.
public sealed class FakeMembership : IGitHubTokenClient
{
    public TokenIdentity? Identity { get; set; }
    public OrgAccess? Access { get; set; } = new(OrgAccessStatus.Active, 1, "org");
    public int Checks;

    public Task<TokenIdentity?> GetIdentityAsync(string token, CancellationToken ct) => Task.FromResult(Identity);

    public Task<OrgAccess?> GetOrganizationAccessAsync(string token, string organization, CancellationToken ct)
    {
        Interlocked.Increment(ref Checks);
        return Task.FromResult(Access is { Status: OrgAccessStatus.Active } a ? a with { OrgId = OrgId, OrgLogin = organization } : Access);
    }

    public long OrgId { get; set; } = 1;
}

public static class TokenFakes
{
    public const string Token = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";

    public static CredentialStore Store(WebApplicationFactory<Program> factory) =>
        new(factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.Sync), new CredentialCipher(new TestKeys()));

    // A host whose sync run, owner check and credential store are fakes or in-memory keys. No call leaves the process.
    public static HttpClient Client(
        WebApplicationFactory<Program> factory, string login, IGitHubSource? source = null, IGitHubTokenClient? membership = null,
        bool appReady = false, List<IDisposable>? owned = null)
    {
        var host = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:Enabled"] = "false", ["GitHub:App:AppId"] = appReady ? "1" : "0",
                ["GitHub:App:PrivateKeyPath"] = appReady ? "unused.pem" : "",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ICredentialKeys>();
                s.AddSingleton<ICredentialKeys, TestKeys>();
                if (source is not null)
                {
                    s.RemoveAll<IGitHubSource>();
                    s.AddSingleton(source);
                }
                if (membership is not null)
                {
                    s.RemoveAll<IGitHubTokenClient>();
                    s.AddSingleton(membership);
                }
            });
        });
        owned?.Add(host);
        return host.CreateClient().WithDevUser(login);
    }

    public static async Task MakeTokenConnectionAsync(TestTenant t) =>
        await t.ExecAsync("update github_connections set auth_kind = 'token', store_bodies = false where id = @id", new { id = t.ConnectionId });

    // Stores a sealed token for the connection, owned by the member with that GitHub user id.
    public static async Task AddCredentialAsync(
        WebApplicationFactory<Program> factory, TestTenant t, long ownerUserId, DateTimeOffset? expiresAt = null,
        string status = "active", string token = Token)
    {
        var memberId = await t.ScalarAsync<Guid>("select id from members where tenant_id = @tid and github_user_id = @ownerUserId",
            new { tid = t.Id, ownerUserId });
        await Store(factory).SaveAsync(t.Id, t.ConnectionId, memberId, token, ["repo", "read:org"], expiresAt, default, "owner-" + t.Suffix);
        if (status != "active")
            await t.ExecAsync("update github_credentials set status = @status where connection_id = @id", new { status, id = t.ConnectionId });
    }

    public static Task<string?> StatusOfAsync(TestTenant t) =>
        t.ScalarAsync<string?>("select status from github_credentials where connection_id = @id", new { id = t.ConnectionId });

    public static async Task WaitSyncIdleAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            var status = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/sync/status");
            if (!status.GetProperty("running").GetBoolean())
                return;
        }
        throw new TimeoutException("sync did not finish");
    }
}
