using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using WorkBoard.Api.Features.Me;
using WorkBoard.Api.Features.Tenants;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Members;

public sealed record InvitedBy(string GithubLogin, string? DisplayName);

public sealed record Invitation(
    Guid Id, long GithubUserId, string GithubLogin, string Role, InvitedBy? InvitedBy, DateTime CreatedAt, DateTime ExpiresAt);

// Token is the only copy of the link secret: shown once, null in a replay of the same Idempotency-Key.
public sealed record CreatedInvitation(Invitation Invitation, string? Token);

public sealed record InvitationPreview(TenantInfo Tenant, string GithubLogin, string Role, InvitedBy? InvitedBy, DateTime ExpiresAt, bool AlreadyMember);

public sealed record JoinedTenant(TenantInfo Tenant, Member Member);

internal sealed record CreateInvitationRequest(string? GithubLogin);

internal sealed record TokenRequest(string? Token);

internal sealed record ChangeRoleRequest(string? Role);

public static class Invitations
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private const int MaxBodyBytes = 4096;

    public const string InvalidLogin = "invalid_login";
    public const string GithubUserNotFound = "github_user_not_found";
    public const string GithubRateLimited = "github_rate_limited";
    public const string AlreadyMember = "already_member";
    public const string LastAdmin = "last_admin";
    public const string LoginTaken = "login_taken";
    public const string InviteNotFound = "invite_not_found";
    public const string InviteOtherAccount = "invite_other_account";
    public const string InviteRevoked = "invite_revoked";
    public const string InviteExpired = "invite_expired";
    public const string InviteUsed = "invite_used";

    private const string InvitationColumns = """
        i.id, i.github_user_id, i.github_login, i.role, i.created_at, i.expires_at,
        b.github_login as invited_by_login, b.display_name as invited_by_name
        """;

    // Admin endpoints, behind CurrentMemberFilter.
    public static void MapInvitations(this RouteGroupBuilder api)
    {
        api.MapGet("/invitations", ListAsync);
        api.MapPost("/invitations", CreateAsync);
        api.MapDelete("/invitations/{id:guid}", RevokeAsync);
        api.MapDelete("/members/{id:guid}", RemoveMemberAsync);
        api.MapPatch("/members/{id:guid}", ChangeRoleAsync);
    }

    // Join endpoints: the caller is signed in but not yet a member, so these are not behind CurrentMemberFilter.
    public static void MapJoin(this RouteGroupBuilder api)
    {
        api.MapPost("/invitations/preview", PreviewAsync);
        api.MapPost("/invitations/accept", AcceptAsync);
    }

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsTokenShaped(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static IResult Error(int status, string code) => ApiErrors.Of(status, code);

    private static WriteResult WriteError(int status, string code) => new(status, new ErrorBody(code));

    // ---- admin ----

    private static async Task<IResult> ListAsync(ICurrentUser user, TenantDb db, CancellationToken ct)
    {
        if (user.Identity.Member.Role != "admin")
            return ApiErrors.AdminOnlyResult();
        var rows = await db.ReadAsync(s => s.QueryAsync<InvitationRow>($"""
            select {InvitationColumns}
              from invitations i
              left join members b on b.id = i.invited_by
             where i.accepted_at is null and i.revoked_at is null
             order by i.created_at desc
            """), ct);
        return Results.Ok(rows.Select(r => r.ToInvitation()).ToList());
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http, ICurrentUser user, TenantDb db, IAuditLog audit, IGitHubUserLookup github,
        IOptions<GitHubAppOptions> app, CancellationToken ct)
    {
        if (user.Identity.Member.Role != "admin")
            return ApiErrors.AdminOnlyResult();

        var bytes = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        if (bytes is null || !http.Request.HasJsonContentType())
            return ApiErrors.BadRequest();
        CreateInvitationRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<CreateInvitationRequest>(bytes, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return ApiErrors.BadRequest();
        }
        var login = request?.GithubLogin?.Trim().TrimStart('@');
        if (login is null || !GitHubUserLookup.IsValidLogin(login))
            return ApiErrors.BadRequest(InvalidLogin);

        var key = http.Request.Headers[Idempotency.HeaderName].ToString();
        if (key.Length > Idempotency.KeyMaxLength)
            return ApiErrors.BadRequest();

        long? installationId = app.Value.CanReadInstallations
            ? await db.ReadAsync(s => s.QuerySingleOrDefaultAsync<long?>("""
                select installation_id from github_connections
                 where disconnected_at is null and installation_id is not null
                 order by created_at limit 1
                """), ct)
            : null;

        GitHubUser? found;
        try
        {
            found = await github.FindByLoginAsync(login, installationId, ct);
        }
        catch (GitHubSourceException e) when (e.Kind == GitHubSourceErrorKind.RateLimited)
        {
            return Error(StatusCodes.Status429TooManyRequests, GithubRateLimited);
        }
        catch (GitHubSourceException)
        {
            return ApiErrors.GithubUnavailableResult();
        }
        if (found is null)
            return ApiErrors.BadRequest(GithubUserNotFound);

        var hash = Idempotency.HashRequest(
            user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);
        return await db.WriteAsync(s => Idempotency.RunAsync(s, user, key, hash,
            () => InsertAsync(s, user, audit, found),
            body => body is CreatedInvitation created ? created with { Token = null } : body), ct);
    }

    private static async Task<WriteResult> InsertAsync(TenantSession s, ICurrentUser user, IAuditLog audit, GitHubUser person)
    {
        var tenantId = user.Identity.TenantId;
        await s.ExecuteAsync("select pg_advisory_xact_lock(hashtextextended(@scope, 0))",
            new { scope = $"invite:{tenantId:N}:{person.Id}" });

        if (await s.QuerySingleOrDefaultAsync<bool>(
                "select exists (select 1 from members where github_user_id = @id and left_at is null)", new { id = person.Id }))
            return WriteError(StatusCodes.Status409Conflict, AlreadyMember);

        var replaced = await s.QueryAsync<Guid>("""
            update invitations set revoked_at = now()
             where github_user_id = @id and accepted_at is null and revoked_at is null
            returning id
            """, new { id = person.Id });
        foreach (var old in replaced)
            await audit.WriteAsync(s, "invitation.revoke", "invitation", old, null, new { replaced = true });

        var token = NewToken();
        var id = await s.QuerySingleOrDefaultAsync<Guid>("""
            insert into invitations (tenant_id, github_user_id, github_login, token_hash, invited_by, expires_at)
            values (@tenantId, @userId, @login, @hash, @invitedBy, now() + @lifetime)
            returning id
            """, new
        {
            tenantId, userId = person.Id, login = person.Login, hash = HashToken(token),
            invitedBy = user.Identity.Member.Id, lifetime = Lifetime,
        });
        var invitation = (await s.QuerySingleOrDefaultAsync<InvitationRow>($"""
            select {InvitationColumns} from invitations i left join members b on b.id = i.invited_by where i.id = @id
            """, new { id }))!.ToInvitation();
        await audit.WriteAsync(s, "invitation.create", "invitation", id, null,
            new { invitation.GithubUserId, invitation.GithubLogin, invitation.Role, invitation.ExpiresAt });
        return new WriteResult(StatusCodes.Status201Created, new CreatedInvitation(invitation, token));
    }

    private static Task<IResult> RevokeAsync(Guid id, HttpContext http, ICurrentUser user, TenantDb db, IAuditLog audit, CancellationToken ct) =>
        AdminWriteAsync(http, user, db, ct, async s =>
        {
            var row = await s.QuerySingleOrDefaultAsync<InvitationRow>($"""
                select {InvitationColumns} from invitations i left join members b on b.id = i.invited_by
                 where i.id = @id and i.accepted_at is null and i.revoked_at is null
                 for update of i
                """, new { id });
            if (row is null)
                return WriteError(StatusCodes.Status404NotFound, ApiErrors.NotFound);
            await s.ExecuteAsync("update invitations set revoked_at = now() where id = @id", new { id });
            await audit.WriteAsync(s, "invitation.revoke", "invitation", id,
                new { row.GithubUserId, row.GithubLogin }, null);
            return new WriteResult(StatusCodes.Status204NoContent);
        });

    // The row stays (left_at) so history keeps the name. Open invitations the person created are revoked with it.
    private static Task<IResult> RemoveMemberAsync(Guid id, HttpContext http, ICurrentUser user, TenantDb db, IAuditLog audit, CancellationToken ct) =>
        AdminWriteAsync(http, user, db, ct, async s =>
        {
            // Locks every active admin, so two admins removing each other cannot both succeed.
            var admins = await s.QueryAsync<Guid>(
                "select id from members where left_at is null and role = 'admin' order by id for update");
            if (!admins.Contains(user.Identity.Member.Id))
                return WriteError(StatusCodes.Status403Forbidden, ApiErrors.AdminOnly);
            var target = await s.QuerySingleOrDefaultAsync<MemberRow>(
                $"select {MemberRow.Columns} from members m where m.id = @id and m.left_at is null for update", new { id });
            if (target is null)
                return WriteError(StatusCodes.Status404NotFound, ApiErrors.NotFound);
            if (target.Role == "admin" && admins.Count(a => a != id) == 0)
                return WriteError(StatusCodes.Status409Conflict, LastAdmin);

            await s.ExecuteAsync("update members set left_at = now() where id = @id", new { id });
            var revoked = await s.QueryAsync<Guid>("""
                update invitations set revoked_at = now()
                 where invited_by = @id and accepted_at is null and revoked_at is null
                returning id
                """, new { id });
            foreach (var invitationId in revoked)
                await audit.WriteAsync(s, "invitation.revoke", "invitation", invitationId, null, new { inviterRemoved = true });
            await audit.WriteAsync(s, "member.remove", "member", id,
                new { target.GithubUserId, target.GithubLogin, target.Role }, null);
            return new WriteResult(StatusCodes.Status204NoContent);
        });

    // Locks every active admin like removal, so two admins demoting each other leave one admin.
    private static async Task<IResult> ChangeRoleAsync(
        Guid id, HttpContext http, ICurrentUser user, TenantDb db, IAuditLog audit, CancellationToken ct)
    {
        if (user.Identity.Member.Role != "admin")
            return ApiErrors.AdminOnlyResult();
        var bytes = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        string? role = null;
        try
        {
            if (bytes is { Length: > 0 })
                role = JsonSerializer.Deserialize<ChangeRoleRequest>(bytes, JsonSerializerOptions.Web)?.Role;
        }
        catch (JsonException)
        {
        }
        if (bytes is null || role is not ("admin" or "member"))
            return ApiErrors.BadRequest();

        var key = http.Request.Headers[Idempotency.HeaderName].ToString();
        var hash = Idempotency.HashRequest(
            user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);
        return await db.WriteAsync(s => Idempotency.RunAsync(s, user, key, hash, async () =>
        {
            var admins = await s.QueryAsync<Guid>(
                "select id from members where left_at is null and role = 'admin' order by id for update");
            if (!admins.Contains(user.Identity.Member.Id))
                return WriteError(StatusCodes.Status403Forbidden, ApiErrors.AdminOnly);
            var target = await s.QuerySingleOrDefaultAsync<MemberRow>(
                $"select {MemberRow.Columns}, m.removed_at from members m where m.id = @id and m.left_at is null for update",
                new { id });
            if (target is null)
                return WriteError(StatusCodes.Status404NotFound, ApiErrors.NotFound);
            if (target.Role == role)
                return new WriteResult(StatusCodes.Status200OK, target.ToMember());
            if (role == "member" && admins.Count(a => a != id) == 0)
                return WriteError(StatusCodes.Status409Conflict, LastAdmin);

            await s.ExecuteAsync("update members set role = @role where id = @id", new { role, id });
            await audit.WriteAsync(s, "member.role", "member", id,
                new { target.GithubUserId, target.GithubLogin, target.Role }, new { Role = role });
            target.Role = role;
            return new WriteResult(StatusCodes.Status200OK, target.ToMember());
        }), ct);
    }

    private static async Task<IResult> AdminWriteAsync(
        HttpContext http, ICurrentUser user, TenantDb db, CancellationToken ct, Func<TenantSession, Task<WriteResult>> work)
    {
        if (user.Identity.Member.Role != "admin")
            return ApiErrors.AdminOnlyResult();
        var bytes = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        if (bytes is null)
            return ApiErrors.BadRequest();
        var key = http.Request.Headers[Idempotency.HeaderName].ToString();
        var hash = Idempotency.HashRequest(
            user.Identity.Member.Id, http.Request.Method, http.Request.Path + http.Request.QueryString, bytes);
        return await db.WriteAsync(s => Idempotency.RunAsync(s, user, key, hash, () => work(s)), ct);
    }

    // ---- join ----

    private sealed record Found(InviteRow Row, IResult? Error);

    // The token alone finds the row; the caller's GitHub id must then equal the invited id before anything about
    // the invitation (even the company name) is returned.
    private static async Task<Found?> FindAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string token, long callerId, bool lockRow, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<InviteRow>(new CommandDefinition($"""
            select i.id, i.tenant_id, t.name as tenant_name, i.github_user_id, i.github_login, i.role, i.expires_at,
                   i.accepted_at, i.revoked_at, (i.expires_at <= now()) as expired,
                   b.github_login as invited_by_login, b.display_name as invited_by_name,
                   exists (select 1 from members m where m.tenant_id = i.tenant_id and m.github_user_id = i.github_user_id
                                                     and m.left_at is null) as already_member
              from invitations i
              join tenants t on t.id = i.tenant_id
              left join members b on b.id = i.invited_by
             where i.token_hash = @hash
             {(lockRow ? "for update of i" : "")}
            """, new { hash = HashToken(token) }, tx, cancellationToken: ct));
        if (row is null)
            return null;
        if (row.GithubUserId != callerId)
            return new Found(row, Error(StatusCodes.Status403Forbidden, InviteOtherAccount));
        if (row.AcceptedAt is not null)
            return new Found(row, row.AlreadyMember ? null : Error(StatusCodes.Status409Conflict, InviteUsed));
        if (row.RevokedAt is not null)
            return new Found(row, Error(StatusCodes.Status410Gone, InviteRevoked));
        if (row.Expired)
            return new Found(row, Error(StatusCodes.Status410Gone, InviteExpired));
        return new Found(row, null);
    }

    private static async Task<(Caller? Caller, string? Token, IResult? Error)> ReadJoinAsync(
        HttpContext http, ICallerResolver callers, CancellationToken ct)
    {
        var caller = await callers.ResolveAsync(http, ct);
        if (caller is null)
            return (null, null, Error(StatusCodes.Status401Unauthorized, "unauthenticated"));
        if (!http.Request.HasJsonContentType())
            return (caller, null, ApiErrors.BadRequest());
        var bytes = await RequestBodies.ReadAsync(http, MaxBodyBytes, ct);
        if (bytes is null)
            return (caller, null, ApiErrors.BadRequest());
        TokenRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<TokenRequest>(bytes, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return (caller, null, ApiErrors.BadRequest());
        }
        return IsTokenShaped(body?.Token)
            ? (caller, body!.Token, null)
            : (caller, null, Error(StatusCodes.Status404NotFound, InviteNotFound));
    }

    private static async Task<IResult> PreviewAsync(
        HttpContext http, ICallerResolver callers, [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, CancellationToken ct)
    {
        var (caller, token, error) = await ReadJoinAsync(http, callers, ct);
        if (error is not null)
            return error;
        await using var conn = await sync.OpenConnectionAsync(ct);
        var found = await FindAsync(conn, null, token!, caller!.GithubUserId, lockRow: false, ct);
        if (found is null)
            return Error(StatusCodes.Status404NotFound, InviteNotFound);
        if (found.Error is not null)
            return found.Error;
        var r = found.Row;
        return Results.Ok(new InvitationPreview(
            new TenantInfo(r.TenantId, r.TenantName), r.GithubLogin, r.Role, r.InvitedBy(), r.ExpiresAt, r.AlreadyMember));
    }

    // Repeating an accept that already succeeded returns the same 200, so no Idempotency-Key is needed.
    private static async Task<IResult> AcceptAsync(
        HttpContext http, ICallerResolver callers, IAuthIdentitySource source, IMemberLookup members,
        [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, CancellationToken ct)
    {
        var (caller, token, error) = await ReadJoinAsync(http, callers, ct);
        if (error is not null)
            return error;

        await using var conn = await sync.OpenConnectionAsync(ct);
        GithubProfile? profile = null;
        {
            // Cheap checks first, so a wrong account or a dead link never reaches the identity source.
            var peek = await FindAsync(conn, null, token!, caller!.GithubUserId, lockRow: false, ct);
            if (peek is null)
                return Error(StatusCodes.Status404NotFound, InviteNotFound);
            if (peek.Error is not null)
                return peek.Error;
            if (peek.Row.AcceptedAt is null)
                profile = await TenantsEndpoints.ProfileAsync(caller, source, members, ct);
            if (peek.Row.AcceptedAt is null && profile is null)
                return Error(StatusCodes.Status401Unauthorized, "unauthenticated");
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        var found = await FindAsync(conn, tx, token!, caller.GithubUserId, lockRow: true, ct);
        if (found is null)
            return Error(StatusCodes.Status404NotFound, InviteNotFound);
        if (found.Error is not null)
            return found.Error;
        var r = found.Row;
        var tenant = new TenantInfo(r.TenantId, r.TenantName);

        if (r.AcceptedAt is not null || r.AlreadyMember)
        {
            if (r.AcceptedAt is null)
                await conn.ExecuteAsync(new CommandDefinition(
                    "update invitations set accepted_at = now() where id = @id", new { id = r.Id }, tx, cancellationToken: ct));
            var current = await conn.QuerySingleAsync<MemberRow>(new CommandDefinition($"""
                select {MemberRow.Columns} from members m where m.tenant_id = @tenantId and m.github_user_id = @userId
                """, new { r.TenantId, userId = r.GithubUserId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return Results.Ok(new JoinedTenant(tenant, current.ToMember()));
        }

        var args = new
        {
            r.TenantId, userId = r.GithubUserId, login = profile!.Login ?? r.GithubLogin,
            displayName = profile.DisplayName, avatarUrl = profile.AvatarUrl, r.Role,
        };
        MemberRow member;
        try
        {
            member = await conn.QuerySingleAsync<MemberRow>(new CommandDefinition("""
                insert into members as m (tenant_id, github_user_id, github_login, display_name, avatar_url, role)
                values (@tenantId, @userId, @login, @displayName, @avatarUrl, @role)
                on conflict (tenant_id, github_user_id) do update
                   set left_at = null, role = excluded.role, github_login = excluded.github_login,
                       display_name = coalesce(excluded.display_name, m.display_name),
                       avatar_url = coalesce(excluded.avatar_url, m.avatar_url)
                returning m.id, m.github_user_id, m.github_login, m.display_name, m.avatar_url, m.role
                """, args, tx, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Another member row of the company still holds this login (GitHub logins can be renamed and reused).
            return Error(StatusCodes.Status409Conflict, LoginTaken);
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "update invitations set accepted_at = now() where id = @id", new { id = r.Id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into audit_log (tenant_id, member_id, action, target_type, target_id, after)
            values (@tenantId, @memberId, 'member.join', 'member', @memberId, cast(@after as jsonb))
            """, new
        {
            r.TenantId, memberId = member.Id,
            after = JsonSerializer.Serialize(new { invitationId = r.Id, member.GithubUserId, member.GithubLogin, member.Role },
                JsonSerializerOptions.Web),
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return Results.Ok(new JoinedTenant(tenant, member.ToMember()));
    }

    private sealed class InvitationRow
    {
        public Guid Id { get; set; }
        public long GithubUserId { get; set; }
        public string GithubLogin { get; set; } = "";
        public string Role { get; set; } = "member";
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string? InvitedByLogin { get; set; }
        public string? InvitedByName { get; set; }

        public Invitation ToInvitation() => new(Id, GithubUserId, GithubLogin, Role,
            InvitedByLogin is null ? null : new InvitedBy(InvitedByLogin, InvitedByName), CreatedAt, ExpiresAt);
    }

    private sealed class InviteRow
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string TenantName { get; set; } = "";
        public long GithubUserId { get; set; }
        public string GithubLogin { get; set; } = "";
        public string Role { get; set; } = "member";
        public DateTime ExpiresAt { get; set; }
        public DateTime? AcceptedAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public bool Expired { get; set; }
        public string? InvitedByLogin { get; set; }
        public string? InvitedByName { get; set; }
        public bool AlreadyMember { get; set; }

        public InvitedBy? InvitedBy() => InvitedByLogin is null ? null : new InvitedBy(InvitedByLogin, InvitedByName);
    }
}
