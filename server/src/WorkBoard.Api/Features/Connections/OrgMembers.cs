using Dapper;
using Npgsql;
using WorkBoard.Api.Infrastructure.GitHub;

namespace WorkBoard.Api.Features.Connections;

// github_org_members: who belongs to each connected organization. Runs on the sync role, so every statement filters
// on tenant_id itself. members.removed_at is derived here: null while the person is a current member of any active
// organization connection of the company; set (keeping an earlier stamp) when such a connection reports them gone;
// null again when no active organization connection mentions them, e.g. after that organization is disconnected.
public static class OrgMembers
{
    public static async Task<string> ApplyEventAsync(
        NpgsqlConnection conn, Guid tenantId, Guid connectionId, long userId, bool removed, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into github_org_members (tenant_id, connection_id, github_user_id, removed_at)
            values (@tenantId, @connectionId, @userId, case when @removed then now() end)
            on conflict (connection_id, github_user_id) do update
               set removed_at = case when @removed then coalesce(github_org_members.removed_at, now()) end,
                   updated_at = now()
            """, new { tenantId, connectionId, userId, removed }, tx, cancellationToken: ct));
        var changed = await RecomputeAsync(conn, tx, tenantId, [userId], ct);
        var known = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from members where tenant_id = @tenantId and github_user_id = @userId)",
            new { tenantId, userId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return !known ? "ignored: unknown_member" : changed > 0 ? "applied" : "unchanged";
    }

    // Replaces the connection's member list with what GitHub returns. Null when the installation lacks "Members: read".
    public static async Task<int?> ImportAsync(
        NpgsqlDataSource sync, IGitHubAppClient github, Guid tenantId, Guid connectionId, long installationId,
        string organization, CancellationToken ct)
    {
        var list = await github.ListOrganizationMembersAsync(installationId, organization, ct);
        if (list is null)
            return null;
        await using var conn = await sync.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var ids = list.Select(u => u.Id).ToArray();
        await conn.ExecuteAsync(new CommandDefinition("""
            insert into github_org_members (tenant_id, connection_id, github_user_id, github_login)
            select @tenantId, @connectionId, u.id, u.login from unnest(@ids, @logins) as u(id, login)
            on conflict (connection_id, github_user_id) do update
               set github_login = excluded.github_login, removed_at = null, updated_at = now()
            """, new { tenantId, connectionId, ids, logins = list.Select(u => u.Login).ToArray() }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            update github_org_members set removed_at = now(), updated_at = now()
             where tenant_id = @tenantId and connection_id = @connectionId and removed_at is null
               and github_user_id <> all(@ids)
            """, new { tenantId, connectionId, ids }, tx, cancellationToken: ct));
        await RecomputeAsync(conn, tx, tenantId, null, ct);
        await tx.CommitAsync(ct);
        return list.Count;
    }

    // Re-derives members.removed_at for each company, e.g. after one of its connections was disconnected or resumed.
    public static async Task RecomputeTenantsAsync(NpgsqlConnection conn, IEnumerable<Guid> tenantIds, CancellationToken ct)
    {
        foreach (var tenantId in tenantIds.Distinct())
            await RecomputeAsync(conn, null, tenantId, null, ct);
    }

    // userIds null recomputes every member of the company.
    private static Task<int> RecomputeAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid tenantId, long[]? userIds, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            with active as (
              select o.github_user_id, o.removed_at
                from github_org_members o
                join github_connections c on c.tenant_id = o.tenant_id and c.id = o.connection_id and c.disconnected_at is null
               where o.tenant_id = @tenantId
            ),
            derived as (
              select m.id,
                     case when exists (select 1 from active a where a.github_user_id = m.github_user_id and a.removed_at is null)
                          then null
                          else (select max(a.removed_at) from active a where a.github_user_id = m.github_user_id)
                     end as gone_at
                from members m
               where m.tenant_id = @tenantId and (@userIds::bigint[] is null or m.github_user_id = any(@userIds))
            )
            update members m
               set removed_at = case when d.gone_at is null then null else coalesce(m.removed_at, d.gone_at) end
              from derived d
             where m.tenant_id = @tenantId and m.id = d.id
               and m.removed_at is distinct from (case when d.gone_at is null then null else coalesce(m.removed_at, d.gone_at) end)
            """, new { tenantId, userIds }, tx, cancellationToken: ct));
}
