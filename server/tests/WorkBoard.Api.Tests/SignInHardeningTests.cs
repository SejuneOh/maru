using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WorkBoard.Api.Infrastructure.Identity;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace WorkBoard.Api.Tests;

public class SignInHardeningTests
{
    private const string SupabaseUrl = "https://test-project.supabase.example";
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test" };

    private static SupabaseAuthOptions Options(string url = SupabaseUrl, string? secret = null) =>
        new() { Url = url, ServiceRoleKey = "test-service-role-key", JwtSecret = secret };

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    private static SupabaseAdminIdentitySource Source(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new Handler(respond)), MsOptions.Create(Options()));

    private sealed class ThrowingSource : IAuthIdentitySource
    {
        public Task<GithubProfile?> FindGithubProfileAsync(Guid authUserId, CancellationToken ct) =>
            throw new IdentityUnavailableException("down");
    }

    private sealed class CountingSource(GithubProfile? answer) : IAuthIdentitySource
    {
        public int Calls { get; private set; }

        public Task<GithubProfile?> FindGithubProfileAsync(Guid authUserId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    private sealed class MemoryLinks(long? existing = null) : IAuthLinkStore
    {
        public Task<long?> FindAsync(Guid authUserId, CancellationToken ct) => Task.FromResult(existing);

        public Task SaveAsync(Guid authUserId, long githubUserId, CancellationToken ct) => Task.CompletedTask;
    }

    // 1. JWKS signing algorithms

    [Fact]
    public void Jwks_path_accepts_only_rs256_and_es256()
    {
        var setup = new SupabaseJwtBearerSetup(MsOptions.Create(Options()));
        var bearer = new JwtBearerOptions();

        setup.Configure(JwtBearerDefaults.AuthenticationScheme, bearer);

        Assert.Equal(
            [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
            bearer.TokenValidationParameters.ValidAlgorithms);
    }

    [Fact]
    public void Shared_secret_path_still_accepts_only_hs256()
    {
        var setup = new SupabaseJwtBearerSetup(MsOptions.Create(Options(secret: new string('s', 40))));
        var bearer = new JwtBearerOptions();

        setup.Configure(JwtBearerDefaults.AuthenticationScheme, bearer);

        Assert.Equal([SecurityAlgorithms.HmacSha256], bearer.TokenValidationParameters.ValidAlgorithms);
    }

    // 2. https outside Development

    [Theory]
    [InlineData("Production", "http://test-project.supabase.example", false)]
    [InlineData("Production", "https://test-project.supabase.example", true)]
    [InlineData("Production", "", false)]
    [InlineData("Development", "http://localhost:54321", true)]
    public void Supabase_url_must_be_https_outside_development(string environment, string url, bool valid)
    {
        var result = new SupabaseAuthOptionsValidator(new Env(environment)).Validate(null, Options(url));

        Assert.Equal(valid, result.Succeeded);
    }

    // 3. Admin API failures

    [Fact]
    public async Task Admin_api_error_status_becomes_identity_unavailable()
    {
        var source = Source(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<IdentityUnavailableException>(() => source.FindGithubProfileAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Admin_api_network_failure_becomes_identity_unavailable()
    {
        var source = Source(_ => throw new HttpRequestException("refused"));

        await Assert.ThrowsAsync<IdentityUnavailableException>(() => source.FindGithubProfileAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Admin_api_timeout_becomes_identity_unavailable()
    {
        var source = Source(_ => throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<IdentityUnavailableException>(() => source.FindGithubProfileAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_identity_unavailable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var source = Source(_ => throw new TaskCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FindGithubProfileAsync(Guid.NewGuid(), cts.Token));
    }

    private static HttpClient Client(IAuthIdentitySource source)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                services.AddSingleton<IAuthLinkStore>(new MemoryLinks(7001));
                services.RemoveAll<IAuthIdentitySource>();
                services.AddSingleton(source);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    o.ConfigurationManager = null;
                    o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                });
            });
        });
        return factory.CreateClient();
    }

    private static HttpRequestMessage CreateTenant()
    {
        var now = DateTime.UtcNow;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = SupabaseUrl + "/auth/v1",
            Audience = "authenticated",
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString() },
            NotBefore = now.AddHours(-1),
            IssuedAt = now.AddHours(-1),
            Expires = now.AddHours(1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tenants")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = "Example" }), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return request;
    }

    [Fact]
    public async Task Create_company_returns_503_identity_unavailable_when_the_identity_source_is_down()
    {
        var source = new ThrowingSource();
        var factoryClient = Client(source);

        var response = await factoryClient.SendAsync(CreateTenant());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"error":"identity_unavailable"}""", await response.Content.ReadAsStringAsync());
    }

    // 5. Identity without a GitHub login

    [Fact]
    public async Task Create_company_without_a_github_login_is_403_and_not_401()
    {
        using var client = Client(new CountingSource(new GithubProfile(7001, null, null, null)));

        var response = await client.SendAsync(CreateTenant());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("""{"error":"github_login_missing"}""", await response.Content.ReadAsStringAsync());
    }

    // 4. Short, bounded cache for "no GitHub identity"

    [Fact]
    public async Task A_missing_identity_is_looked_up_once_within_the_cache_window()
    {
        var source = new CountingSource(null);
        var resolver = new AuthLinkResolver(new MemoryLinks(), source, new MemoryCache(new MemoryCacheOptions()));
        var user = Guid.NewGuid();

        Assert.Null(await resolver.ResolveGithubUserIdAsync(user, default));
        Assert.Null(await resolver.ResolveGithubUserIdAsync(user, default));

        Assert.Equal(1, source.Calls);
        Assert.True(AuthLinkResolver.MissTtl <= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Remembered_misses_stay_within_the_capacity()
    {
        var resolver = new AuthLinkResolver(new MemoryLinks(), new CountingSource(null), new MemoryCache(new MemoryCacheOptions()));

        for (var i = 0; i < AuthLinkResolver.MissCapacity + 200; i++)
            await resolver.ResolveGithubUserIdAsync(Guid.NewGuid(), default);

        Assert.True(resolver.CachedMissCount <= AuthLinkResolver.MissCapacity);
    }

    [Fact]
    public async Task A_found_identity_is_still_cached()
    {
        var source = new CountingSource(new GithubProfile(42, "login", null, null));
        var resolver = new AuthLinkResolver(new MemoryLinks(), source, new MemoryCache(new MemoryCacheOptions()));
        var user = Guid.NewGuid();

        Assert.Equal(42, await resolver.ResolveGithubUserIdAsync(user, default));
        Assert.Equal(42, await resolver.ResolveGithubUserIdAsync(user, default));

        Assert.Equal(1, source.Calls);
    }
}
