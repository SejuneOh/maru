using Dapper;
using Npgsql;
using WorkBoard.Api.Infrastructure.Data;

namespace WorkBoard.Api.Features.Connections;

public sealed record GithubConnection(
    Guid Id,
    long AccountId,
    string AccountLogin,
    string AccountType,
    long? InstallationId,
    string? RepositorySelection,
    DateTime CreatedAt,
    DateTime? DisconnectedAt,
    int? OrgMemberCount,
    string AuthKind,
    string? TokenLast4,
    string? TokenOwnerLogin,
    DateTime? TokenExpiresAt,
    string? TokenStatus,
    [property: System.Text.Json.Serialization.JsonIgnore] string? TokenScopesText)
{
    public string[]? TokenScopes => TokenScopesText is null ? null : TokenScopesText.Length == 0 ? [] : TokenScopesText.Split(',');
}

public static class ConnectionsEndpoints
{
    // Selects from github_connections aliased c. org_member_count is null for personal accounts and for an
    // organization whose members could not be read.
    public const string Columns = """
        c.id, c.account_id, c.account_login, c.account_type, c.installation_id, c.repository_selection, c.created_at,
        c.disconnected_at,
        case when c.account_type = 'organization' then
          (select nullif(count(*), 0)::int from github_org_members o
            where o.tenant_id = c.tenant_id and o.connection_id = c.id and o.removed_at is null)
        end as org_member_count,
        c.auth_kind, k.last4 as token_last4, k.owner_login as token_owner_login, k.expires_at as token_expires_at,
        k.status as token_status, array_to_string(k.scopes, ',') as token_scopes_text
        """;

    // The stored token's metadata; never the token itself.
    public const string Joins = "left join github_credentials k on k.tenant_id = c.tenant_id and k.connection_id = c.id";

    public static void MapConnections(this RouteGroupBuilder api)
    {
        api.MapConnectGithub();
        api.MapConnectGithubToken();
        api.MapConnectionRequests();
        api.MapGet("/connections", async (TenantDb db, CancellationToken ct) =>
        {
            var rows = await db.ReadAsync(
                s => s.QueryAsync<GithubConnection>($"""
                    select {Columns}
                      from github_connections c
                      {Joins}
                     order by c.disconnected_at is not null, c.account_login
                    """),
                ct);
            return rows.ToList();
        });
    }

    // On the sync role, so the tenant filter is explicit.
    internal static Task<GithubConnection> ReadAsync(NpgsqlConnection conn, Guid tenantId, Guid id, CancellationToken ct) =>
        conn.QuerySingleAsync<GithubConnection>(new CommandDefinition(
            $"select {Columns} from github_connections c {Joins} where c.tenant_id = @tenantId and c.id = @id",
            new { tenantId, id }, cancellationToken: ct));
}
