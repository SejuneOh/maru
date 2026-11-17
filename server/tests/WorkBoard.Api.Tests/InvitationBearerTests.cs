using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WorkBoard.Api.Features.Members;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

// The bearer path: the GitHub id comes from the Supabase identity, and the login is never used to match.
public class InvitationBearerTests
{
    private const string SupabaseUrl = "https://test-project.supabase.example";
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test" };

    private sealed class FakeSource : IAuthIdentitySource
    {
        public ConcurrentDictionary<Guid, GithubProfile> Profiles { get; } = new();

        public Task<GithubProfile?> FindGithubProfileAsync(Guid authUserId, CancellationToken ct) =>
            Task.FromResult(Profiles.TryGetValue(authUserId, out var p) ? p : null);
    }

    private sealed class MemoryLinks : IAuthLinkStore
    {
        private readonly ConcurrentDictionary<Guid, long> _links = new();

        public Task<long?> FindAsync(Guid authUserId, CancellationToken ct) =>
            Task.FromResult(_links.TryGetValue(authUserId, out var id) ? id : (long?)null);

        public Task SaveAsync(Guid authUserId, long githubUserId, CancellationToken ct)
        {
            _links[authUserId] = githubUserId;
            return Task.CompletedTask;
        }
    }

    private static string Token(Guid sub)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = SupabaseUrl + "/auth/v1",
            Audience = "authenticated",
            Claims = new Dictionary<string, object> { ["sub"] = sub.ToString() },
            NotBefore = now.AddHours(-1),
            IssuedAt = now.AddHours(-1),
            Expires = now.AddHours(1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    [DbFact]
    public async Task Joins_by_github_id_after_a_rename_and_a_login_match_alone_is_refused()
    {
        var source = new FakeSource();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Supabase:Url"] = SupabaseUrl,
                ["Auth:Supabase:ServiceRoleKey"] = "test-service-role-key",
                ["Auth:Supabase:JwtSecret"] = "",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuthLinkStore>();
                services.AddSingleton<IAuthLinkStore, MemoryLinks>();
                services.RemoveAll<IAuthIdentitySource>();
                services.AddSingleton<IAuthIdentitySource>(source);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    o.ConfigurationManager = null;
                    o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                });
            });
        });
        await using var t = await TestTenant.CreateAsync(factory);
        var invitedId = TestTenant.NextId();
        var token = Invitations.NewToken();
        await t.ExecAsync("""
            insert into invitations (tenant_id, github_user_id, github_login, token_hash, expires_at)
            values (@tid, @invitedId, @login, @hash, now() + interval '1 day')
            """, new { tid = t.Id, invitedId, login = "old-" + invitedId, hash = Invitations.HashToken(token) });

        var impostor = Guid.NewGuid();
        source.Profiles[impostor] = new GithubProfile(TestTenant.NextId(), "old-" + invitedId, null, null);
        var invitee = Guid.NewGuid();
        source.Profiles[invitee] = new GithubProfile(invitedId, "renamed-" + invitedId, "Renamed", null);
        using var client = factory.CreateClient();

        HttpRequestMessage Accept(Guid sub)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/api/invitations/accept") { Content = JsonContent.Create(new { token }) };
            r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token(sub));
            return r;
        }

        var refused = await client.SendAsync(Accept(impostor));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("""{"error":"invite_other_account"}""", await refused.Content.ReadAsStringAsync());

        var joined = await client.SendAsync(Accept(invitee));
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var member = (await joined.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("member");
        Assert.Equal(invitedId, member.GetProperty("githubUserId").GetInt64());
        Assert.Equal("renamed-" + invitedId, member.GetProperty("githubLogin").GetString());
        Assert.Equal("Renamed", member.GetProperty("displayName").GetString());
    }
}
