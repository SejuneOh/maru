namespace WorkBoard.Api.Infrastructure.Data;

// Opens a tenant-scoped (workboard_app, RLS) session for a tenant and member resolved outside the request identity,
// such as an MCP client token. Mirrors TenantDb's open → begin → set app.tenant_id/app.member_id → commit flow.
public sealed class TenantSessions(IAppConnectionFactory connections)
{
    public async Task<T> RunAsync<T>(Guid tenantId, Guid memberId, Func<TenantSession, Task<T>> work, CancellationToken ct)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var session = new TenantSession(connection, transaction, ct);
        await session.ExecuteAsync(TenantDb.SetTenantSql, new { tenantId = tenantId.ToString(), memberId = memberId.ToString() });

        var result = await work(session);
        await transaction.CommitAsync(ct);
        return result;
    }
}
