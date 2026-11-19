using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WorkBoard.Api.Infrastructure.Credentials;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class CredentialStoreTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Token = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";

    private sealed class StaticKeys(CredentialKeyring ring) : ICredentialKeys
    {
        public CredentialKeyring Ring { get; set; } = ring;
        public CredentialKeyring Get() => Ring;
    }

    private static byte[] NewKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(CredentialKeyring.KeySize);

    private static CredentialKeyring Ring(int current, params int[] versions) =>
        new(current, versions.ToDictionary(v => v, _ => NewKey()));

    private readonly WebApplicationFactory<Program> _factory;

    public CredentialStoreTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private NpgsqlDataSource Sync => _factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.Sync);

    private CredentialStore Store(StaticKeys keys) => new(Sync, new CredentialCipher(keys));

    private static async Task<Guid> MemberIdAsync(TestTenant t) =>
        await t.ScalarAsync<Guid>("select id from members where tenant_id = @tid limit 1", new { tid = t.Id });

    // ---- cipher ------------------------------------------------------------------------------

    [Fact]
    public void Seal_and_open_round_trip()
    {
        var cipher = new CredentialCipher(new StaticKeys(Ring(1, 1)));
        var (tenant, connection) = (Guid.NewGuid(), Guid.NewGuid());

        var sealedToken = cipher.Seal(Token, tenant, connection);

        Assert.Equal(Token, cipher.Open(sealedToken, tenant, connection));
        Assert.DoesNotContain(Token, System.Text.Encoding.UTF8.GetString(sealedToken.Ciphertext));
        Assert.NotEqual(sealedToken.Nonce, cipher.Seal(Token, tenant, connection).Nonce);
    }

    [Fact]
    public void Open_fails_when_the_tenant_or_connection_differs_from_the_sealed_one()
    {
        var cipher = new CredentialCipher(new StaticKeys(Ring(1, 1)));
        var (tenant, connection) = (Guid.NewGuid(), Guid.NewGuid());
        var sealedToken = cipher.Seal(Token, tenant, connection);

        Assert.Throws<CredentialException>(() => cipher.Open(sealedToken, Guid.NewGuid(), connection));
        Assert.Throws<CredentialException>(() => cipher.Open(sealedToken, tenant, Guid.NewGuid()));
    }

    [Fact]
    public void Open_fails_for_tampered_ciphertext()
    {
        var cipher = new CredentialCipher(new StaticKeys(Ring(1, 1)));
        var (tenant, connection) = (Guid.NewGuid(), Guid.NewGuid());
        var sealedToken = cipher.Seal(Token, tenant, connection);
        sealedToken.Ciphertext[0] ^= 1;

        Assert.Throws<CredentialException>(() => cipher.Open(sealedToken, tenant, connection));
    }

    [Fact]
    public void Old_key_versions_still_open_and_new_rows_use_the_current_version()
    {
        var keys1 = new Dictionary<int, byte[]> { [1] = NewKey() };
        var keys12 = new Dictionary<int, byte[]> { [1] = keys1[1], [2] = NewKey() };
        var (tenant, connection) = (Guid.NewGuid(), Guid.NewGuid());
        var keys = new StaticKeys(new CredentialKeyring(1, keys1));
        var cipher = new CredentialCipher(keys);
        var old = cipher.Seal(Token, tenant, connection);

        keys.Ring = new CredentialKeyring(2, keys12);

        Assert.Equal(1, old.KeyVersion);
        Assert.Equal(Token, cipher.Open(old, tenant, connection));
        Assert.Equal(2, cipher.Seal(Token, tenant, connection).KeyVersion);

        keys.Ring = new CredentialKeyring(2, new Dictionary<int, byte[]> { [2] = keys12[2] });
        Assert.Throws<CredentialKeyException>(() => cipher.Open(old, tenant, connection));
    }

    [Fact]
    public void Key_file_is_parsed_and_must_be_private()
    {
        var json = CredentialKeyring.NewKeyFileJson(3);
        var ring = CredentialKeyring.Parse(json);
        Assert.Equal(3, ring.CurrentVersion);
        Assert.Equal(CredentialKeyring.KeySize, ring.Get(3).Length);

        Assert.Throws<CredentialKeyException>(() => CredentialKeyring.Parse("""{"current":2,"keys":{"1":"AAAA"}}"""));
        Assert.Throws<CredentialKeyException>(() => CredentialKeyring.Parse("not json"));

        if (OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(Path.GetTempPath(), "wb-key-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, json);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(3, CredentialKeyring.Load(path).CurrentVersion);

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<CredentialKeyException>(() => CredentialKeyring.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Key_can_come_from_configuration_and_the_file_wins()
    {
        var env = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = Path.GetTempPath() };
        CredentialKeyring Ring(CredentialOptions o) => new FileCredentialKeys(Microsoft.Extensions.Options.Options.Create(o), env).Get();

        Assert.Equal(4, Ring(new CredentialOptions { Key = CredentialKeyring.NewKeyFileJson(4) }).CurrentVersion);
        Assert.Throws<CredentialKeyException>(() => Ring(new CredentialOptions()));

        if (OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(Path.GetTempPath(), "wb-key-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, CredentialKeyring.NewKeyFileJson(2));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(2, Ring(new CredentialOptions { KeyFile = path, Key = CredentialKeyring.NewKeyFileJson(4) }).CurrentVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- masking -----------------------------------------------------------------------------

    [Theory]
    [InlineData("ghp_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("gho_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("ghu_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("ghs_0123456789abcdefghijklmnopqrstuvwxyz")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijkl_0123456789abcdefghijklmnopqrstuvwxyz")]
    public void Masker_replaces_every_token_shape(string token)
    {
        var masked = TokenMasker.Apply($"failed with Authorization: token {token}, retry \"{token}\"");

        Assert.DoesNotContain(token, masked);
        Assert.Equal(2, masked!.Split(TokenMasker.Mask).Length - 1);
    }

    [Fact]
    public void Masker_leaves_ordinary_text_alone()
    {
        Assert.Equal("Unauthorized: no ghp_ here", TokenMasker.Apply("Unauthorized: no ghp_ here"));
        Assert.Null(TokenMasker.Apply(null));
    }

    [DbFact]
    public async Task Audit_rows_never_contain_a_token()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var member = new Features.Members.Member(await MemberIdAsync(t), 1, "m", null, null, "member");
        var user = new FixedUser(new DevIdentity(member, t.Id, "t"));
        var db = new Infrastructure.Data.TenantDb(user, _factory.Services.GetRequiredService<Infrastructure.Data.IAppConnectionFactory>());
        var log = new Infrastructure.Writes.AuditLog(user);

        await db.WriteAsync(async s =>
        {
            await log.WriteAsync(s, "test", "x", null, new { note = "was " + Token }, new { note = "now " + Token });
            return 0;
        }, default);

        var after = await t.ScalarAsync<string>("select after::text from audit_log where tenant_id = @tid", new { tid = t.Id });
        Assert.DoesNotContain(Token, after);
        Assert.Contains(TokenMasker.Mask, after);
    }

    private sealed class FixedUser(DevIdentity identity) : ICurrentUser
    {
        public DevIdentity Identity { get; } = identity;
    }

    // ---- store -------------------------------------------------------------------------------

    [DbFact]
    public async Task Save_open_replace_and_delete()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var store = Store(new StaticKeys(Ring(1, 1)));
        var member = await MemberIdAsync(t);

        await store.SaveAsync(t.Id, t.ConnectionId, member, Token, ["repo", "read:org"], null);
        Assert.Equal(Token, await store.OpenAsync(t.Id, t.ConnectionId));
        Assert.NotNull(await t.ScalarAsync<DateTime?>("select last_used_at from github_credentials where connection_id = @c", new { c = t.ConnectionId }));

        await store.SaveAsync(t.Id, t.ConnectionId, member, "ghp_replacement0123456789abcdef", [], null);
        Assert.Equal("ghp_replacement0123456789abcdef", await store.OpenAsync(t.Id, t.ConnectionId));
        Assert.Equal(1, await t.ScalarAsync<long>("select count(*) from github_credentials where connection_id = @c", new { c = t.ConnectionId }));

        Assert.True(await store.DeleteAsync(t.Id, t.ConnectionId));
        Assert.Null(await store.OpenAsync(t.Id, t.ConnectionId));
    }

    [DbFact]
    public async Task Inactive_or_expired_credentials_do_not_open()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var store = Store(new StaticKeys(Ring(1, 1)));
        var member = await MemberIdAsync(t);

        await store.SaveAsync(t.Id, t.ConnectionId, member, Token, [], DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Null(await store.OpenAsync(t.Id, t.ConnectionId));

        await store.SaveAsync(t.Id, t.ConnectionId, member, Token, [], null);
        await store.SetStatusAsync(t.Id, t.ConnectionId, "invalid");
        Assert.Null(await store.OpenAsync(t.Id, t.ConnectionId));
    }

    [DbFact]
    public async Task Another_tenant_cannot_open_or_replace_a_credential()
    {
        await using var mine = await TestTenant.CreateAsync(_factory);
        await using var other = await TestTenant.CreateAsync(_factory);
        var store = Store(new StaticKeys(Ring(1, 1)));
        await store.SaveAsync(mine.Id, mine.ConnectionId, await MemberIdAsync(mine), Token, [], null);

        Assert.Null(await store.OpenAsync(other.Id, mine.ConnectionId));
        Assert.False(await store.DeleteAsync(other.Id, mine.ConnectionId));
        var otherMember = await MemberIdAsync(other);
        await store.SaveAsync(other.Id, mine.ConnectionId, otherMember, "ghp_other0123456789abcdefghij", [], null);
        Assert.Equal(Token, await store.OpenAsync(mine.Id, mine.ConnectionId));

        await using var conn = await _factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.App).OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("select set_config('app.tenant_id', @tid, true)", new { tid = other.Id.ToString() }, tx);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>(
            "select count(*) from github_credentials where connection_id = @c", new { c = mine.ConnectionId }, tx));
    }

    [DbFact]
    public async Task A_row_moved_to_another_connection_does_not_open()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var store = Store(new StaticKeys(Ring(1, 1)));
        var member = await MemberIdAsync(t);
        var second = Guid.NewGuid();
        await t.ExecAsync(
            "insert into github_connections (id, tenant_id, account_id, account_login, account_type) values (@id, @tid, @acc, @login, 'user')",
            new { id = second, tid = t.Id, acc = TestTenant.NextId(), login = "u-" + t.Suffix });
        await store.SaveAsync(t.Id, t.ConnectionId, member, Token, [], null);
        await store.SaveAsync(t.Id, second, member, "ghp_second0123456789abcdefghij", [], null);

        await t.ExecAsync("""
            update github_credentials s set ciphertext = f.ciphertext, nonce = f.nonce
              from github_credentials f where s.connection_id = @second and f.connection_id = @first
            """, new { first = t.ConnectionId, second });

        await Assert.ThrowsAsync<CredentialException>(() => store.OpenAsync(t.Id, second));
    }

    [DbFact]
    public async Task Reencrypt_moves_rows_to_the_current_key_version()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var key1 = NewKey();
        var keys = new StaticKeys(new CredentialKeyring(1, new Dictionary<int, byte[]> { [1] = key1 }));
        var store = Store(keys);
        await store.SaveAsync(t.Id, t.ConnectionId, await MemberIdAsync(t), Token, [], null);

        keys.Ring = new CredentialKeyring(2, new Dictionary<int, byte[]> { [1] = key1, [2] = NewKey() });
        Assert.Equal(Token, await store.OpenAsync(t.Id, t.ConnectionId));
        Assert.Equal(1, await t.ScalarAsync<int>("select key_version from github_credentials where connection_id = @c", new { c = t.ConnectionId }));

        Assert.True((await store.ReencryptAsync()).Moved >= 1);

        Assert.Equal(2, await t.ScalarAsync<int>("select key_version from github_credentials where connection_id = @c", new { c = t.ConnectionId }));
        keys.Ring = new CredentialKeyring(2, new Dictionary<int, byte[]> { [2] = keys.Ring.Get(2) });
        Assert.Equal(Token, await store.OpenAsync(t.Id, t.ConnectionId));
    }

    [DbFact]
    public async Task Reencrypt_skips_a_row_it_cannot_open_and_still_moves_the_others()
    {
        await using var good = await TestTenant.CreateAsync(_factory);
        await using var bad = await TestTenant.CreateAsync(_factory);
        var key1 = NewKey();
        var keys = new StaticKeys(new CredentialKeyring(1, new Dictionary<int, byte[]> { [1] = key1 }));
        var store = Store(keys);
        await store.SaveAsync(good.Id, good.ConnectionId, await MemberIdAsync(good), Token, [], null);
        await store.SaveAsync(bad.Id, bad.ConnectionId, await MemberIdAsync(bad), Token, [], null);
        await bad.ExecAsync("update github_credentials set ciphertext = '\\x00', nonce = '\\x000000000000000000000000' where connection_id = @c",
            new { c = bad.ConnectionId });
        keys.Ring = new CredentialKeyring(2, new Dictionary<int, byte[]> { [1] = key1, [2] = NewKey() });

        var result = await store.ReencryptAsync();

        Assert.True(result.Moved >= 1);
        Assert.True(result.Skipped >= 1);
        Assert.Equal(2, await good.ScalarAsync<int>("select key_version from github_credentials where connection_id = @c", new { c = good.ConnectionId }));
        Assert.Equal(1, await bad.ScalarAsync<int>("select key_version from github_credentials where connection_id = @c", new { c = bad.ConnectionId }));
        Assert.Equal(Token, await store.OpenAsync(good.Id, good.ConnectionId));
    }

    [DbFact]
    public async Task Stored_bytes_do_not_contain_the_token()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        await Store(new StaticKeys(Ring(1, 1))).SaveAsync(t.Id, t.ConnectionId, await MemberIdAsync(t), Token, [], null);

        var raw = await t.ScalarAsync<byte[]>("select ciphertext from github_credentials where connection_id = @c", new { c = t.ConnectionId });

        Assert.DoesNotContain(Token, System.Text.Encoding.UTF8.GetString(raw));
    }

    [DbFact]
    public async Task App_role_cannot_select_ciphertext_or_nonce_but_reads_metadata()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        await Store(new StaticKeys(Ring(1, 1))).SaveAsync(t.Id, t.ConnectionId, await MemberIdAsync(t), Token, ["repo"], null);

        async Task<(NpgsqlConnection, NpgsqlTransaction)> Open()
        {
            var conn = await _factory.Services.GetRequiredKeyedService<NpgsqlDataSource>(DataKeys.App).OpenConnectionAsync();
            var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("select set_config('app.tenant_id', @tid, true)", new { tid = t.Id.ToString() }, tx);
            return (conn, tx);
        }

        foreach (var sql in new[]
                 {
                     "select ciphertext from github_credentials",
                     "select nonce from github_credentials",
                     "select * from github_credentials",
                     "insert into github_credentials (tenant_id, connection_id, owner_member_id, ciphertext, nonce, key_version) values (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), '\\x00', '\\x000000000000000000000000', 1)",
                     "update github_credentials set status = 'revoked'",
                     "delete from github_credentials",
                 })
        {
            var (conn, tx) = await Open();
            await using var _ = conn;
            var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, transaction: tx));
            Assert.Equal("42501", ex.SqlState);
        }

        var (c2, tx2) = await Open();
        await using var __ = c2;
        var status = await c2.ExecuteScalarAsync<string>(
            "select status from github_credentials where connection_id = @c", new { c = t.ConnectionId }, tx2);
        Assert.Equal("active", status);
    }

    // ---- credentials resolver ------------------------------------------------------------------

    private sealed class FakeInstallationTokens : IGitHubInstallationTokens
    {
        public Task<string> GetAsync(long installationId, CancellationToken ct) => Task.FromResult("ghs_install" + installationId);
        public void Invalidate(long installationId) { }
    }

    [DbFact]
    public async Task Credentials_resolve_installation_token_for_app_and_stored_token_for_token_connections()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var store = Store(new StaticKeys(Ring(1, 1)));
        var credentials = new GitHubCredentials(new FakeInstallationTokens(), store);
        await store.SaveAsync(t.Id, t.ConnectionId, await MemberIdAsync(t), Token, [], null);

        var app = await credentials.GetAsync(new GitHubConnectionRef(t.Id, t.ConnectionId, GitHubAuthKind.App, 7), default);
        var token = await credentials.GetAsync(new GitHubConnectionRef(t.Id, t.ConnectionId, GitHubAuthKind.Token, null), default);

        Assert.Equal(("ghs_install7", GitHubAuthKind.App), (app.Token, app.Kind));
        Assert.Equal((Token, GitHubAuthKind.Token), (token.Token, token.Kind));

        var missing = await Assert.ThrowsAsync<GitHubSourceException>(() =>
            credentials.GetAsync(new GitHubConnectionRef(t.Id, Guid.NewGuid(), GitHubAuthKind.Token, null), default));
        Assert.Equal(GitHubSourceErrorKind.Unauthorized, missing.Kind);
    }

    // ---- store_bodies --------------------------------------------------------------------------

    private MirrorWriter Writer() => new(Sync);

    private static SourceUser User(long id) => new(id, "u" + id, null, null);

    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static SourceIssue Issue(long id, int number) =>
        new(id, "node" + id, number, "title", "secret issue body", null, "open", null, User(1), T0, null, T0,
            "https://x/" + number, null, [], [], null, null);

    private static SourceComment Comment(long id, int issueNumber) =>
        new(id, issueNumber, User(3), "secret comment body", T0, T0, null);

    [DbFact]
    public async Task Sync_stores_bodies_by_default_and_not_when_store_bodies_is_off()
    {
        await using var on = await TestTenant.CreateAsync(_factory);
        await using var off = await TestTenant.CreateAsync(_factory);
        await off.ExecAsync("update github_connections set auth_kind = 'token', store_bodies = false where id = @c", new { c = off.ConnectionId });

        foreach (var t in new[] { on, off })
        {
            var (issue, comment) = (TestTenant.NextId(), TestTenant.NextId());
            var snapshot = new RepositorySnapshot(
                new SourceRepository(TestTenant.NextId(), "api", $"org-{t.Suffix}/api", null, false),
                [], [], [Issue(issue, 1)], [], [Comment(comment, 1)], null);
            await Writer().ApplyAsync(t.Id, t.ConnectionId, snapshot, default);
        }

        Assert.Equal("secret issue body", await on.ScalarAsync<string>("select body from issues where tenant_id = @tid", new { tid = on.Id }));
        Assert.Equal("secret comment body", await on.ScalarAsync<string>("select body from comments where tenant_id = @tid", new { tid = on.Id }));
        Assert.Null(await off.ScalarAsync<string?>("select body from issues where tenant_id = @tid", new { tid = off.Id }));
        Assert.Equal("", await off.ScalarAsync<string>("select body from comments where tenant_id = @tid", new { tid = off.Id }));
    }

    [DbFact]
    public async Task Written_issue_and_comment_bodies_are_not_stored_when_store_bodies_is_off()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        await t.ExecAsync("update github_connections set auth_kind = 'token', store_bodies = false where id = @c", new { c = t.ConnectionId });
        var repo = await t.AddRepoAsync("api");
        var issueId = TestTenant.NextId();

        await Writer().ApplyWrittenIssueAsync(t.Id, repo, Issue(issueId, 5), null, null, default);
        await Writer().ApplyWrittenCommentAsync(t.Id, repo, Comment(TestTenant.NextId(), 5), default);

        Assert.Null(await t.ScalarAsync<string?>("select body from issues where tenant_id = @tid and github_issue_id = @issueId", new { tid = t.Id, issueId }));
        Assert.Equal("", await t.ScalarAsync<string>("select body from comments where tenant_id = @tid", new { tid = t.Id }));
    }

    [DbFact]
    public async Task Turning_store_bodies_off_clears_a_stored_body_on_the_next_update()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var repo = await t.AddRepoAsync("api");
        var issueId = TestTenant.NextId();
        await Writer().ApplyWrittenIssueAsync(t.Id, repo, Issue(issueId, 5), null, null, default);
        await t.ExecAsync("update github_connections set store_bodies = false where id = @c", new { c = t.ConnectionId });

        await Writer().ApplyWrittenIssueAsync(t.Id, repo, Issue(issueId, 5) with { UpdatedAt = T0.AddMinutes(1) }, null, null, default);

        Assert.Null(await t.ScalarAsync<string?>("select body from issues where tenant_id = @tid and github_issue_id = @issueId", new { tid = t.Id, issueId }));
    }
}
