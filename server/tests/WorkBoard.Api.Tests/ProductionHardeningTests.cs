using System.Net;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public void Credential_key_from_configuration_is_validated()
    {
        var values = Good();
        values["GitHub:Credentials:Key"] = WorkBoard.Api.Infrastructure.Credentials.CredentialKeyring.NewKeyFileJson();
        var result = Check(values);
        Assert.Empty(result.Problems);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("GitHub:Credentials"));

        values["GitHub:Credentials:Key"] = """{"current":1,"keys":{"1":"AAAA"}}""";
        Assert.Contains(Check(values).Problems, p => p.Contains("GitHub:Credentials:Key"));
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

    [Theory]
    [InlineData("http_ports", "5080")]
    [InlineData("HTTPS_PORTS", "5443")]
    [InlineData("DOTNET_URLS", "http://0.0.0.0:5080")]
    [InlineData("Kestrel:Endpoints:Web:Url", "http://*:5080")]
    [InlineData("Kestrel:Endpoints:Web:Url", "https://[::]:5443")]
    public void Development_refuses_every_kestrel_listen_source(string key, string value)
    {
        Assert.NotEmpty(Check(new() { [key] = value }, "Development").Problems);
    }

    [Fact]
    public void Development_accepts_loopback_kestrel_endpoint()
    {
        Assert.Empty(Check(new() { ["Kestrel:Endpoints:Web:Url"] = "http://localhost:5080" }, "Development").Problems);
    }

    [Fact]
    public void Production_warns_about_non_loopback_binding()
    {
        var values = Good();
        values["http_ports"] = "5080";

        Assert.Contains(Check(values).Warnings, w => w.Contains("127.0.0.1"));
    }

    [Fact]
    public void Default_passwords_are_matched_without_case_or_padding()
    {
        var values = Good();
        values["ConnectionStrings:App"] = "Host=db;Username=workboard_app;Password=\" WorkBoard_App \"";

        Assert.NotEmpty(Check(values).Problems);
        Assert.NotEmpty(RoleSetup.ValidatePasswords("  WORKBOARD_APP  ", "a-long-random-sync-secret"));
    }

    [Fact]
    public void Skip_setting_from_configuration_is_ignored()
    {
        var values = Good();
        values["ConnectionStrings:App"] = "Host=db;Username=workboard_app;Password=workboard_app";
        values["Hardening:SkipChecks"] = "true";

        Assert.NotEmpty(Check(values).Problems);
    }

    [Fact]
    public void Scram_verifier_has_the_postgres_format_and_a_fresh_salt()
    {
        var a = RoleSetup.ScramVerifier("pencil");
        var b = RoleSetup.ScramVerifier("pencil");

        Assert.Matches(@"^SCRAM-SHA-256\$4096:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Alter_statement_never_contains_the_password()
    {
        const string password = "plain-text-password-123";

        var statement = RoleSetup.AlterPasswordStatement("workboard_app", RoleSetup.ScramVerifier(password));

        Assert.DoesNotContain(password, statement);
        Assert.StartsWith("alter role \"workboard_app\" password 'SCRAM-SHA-256$4096:", statement);
    }

    [DbFact]
    public async Task Role_can_log_in_with_the_password_set_through_a_verifier()
    {
        var role = "wb_scram_" + Guid.NewGuid().ToString("N")[..8];
        var admin = Environment.GetEnvironmentVariable("ConnectionStrings__Migrate")!;
        const string password = "plain-text-password-123";
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync($"create role {role} login");
            try
            {
                await RoleSetup.SetRolePasswordAsync(conn, role, password);
                var login = new NpgsqlConnectionStringBuilder(admin) { Username = role, Password = password, Pooling = false };
                await using var good = new NpgsqlConnection(login.ConnectionString);
                await good.OpenAsync();
                Assert.Equal(role, await good.ExecuteScalarAsync<string>("select current_user"));

                login.Password = "wrong-password-123456";
                await using var bad = new NpgsqlConnection(login.ConnectionString);
                await Assert.ThrowsAsync<PostgresException>(() => bad.OpenAsync());
            }
            finally
            {
                await conn.ExecuteAsync($"drop role {role}");
            }
        }
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
                services.RemoveAll<StartupGuardBypass>();
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

    [DbFact]
    public async Task Cleanup_also_deletes_old_company_creation_keys()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var userId = TestTenant.NextId();
        foreach (var (key, age) in new[] { ("old", "72 hours"), ("fresh", "1 hour") })
            await t.ExecAsync(
                $"insert into tenant_create_keys (github_user_id, key, request_hash, status_code, response, created_at) values (@userId, @key, 'h', 201, '{{}}', now() - interval '{age}')",
                new { userId, key });
        var cleaner = _factory.Services.GetRequiredService<IdempotencyCleaner>();

        await cleaner.DeleteExpiredAsync(CancellationToken.None);

        var left = (await t.QueryAsync("select key from tenant_create_keys where github_user_id = @userId", new { userId }))
            .Select(r => (string)r.key).ToList();
        Assert.Equal(["fresh"], left);
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
            var system = await Assert.ThrowsAsync<PostgresException>(
                () => conn.ExecuteAsync(InsertAudit, new { tid = t.Id, member = (Guid?)null }, tx));
            Assert.Equal("42501", system.SqlState);
        }
        (conn, tx) = await OpenAppAsync(t.Id, mine);
        await using (conn)
        {
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
