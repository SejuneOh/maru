using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkBoard.Api.Features.Members;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Tests.Fakes;

namespace WorkBoard.Api.Tests;

public class InvitationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeUsers : IGitHubUserLookup
    {
        public Dictionary<string, GitHubUser> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? Throw { get; set; }
        public int Calls;

        public Task<GitHubUser?> FindByLoginAsync(string login, long? installationId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Throw is not null)
                return Task.FromException<GitHubUser?>(Throw);
            return Task.FromResult(Users.TryGetValue(login, out var u) ? u : null);
        }
    }

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeUsers _github = new();

    public InvitationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient Client(string login) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IGitHubUserLookup>();
            s.AddSingleton<IGitHubUserLookup>(_github);
        })).CreateClient().WithDevUser(login);

    private static async Task<string?> ErrorAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private static async Task<string> AdminAsync(TestTenant t, string name = "admin")
    {
        var (login, userId) = await t.AddMemberAsync(name);
        await t.ExecAsync("update members set role = 'admin' where tenant_id = @tid and github_user_id = @userId", new { tid = t.Id, userId });
        return login;
    }

    // A GitHub person GitHub knows about but who is in no company yet.
    private (string Login, long Id) Outsider()
    {
        var id = TestTenant.NextId();
        var login = "new-" + id;
        _github.Users[login] = new GitHubUser(id, login);
        return (login, id);
    }

    private static Task<HttpResponseMessage> InviteAsync(HttpClient c, string login, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/invitations") { Content = JsonContent.Create(new { githubLogin = login }) };
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return c.SendAsync(request);
    }

    private static async Task<(Guid Id, string Token)> InvitedAsync(HttpClient admin, string login)
    {
        var response = await InviteAsync(admin, login);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("invitation").GetProperty("id").GetGuid(), body.GetProperty("token").GetString()!);
    }

    private static Task<HttpResponseMessage> AcceptAsync(HttpClient c, string token) =>
        c.PostAsJsonAsync("/api/invitations/accept", new { token });

    private static Task<HttpResponseMessage> PreviewAsync(HttpClient c, string token) =>
        c.PostAsJsonAsync("/api/invitations/preview", new { token });

    [DbFact]
    public async Task Non_admin_cannot_invite_list_revoke_or_remove()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var (invitee, _) = Outsider();
        var (invitationId, _) = await InvitedAsync(admin, invitee);
        var (_, otherId) = await t.AddMemberAsync("other");
        var otherMemberId = await t.ScalarAsync<Guid>("select id from members where github_user_id = @otherId", new { otherId });
        var member = Client(t.ViewerLogin);
        var callsBefore = _github.Calls;

        var invite = await InviteAsync(member, Outsider().Login);
        var list = await member.GetAsync("/api/invitations");
        var revoke = await member.DeleteAsync($"/api/invitations/{invitationId}");
        var remove = await member.DeleteAsync($"/api/members/{otherMemberId}");

        foreach (var r in new[] { invite, list, revoke, remove })
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("admin_only", await ErrorAsync(r));
        }
        Assert.Equal(callsBefore, _github.Calls);
        Assert.Null(await t.ScalarAsync<DateTime?>("select revoked_at from invitations where id = @invitationId", new { invitationId }));
        Assert.Null(await t.ScalarAsync<DateTime?>("select left_at from members where id = @otherMemberId", new { otherMemberId }));
    }

    [DbFact]
    public async Task Invite_stores_the_github_id_and_only_a_hash_of_the_token()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var (login, githubId) = Outsider();

        var response = await InviteAsync(admin, "@" + login.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        Assert.Equal(43, token.Length);
        var invitation = body.GetProperty("invitation");
        Assert.Equal(githubId, invitation.GetProperty("githubUserId").GetInt64());
        Assert.Equal(login, invitation.GetProperty("githubLogin").GetString());
        Assert.Equal("member", invitation.GetProperty("role").GetString());
        var id = invitation.GetProperty("id").GetGuid();
        var stored = await t.ScalarAsync<byte[]>("select token_hash from invitations where id = @id", new { id });
        Assert.Equal(Invitations.HashToken(token), stored);
        Assert.Equal(0, await t.ScalarAsync<int>(
            "select count(*)::int from invitations where id = @id and position(convert_to(@token, 'UTF8') in token_hash) > 0", new { id, token }));
        var expires = invitation.GetProperty("expiresAt").GetDateTime();
        Assert.InRange(expires, DateTime.UtcNow.AddDays(7).AddMinutes(-5), DateTime.UtcNow.AddDays(7).AddMinutes(5));
        Assert.Equal(1, await t.ScalarAsync<int>(
            "select count(*)::int from audit_log where tenant_id = @tid and action = 'invitation.create' and target_id = @id", new { tid = t.Id, id }));

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/invitations");
        Assert.Equal(id, Assert.Single(list.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [DbFact]
    public async Task Invite_errors()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var viewerId = await t.ScalarAsync<long>("select github_user_id from members where github_login = @l", new { l = t.ViewerLogin });
        _github.Users[t.ViewerLogin] = new GitHubUser(viewerId, t.ViewerLogin);

        Assert.Equal("invalid_login", await ErrorAsync(await InviteAsync(admin, "-bad-")));
        Assert.Equal("invalid_login", await ErrorAsync(await InviteAsync(admin, "")));
        Assert.Equal("github_user_not_found", await ErrorAsync(await InviteAsync(admin, "nobody-" + TestTenant.NextId())));
        var already = await InviteAsync(admin, t.ViewerLogin);
        Assert.Equal(HttpStatusCode.Conflict, already.StatusCode);
        Assert.Equal("already_member", await ErrorAsync(already));

        var (login, _) = Outsider();
        _github.Throw = new GitHubSourceException(GitHubSourceErrorKind.RateLimited, "limit");
        var limited = await InviteAsync(admin, login);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("github_rate_limited", await ErrorAsync(limited));
        _github.Throw = new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "down");
        Assert.Equal(HttpStatusCode.BadGateway, (await InviteAsync(admin, login)).StatusCode);
        _github.Throw = null;
        Assert.Equal(0, await t.ScalarAsync<int>("select count(*)::int from invitations where tenant_id = @tid", new { tid = t.Id }));
    }

    [DbFact]
    public async Task Invite_replay_with_the_same_key_does_not_return_the_token_again()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var (login, _) = Outsider();

        var first = await InviteAsync(admin, login, "k-" + login);
        var replay = await InviteAsync(admin, login, "k-" + login);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<JsonElement>();
        var b = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(a.GetProperty("token").GetString());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("token").ValueKind);
        Assert.Equal(a.GetProperty("invitation").GetProperty("id").GetGuid(), b.GetProperty("invitation").GetProperty("id").GetGuid());
        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from invitations where tenant_id = @tid", new { tid = t.Id }));
        Assert.Equal(0, await t.ScalarAsync<int>(
            "select count(*)::int from idempotency_keys where tenant_id = @tid and response::text like '%' || @token || '%'",
            new { tid = t.Id, token = a.GetProperty("token").GetString() }));
    }

    [DbFact]
    public async Task Invitee_previews_and_joins_and_a_repeat_accept_is_the_same_200()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var (login, githubId) = Outsider();
        var (invitationId, token) = await InvitedAsync(admin, login);
        var invitee = Client(login);

        Assert.Equal(HttpStatusCode.Unauthorized, (await invitee.GetAsync("/api/me")).StatusCode);
        var preview = await PreviewAsync(invitee, token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var p = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org-" + t.Suffix, p.GetProperty("tenant").GetProperty("name").GetString());
        Assert.False(p.GetProperty("alreadyMember").GetBoolean());

        var accept = await AcceptAsync(invitee, token);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var joined = await accept.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(t.Id, joined.GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal("member", joined.GetProperty("member").GetProperty("role").GetString());
        Assert.Equal(githubId, joined.GetProperty("member").GetProperty("githubUserId").GetInt64());

        var me = await invitee.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(t.Id, me.GetProperty("tenant").GetProperty("id").GetGuid());
        var again = await AcceptAsync(invitee, token);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.NotNull(await t.ScalarAsync<DateTime?>("select accepted_at from invitations where id = @invitationId", new { invitationId }));
        Assert.Equal(1, await t.ScalarAsync<int>(
            "select count(*)::int from audit_log where tenant_id = @tid and action = 'member.join'", new { tid = t.Id }));
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/invitations")).EnumerateArray());
    }

    [DbFact]
    public async Task A_leaked_link_does_not_let_another_account_join_or_see_the_company()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        await using var other = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var (login, _) = Outsider();
        var (_, token) = await InvitedAsync(admin, login);
        var stranger = Client(other.ViewerLogin);

        var preview = await PreviewAsync(stranger, token);
        var accept = await AcceptAsync(stranger, token);

        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Equal("""{"error":"invite_other_account"}""", await preview.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, accept.StatusCode);
        Assert.Equal("invite_other_account", await ErrorAsync(accept));
        Assert.Equal(1, await t.ScalarAsync<int>("select count(*)::int from members where tenant_id = @tid and github_login like 'admin-%' ", new { tid = t.Id }));
        Assert.Equal(2, await t.ScalarAsync<int>("select count(*)::int from members where tenant_id = @tid", new { tid = t.Id }));
        Assert.Null(await t.ScalarAsync<DateTime?>("select accepted_at from invitations where tenant_id = @tid", new { tid = t.Id }));
    }

    [DbFact]
    public async Task Unknown_or_malformed_tokens_and_anonymous_callers_are_refused()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var c = Client(t.ViewerLogin);

        Assert.Equal("invite_not_found", await ErrorAsync(await AcceptAsync(c, Invitations.NewToken())));
        Assert.Equal("invite_not_found", await ErrorAsync(await AcceptAsync(c, "short")));
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/invitations/accept", new { token = Invitations.NewToken() })).StatusCode);
    }

    [DbFact]
    public async Task Revoked_expired_and_replaced_invitations_cannot_be_used()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var (a, _) = Outsider();
        var (b, _) = Outsider();
        var (c, _) = Outsider();
        var (aId, aToken) = await InvitedAsync(admin, a);
        var (bId, bToken) = await InvitedAsync(admin, b);
        var (_, cOld) = await InvitedAsync(admin, c);
        var (_, cNew) = await InvitedAsync(admin, c);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/invitations/{aId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/invitations/{aId}")).StatusCode);
        await t.ExecAsync("update invitations set expires_at = now() - interval '1 minute' where id = @bId", new { bId });

        var revoked = await AcceptAsync(Client(a), aToken);
        Assert.Equal(HttpStatusCode.Gone, revoked.StatusCode);
        Assert.Equal("invite_revoked", await ErrorAsync(revoked));
        Assert.Equal("invite_expired", await ErrorAsync(await AcceptAsync(Client(b), bToken)));
        Assert.Equal("invite_revoked", await ErrorAsync(await AcceptAsync(Client(c), cOld)));
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(Client(c), cNew)).StatusCode);
        Assert.Equal(2, await t.ScalarAsync<int>(
            "select count(*)::int from audit_log where tenant_id = @tid and action = 'invitation.revoke'", new { tid = t.Id }));

        // An expired invitation is replaced by a new one for the same person.
        var (_, bNew) = await InvitedAsync(admin, b);
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(Client(b), bNew)).StatusCode);
    }

    [DbFact]
    public async Task Removed_member_loses_access_at_once_but_their_history_stays()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var adminLogin = await AdminAsync(t);
        var admin = Client(adminLogin);
        var viewerId = await t.ScalarAsync<Guid>("select id from members where github_login = @l", new { l = t.ViewerLogin });
        var note = await t.AddNoteAsync("weekly");
        await t.ExecAsync("update meeting_notes set created_by = @viewerId where id = @note", new { viewerId, note });
        var viewer = Client(t.ViewerLogin);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/me")).StatusCode);

        var remove = await admin.DeleteAsync($"/api/members/{viewerId}");

        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/issues")).StatusCode);
        var members = await admin.GetFromJsonAsync<JsonElement>("/api/members");
        Assert.DoesNotContain(members.EnumerateArray(), m => m.GetProperty("id").GetGuid() == viewerId);
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/meeting-notes/{note}");
        Assert.Equal(t.ViewerLogin, detail.GetProperty("createdBy").GetProperty("githubLogin").GetString());
        Assert.Equal(1, await t.ScalarAsync<int>(
            "select count(*)::int from audit_log where tenant_id = @tid and action = 'member.remove' and target_id = @viewerId",
            new { tid = t.Id, viewerId }));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/members/{viewerId}")).StatusCode);
    }

    [DbFact]
    public async Task Removing_an_admin_revokes_the_invitations_they_sent()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var first = Client(await AdminAsync(t, "first"));
        var secondLogin = await AdminAsync(t, "second");
        var (login, _) = Outsider();
        var (invitationId, token) = await InvitedAsync(Client(secondLogin), login);
        var secondId = await t.ScalarAsync<Guid>("select id from members where github_login = @secondLogin", new { secondLogin });

        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync($"/api/members/{secondId}")).StatusCode);

        Assert.NotNull(await t.ScalarAsync<DateTime?>("select revoked_at from invitations where id = @invitationId", new { invitationId }));
        Assert.Equal("invite_revoked", await ErrorAsync(await AcceptAsync(Client(login), token)));
    }

    [DbFact]
    public async Task Last_admin_cannot_remove_themselves_but_can_when_another_admin_exists()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var firstLogin = await AdminAsync(t, "first");
        var first = Client(firstLogin);
        var firstId = await t.ScalarAsync<Guid>("select id from members where github_login = @firstLogin", new { firstLogin });

        var refused = await first.DeleteAsync($"/api/members/{firstId}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("last_admin", await ErrorAsync(refused));

        await AdminAsync(t, "second");
        Assert.Equal(HttpStatusCode.NoContent, (await first.DeleteAsync($"/api/members/{firstId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/me")).StatusCode);
    }

    [DbFact]
    public async Task Two_admins_removing_each_other_at_once_leave_one_admin()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var aLogin = await AdminAsync(t, "a");
        var bLogin = await AdminAsync(t, "b");
        var aId = await t.ScalarAsync<Guid>("select id from members where github_login = @aLogin", new { aLogin });
        var bId = await t.ScalarAsync<Guid>("select id from members where github_login = @bLogin", new { bLogin });

        await Task.WhenAll(Client(aLogin).DeleteAsync($"/api/members/{bId}"), Client(bLogin).DeleteAsync($"/api/members/{aId}"));

        Assert.Equal(1, await t.ScalarAsync<int>(
            "select count(*)::int from members where tenant_id = @tid and role = 'admin' and left_at is null", new { tid = t.Id }));
    }

    [DbFact]
    public async Task Rejoining_after_removal_needs_a_new_invitation_and_reuses_the_member_row()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var (login, _) = Outsider();
        var (_, token) = await InvitedAsync(admin, login);
        var person = Client(login);
        var memberId = (await (await AcceptAsync(person, token)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("member").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/members/{memberId}")).StatusCode);

        var reused = await AcceptAsync(person, token);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("invite_used", await ErrorAsync(reused));

        var (_, fresh) = await InvitedAsync(admin, login);
        var rejoin = await AcceptAsync(person, fresh);
        Assert.Equal(HttpStatusCode.OK, rejoin.StatusCode);
        Assert.Equal(memberId, (await rejoin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("member").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await person.GetAsync("/api/me")).StatusCode);
    }

    [DbFact]
    public async Task Admins_cannot_see_or_touch_another_companys_invitations_or_members()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        await using var other = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var otherAdmin = Client(await AdminAsync(other));
        var (invitationId, _) = await InvitedAsync(otherAdmin, Outsider().Login);
        var otherViewer = await other.ScalarAsync<Guid>("select id from members where github_login = @l", new { l = other.ViewerLogin });

        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/invitations/{invitationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/members/{otherViewer}")).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/invitations")).EnumerateArray());
        Assert.Null(await other.ScalarAsync<DateTime?>("select revoked_at from invitations where id = @invitationId", new { invitationId }));
        Assert.Null(await other.ScalarAsync<DateTime?>("select left_at from members where id = @otherViewer", new { otherViewer }));
    }

    private static Task<HttpResponseMessage> SetRoleAsync(HttpClient c, Guid id, string role) =>
        c.PatchAsJsonAsync($"/api/members/{id}", new { role });

    private static Task<Guid> IdAsync(TestTenant t, string login) =>
        t.ScalarAsync<Guid>("select id from members where github_login = @login", new { login });

    private static Task<string> RoleAsync(TestTenant t, Guid id) =>
        t.ScalarAsync<string>("select role from members where id = @id", new { id });

    [DbFact]
    public async Task Admin_promotes_and_demotes_a_member_and_both_are_audited()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var viewerId = await IdAsync(t, t.ViewerLogin);

        var promoted = await SetRoleAsync(admin, viewerId, "admin");

        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal("admin", (await promoted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
        Assert.Equal("admin", await RoleAsync(t, viewerId));
        var me = await Client(t.ViewerLogin).GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("admin", me.GetProperty("member").GetProperty("role").GetString());

        var demoted = await SetRoleAsync(admin, viewerId, "member");

        Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        Assert.Equal("member", await RoleAsync(t, viewerId));
        Assert.Equal(2, await t.ScalarAsync<int>(
            "select count(*)::int from audit_log where tenant_id = @tid and action = 'member.role' and target_id = @viewerId",
            new { tid = t.Id, viewerId }));
    }

    [DbFact]
    public async Task Role_change_rejects_non_admins_unknown_roles_and_other_tenants_members()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        await using var other = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var viewerId = await IdAsync(t, t.ViewerLogin);
        var (_, outsiderUserId) = await t.AddMemberAsync("peer");
        var peerId = await t.ScalarAsync<Guid>(
            "select id from members where tenant_id = @tid and github_user_id = @outsiderUserId", new { tid = t.Id, outsiderUserId });
        var foreignId = await IdAsync(other, other.ViewerLogin);

        var byMember = await SetRoleAsync(Client(t.ViewerLogin), peerId, "admin");
        var badRole = await SetRoleAsync(admin, viewerId, "owner");
        var foreign = await SetRoleAsync(admin, foreignId, "admin");

        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal("admin_only", await ErrorAsync(byMember));
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("member", await RoleAsync(t, peerId));
        Assert.Equal("member", await RoleAsync(other, foreignId));
    }

    [DbFact]
    public async Task Removed_member_cannot_be_promoted()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var viewerId = await IdAsync(t, t.ViewerLogin);
        await t.ExecAsync("update members set left_at = now() where id = @viewerId", new { viewerId });

        var response = await SetRoleAsync(admin, viewerId, "admin");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("member", await RoleAsync(t, viewerId));
    }

    [DbFact]
    public async Task Member_removed_from_the_github_organization_cannot_be_promoted_but_can_stay_or_be_demoted()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var viewerId = await IdAsync(t, t.ViewerLogin);
        await t.ExecAsync("update members set removed_at = now() where id = @viewerId", new { viewerId });

        var response = await SetRoleAsync(admin, viewerId, "admin");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("member_removed", await ErrorAsync(response));
        Assert.Equal("member", await RoleAsync(t, viewerId));
        Assert.Equal(HttpStatusCode.OK, (await SetRoleAsync(admin, viewerId, "member")).StatusCode);
    }

    [DbFact]
    public async Task Accepting_one_invitation_from_two_tabs_at_once_gives_both_the_same_200()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var (login, _) = Outsider();
        var (_, token) = await InvitedAsync(admin, login);
        var person = Client(login);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AcceptAsync(person, token)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = new HashSet<Guid>();
        foreach (var r in results)
            ids.Add((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("member").GetProperty("id").GetGuid());
        Assert.Single(ids);
    }

    [DbFact]
    public async Task Two_current_members_still_cannot_share_a_login()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var (login, _) = await t.AddMemberAsync("dup");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => t.ExecAsync(
            "insert into members (tenant_id, github_user_id, github_login) values (@tid, @userId, @login)",
            new { tid = t.Id, userId = TestTenant.NextId(), login }));
        Assert.Contains("members_tenant_login_active", ex.Message);
    }

    [DbFact]
    public async Task Invite_retry_with_the_same_key_does_not_call_github_again()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var admin = Client(await AdminAsync(t));
        var (login, _) = Outsider();

        var first = await InviteAsync(admin, login, "same-key");
        var callsAfterFirst = _github.Calls;
        var again = await InviteAsync(admin, login, "same-key");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(callsAfterFirst, _github.Calls);
    }

    [DbFact]
    public async Task Last_admin_cannot_demote_themselves_but_can_when_another_admin_exists()
    {
        await using var t = await TestTenant.CreateAsync(_factory);
        var firstLogin = await AdminAsync(t, "first");
        var first = Client(firstLogin);
        var firstId = await IdAsync(t, firstLogin);

        var refused = await SetRoleAsync(first, firstId, "member");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("last_admin", await ErrorAsync(refused));
        Assert.Equal("admin", await RoleAsync(t, firstId));

        await AdminAsync(t, "second");
        var allowed = await SetRoleAsync(first, firstId, "member");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("member", await RoleAsync(t, firstId));
    }

    [DbFact]
    public async Task Two_admins_demoting_each_other_at_once_leave_one_admin()
    {
        for (var round = 0; round < 5; round++)
        {
            await using var t = await TestTenant.CreateAsync(_factory);
            var aLogin = await AdminAsync(t, "a");
            var bLogin = await AdminAsync(t, "b");
            var (aId, bId) = (await IdAsync(t, aLogin), await IdAsync(t, bLogin));
            var (a, b) = (Client(aLogin), Client(bLogin));

            var results = await Task.WhenAll(SetRoleAsync(a, bId, "member"), SetRoleAsync(b, aId, "member"));

            Assert.Equal(1, await t.ScalarAsync<int>(
                "select count(*)::int from members where tenant_id = @tid and role = 'admin' and left_at is null", new { tid = t.Id }));
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        }
    }
}
