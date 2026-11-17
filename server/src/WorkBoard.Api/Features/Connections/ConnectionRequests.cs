using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;

namespace WorkBoard.Api.Features.Connections;

public sealed record ConnectionRequest(Guid Id, DateTime RequestedAt, string? RequestedByLogin, string? AccountLogin);

public sealed record ConnectionRequestState(ConnectionRequest? Request);

public sealed record ConnectionRequestCheck(string Status, GithubConnection? Connection = null);

public sealed record OrganizationChoice(long AccountId, string Login);

public sealed record MultipleOrganizationsBody(string Error, IReadOnlyList<OrganizationChoice> Organizations);

public sealed record CheckConnectionRequestBody(long? AccountId);

// An organization install that waits for the owner's approval. GitHub's request redirect carries no installation id,
// so the company keeps one open request and "다시 확인" looks for the approved installation through the App itself.
public static class ConnectionRequests
{
    // GitHub and this server stamp the request and the installation with separate clocks.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private const string Select = """
        select r.id, r.requested_at, m.github_login as requested_by_login, r.account_login
          from github_connection_requests r
          left join members m on m.tenant_id = r.tenant_id and m.id = r.requested_by
        """;

    public static void MapConnectionRequests(this RouteGroupBuilder api)
    {
        api.MapGet("/connections/github/request", async (TenantDb db, CancellationToken ct) =>
        {
            var row = await db.ReadAsync(
                s => s.QuerySingleOrDefaultAsync<ConnectionRequest>($"{Select} where r.resolved_at is null"), ct);
            return new ConnectionRequestState(row);
        });
        api.MapPost("/connections/github/request", CreateAsync);
        api.MapDelete("/connections/github/request", CancelAsync);
        api.MapPost("/connections/github/request/check", CheckAsync);
    }

    private static async Task<IResult> CreateAsync(
        ICurrentUser user, [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, CancellationToken ct)
    {
        var caller = user.Identity;
        if (caller.Member.Role != "admin")
            return ConnectGithub.Error(StatusCodes.Status403Forbidden, "admin_only");

        await using var conn = await sync.OpenConnectionAsync(ct);
        var created = await conn.ExecuteAsync(new CommandDefinition("""
            insert into github_connection_requests (tenant_id, requested_by, requester_github_user_id)
            values (@tenantId, @memberId, @userId)
            on conflict (tenant_id) where resolved_at is null do nothing
            """, new { tenantId = caller.TenantId, memberId = caller.Member.Id, userId = caller.Member.GithubUserId },
            cancellationToken: ct));
        var row = await conn.QuerySingleAsync<ConnectionRequest>(new CommandDefinition(
            $"{Select} where r.tenant_id = @tenantId and r.resolved_at is null", new { tenantId = caller.TenantId },
            cancellationToken: ct));
        return Results.Json(row, statusCode: created > 0 ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static async Task<IResult> CancelAsync(
        ICurrentUser user, [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, CancellationToken ct)
    {
        var caller = user.Identity;
        if (caller.Member.Role != "admin")
            return ConnectGithub.Error(StatusCodes.Status403Forbidden, "admin_only");
        await using var conn = await sync.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "update github_connection_requests set resolved_at = now() where tenant_id = @tenantId and resolved_at is null",
            new { tenantId = caller.TenantId }, cancellationToken: ct));
        return Results.NoContent();
    }

    // Links the one organization installation that was created after the request, is not connected anywhere, and whose
    // organization the calling admin is an active member of (checked by GitHub user id with that installation's token).
    private static async Task<IResult> CheckAsync(
        HttpContext http, ICurrentUser user, IOptions<GitHubAppOptions> options, IGitHubAppClient github,
        [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync, ILoggerFactory logs, CancellationToken ct)
    {
        var caller = user.Identity;
        if (caller.Member.Role != "admin")
            return ConnectGithub.Error(StatusCodes.Status403Forbidden, "admin_only");
        if (!options.Value.CanReadInstallations)
            return ConnectGithub.Error(StatusCodes.Status503ServiceUnavailable, "github_app_not_configured");
        var body = await ReadAsync(http, ct);

        DateTime requestedAt;
        long[] taken;
        try
        {
            await using var conn = await sync.OpenConnectionAsync(ct);
            var open = await conn.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
                "select requested_at from github_connection_requests where tenant_id = @tenantId and resolved_at is null",
                new { tenantId = caller.TenantId }, cancellationToken: ct));
            if (open is null)
                return ConnectGithub.Error(StatusCodes.Status404NotFound, "no_request");
            requestedAt = open.Value;

            var installations = await github.ListAppInstallationsAsync(ct);
            var since = new DateTimeOffset(DateTime.SpecifyKind(requestedAt, DateTimeKind.Utc)) - ClockSkew;
            var recent = installations
                .Where(i => ConnectGithub.IsOrganization(i.AccountType) && i.CreatedAt >= since)
                .Where(i => body?.AccountId is not { } want || i.AccountId == want)
                .ToList();
            taken = (await conn.QueryAsync<long>(new CommandDefinition("""
                select installation_id from github_connections
                 where disconnected_at is null and installation_id = any(@ids)
                """, new { ids = recent.Select(i => i.Id).ToArray() }, cancellationToken: ct))).ToArray();

            var candidates = new List<GitHubAppInstallation>();
            var notGranted = false;
            foreach (var i in recent.Where(i => !taken.Contains(i.Id)))
            {
                var membership = await github.GetOrganizationMembershipAsync(
                    i.Id, i.AccountLogin, caller.Member.GithubUserId, caller.Member.GithubLogin, ct);
                if (membership is OrgMembership.Owner or OrgMembership.Member)
                    candidates.Add(i);
                notGranted |= membership == OrgMembership.NotGranted;
            }

            if (candidates.Count > 1)
                return Results.Json(
                    new MultipleOrganizationsBody("multiple_organizations",
                        candidates.Select(c => new OrganizationChoice(c.AccountId, c.AccountLogin)).ToList()),
                    statusCode: StatusCodes.Status409Conflict);
            if (candidates.Count == 0)
            {
                if (notGranted)
                    return ConnectGithub.Error(StatusCodes.Status422UnprocessableEntity, "org_members_not_granted");
                return taken.Length > 0 && recent.All(i => taken.Contains(i.Id))
                    ? ConnectGithub.Error(StatusCodes.Status409Conflict, "account_connected")
                    : Results.Json(new ConnectionRequestCheck("pending"));
            }

            var pick = candidates[0];
            var linked = await ConnectGithub.LinkAsync(sync, github, logs, caller.TenantId, pick.Id,
                new GitHubInstallation(pick.AccountId, pick.AccountLogin, pick.AccountType, pick.RepositorySelection), ct);
            return linked is { } l
                ? Results.Json(new ConnectionRequestCheck("connected", l.Connection),
                    statusCode: l.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK)
                : ConnectGithub.Error(StatusCodes.Status409Conflict, "account_connected");
        }
        catch (GitHubAppKeyException)
        {
            return ConnectGithub.Error(StatusCodes.Status503ServiceUnavailable, "github_app_not_configured");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or GitHubSourceException
                                  || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return ConnectGithub.Error(StatusCodes.Status502BadGateway, "github_unavailable");
        }
    }

    private static async Task<CheckConnectionRequestBody?> ReadAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType())
            return null;
        try
        {
            return await http.Request.ReadFromJsonAsync<CheckConnectionRequestBody>(ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
