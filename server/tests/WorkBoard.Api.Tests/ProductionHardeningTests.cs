using System.Net;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using WorkBoard.Api.Features.Members;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.Data.Migrations;
using WorkBoard.Api.Infrastructure.Hardening;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Maintenance;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class ProductionHardeningTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ProductionHardeningTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static Dictionary<string, string?> Good() => new()
    {
        ["ConnectionStrings:App"] = "Host=db;Database=workboard;Username=workboard_app;Password=a-long-random-app-secret",
        ["ConnectionStrings:Sync"] = "Host=db;Database=workboard;Username=workboard_sync;Password=a-long-random-sync-secret",
        ["Web:BaseUrl"] = "https://board.example.com",
        ["Web:AllowedOrigins:0"] = "https://board.example.com",
    };

    private static GuardResult Check(Dictionary<string, string?> values, string env = "Production") =>
        StartupGuard.Check(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new Env(env));

    [Fact]
    public void Complete_production_settings_pass()
    {
        Assert.Empty(Check(Good()).Problems);
    }

    [Theory]
    [InlineData("ConnectionStrings:App", "Host=db;Username=workboard_app;Password=workboard_app")]
    [InlineData("ConnectionStrings:Sync", "Host=db;Username=workboard_sync;Password=workboard_sync")]
    [InlineData("ConnectionStrings:Sync", "Host=db;Username=workboard_sync")]
    [InlineData("Web:BaseUrl", "")]
    [InlineData("Web:BaseUrl", "http://board.example.com")]
    [InlineData("Web:AllowedOrigins:0", "board.example.com")]
    [InlineData("GitHub:App:Slug", "only-a-slug")]
    [InlineData("GitHub:Credentials:KeyFile", "/nonexistent/keys.json")]
    public void Bad_production_settings_are_rejected(string key, string value)
    {
        var values = Good();
        values[key] = value;

        Assert.NotEmpty(Check(values).Problems);
    }

    [Fact]
    public void Missing_origins_are_rejected()
    {
        var values = Good();
        values.Remove("Web:AllowedOrigins:0");

        Assert.Contains(Check(values).Problems, p => p.Contains("Web:AllowedOrigins"));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("http://*:5080")]
    [InlineData("http://localhost:5080;http://+:5081")]
    [InlineData("https://api.example.com")]
    public void Development_must_not_listen_beyond_localhost(string urls)
    {
        Assert.NotEmpty(Check(new() { ["urls"] = urls }, "Development").Problems);
    }

    [Theory]
    [InlineData("http://localhost:5080")]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://[::1]:5080")]
    [InlineData("")]
    public void Development_on_localhost_is_accepted(string urls)
    {
        Assert.Empty(Check(new() { ["urls"] = urls }, "Development").Problems);
    }

    [Fact]
    public void Role_passwords_must_be_set_long_and_distinct()
    {
        Assert.Empty(RoleSetup.ValidatePasswords("a-long-random-app-secret", "a-long-random-sync-secret"));
        Assert.NotEmpty(RoleSetup.ValidatePasswords(null, "a-long-random-sync-secret"));
        Assert.NotEmpty(RoleSetup.ValidatePasswords("short", "a-long-random-sync-secret"));
        Assert.NotEmpty(RoleSetup.ValidatePasswords("workboard_app", "a-long-random-sync-secret"));
        Assert.NotEmpty(RoleSetup.ValidatePasswords("same-long-password-1", "same-long-password-1"));
    }

    private sealed class AnyMember : IMemberLookup
    {
        public Task<long?> FindUserIdByLoginAsync(string githubLogin, CancellationToken ct) => Task.FromResult<long?>(5001);

        public Task<IReadOnlyList<DevIdentity>> FindByUserIdAsync(long githubUserId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DevIdentity>>(
                [new DevIdentity(new Member(Guid.NewGuid(), 5001, "dev", null, null, "admin"), Guid.NewGuid(), "t")]);

        public Task<IReadOnlyList<DevUser>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DevUser>>([]);
    }

    private static WebApplicationFactory<Program> Host(string environment, Dictionary<string, string?> config) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAppConnectionFactory>(new RecordingConnectionFactory());
                services.AddSingleton<IMemberLookup>(new AnyMember());
            });
        });

    private static Dictionary<string, string?> Supabase(params (string, string?)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Auth:Supabase:Url"] = "https://test-project.supabase.example",
            ["Auth:Supabase:ServiceRoleKey"] = "test-service-role-key",
            ["Auth:Supabase:JwtSecret"] = "",
            [StartupGuard.SkipKey] = "false",
        };
        foreach (var (k, v) in extra)
            values[k] = v;
        return values;
    }

    [Fact]
    public void Production_host_with_default_passwords_and_no_web_url_does_not_start()
    {
        using var host = Host("Production", Supabase());

        var ex = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("Refusing to start", ex.ToString());
        Assert.Contains("Web:BaseUrl", ex.ToString());
    }

    [Fact]
    public async Task Production_host_with_complete_settings_starts()
    {
        var values = Supabase();
        foreach (var (k, v) in Good())
            values[k] = v;
        using var host = Host("Production", values);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Development_host_bound_beyond_localhost_does_not_start()
    {
        using var host = Host("Development", Supabase(("urls", "http://0.0.0.0:5080")));

        var ex = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("X-Dev-User", ex.ToString());
    }

    [Fact]
    public async Task Dev_sign_in_is_refused_for_requests_through_a_cloudflare_tunnel()
    {
        using var host = Host("Development", Supabase());
        using var client = host.CreateClient();

        var direct = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        direct.Headers.Add("X-Dev-User", "dev");
        var tunneled = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        tunneled.Headers.Add("X-Dev-User", "dev");
        tunneled.Headers.Add("CF-Connecting-IP", "203.0.113.9");

        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(direct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(tunneled)).StatusCode);
    }

    [DbFact]
    public async Task Cleanup_deletes_keys_older_than_the_retention_and_keeps_the_rest()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        foreach (var (key, age) in new[] { ("old", "72 hours"), ("recent", "30 hours"), ("fresh", "1 hour") })
            await t.ExecAsync(
                $"insert into idempotency_keys (tenant_id, key, request_hash, status_code, created_at) values (@tid, @key, 'h', 200, now() - interval '{age}')",
                new { tid = t.Id, key });
        var cleaner = _factory.Services.GetRequiredService<IdempotencyCleaner>();

        var deleted = await cleaner.DeleteExpiredAsync(CancellationToken.None);

        Assert.True(deleted >= 1);
        var left = (await t.QueryAsync("select key from idempotency_keys where tenant_id = @tid", new { tid = t.Id }))
            .Select(r => (string)r.key).Order().ToList();
        Assert.Equal(["fresh", "recent"], left);
    }

    [Fact]
    public void Retention_is_never_shorter_than_the_key_lifetime()
    {
        var options = new MaintenanceOptions { IdempotencyRetentionHours = 1 };

        Assert.Equal(TimeSpan.FromHours(24), options.IdempotencyRetention);
    }

    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenAppAsync(Guid tenant, Guid? member)
    {
        var source = _factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.App);
        var conn = await source.OpenConnectionAsync();
        var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            "select set_config('app.tenant_id', @tenant, true), set_config('app.member_id', @member, true)",
            new { tenant = tenant.ToString(), member = member?.ToString() ?? "" }, tx);
        return (conn, tx);
    }

    private const string InsertAudit =
        "insert into audit_log (tenant_id, member_id, action, target_type) values (@tid, @member, 'test', 'x')";

    [DbFact]
    public async Task App_role_audit_rows_carry_only_the_current_member_or_none()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var mine = await t.ScalarAsync<Guid>("select id from members where tenant_id = @tid limit 1", new { tid = t.Id });
        await t.AddMemberAsync("other");
        var other = await t.ScalarAsync<Guid>("select id from members where tenant_id = @tid and id <> @mine limit 1", new { tid = t.Id, mine });

        var (conn, tx) = await OpenAppAsync(t.Id, mine);
        await using (conn)
        {
            await conn.ExecuteAsync(InsertAudit, new { tid = t.Id, member = (Guid?)mine }, tx);
            await conn.ExecuteAsync(InsertAudit, new { tid = t.Id, member = (Guid?)null }, tx);
            var forged = await Assert.ThrowsAsync<PostgresException>(
                () => conn.ExecuteAsync(InsertAudit, new { tid = t.Id, member = (Guid?)other }, tx));
            Assert.Equal("42501", forged.SqlState);
        }

        var (conn2, tx2) = await OpenAppAsync(t.Id, null);
        await using (conn2)
        {
            var noMember = await Assert.ThrowsAsync<PostgresException>(
                () => conn2.ExecuteAsync(InsertAudit, new { tid = t.Id, member = (Guid?)mine }, tx2));
            Assert.Equal("42501", noMember.SqlState);
        }
    }

    [DbFact]
    public async Task Sync_role_may_still_write_audit_rows_for_any_member()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var member = await t.ScalarAsync<Guid>("select id from members where tenant_id = @tid limit 1", new { tid = t.Id });

        await t.ExecAsync(InsertAudit, new { tid = t.Id, member });

        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from audit_log where tenant_id = @tid", new { tid = t.Id }));
    }
}
