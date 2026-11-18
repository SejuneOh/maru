using Dapper;
using Npgsql;
using WorkBoard.Api.Infrastructure.Identity;

namespace WorkBoard.Api.Infrastructure.Sync;

// Writes one sprint_daily row per sprint and Seoul day, for every tenant with sprints enabled, on the workboard_sync
// role (which bypasses RLS, so the statement filters on tenant_id itself). Called on every sync cycle: the row of the
// current day is overwritten with the latest numbers, rows of earlier days are never touched, and days the job missed
// stay empty.
public sealed class SprintDailyRecorder(
    [FromKeyedServices(DataKeys.Sync)] NpgsqlDataSource sync,
    TimeProvider time,
    ILogger<SprintDailyRecorder> logger)
{
    // Asia/Seoul has no daylight saving time.
    private static readonly TimeSpan SeoulOffset = TimeSpan.FromHours(9);

    public DateOnly Today => DateOnly.FromDateTime((time.GetUtcNow() + SeoulOffset).DateTime);

    public async Task RecordTodayAsync(CancellationToken ct)
    {
        try
        {
            await RecordAsync(Today, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Recording sprint daily numbers failed");
        }
    }

    // Returns the number of sprints recorded, for one tenant or (null) all. A sprint is recorded when day is within its dates and it is not stored as closed.
    public async Task<int> RecordAsync(DateOnly day, CancellationToken ct, Guid? tenantId = null)
    {
        await using var conn = await sync.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            insert into sprint_daily (tenant_id, sprint_id, day, scope_count, scope_weight, done_count, done_weight)
            select sp.tenant_id, sp.id, @day,
                   count(i.id) filter (where i.status <> 'cancelled')::int,
                   coalesce(sum(i.weight) filter (where i.status <> 'cancelled'), 0)::int,
                   count(i.id) filter (where i.status = 'done')::int,
                   coalesce(sum(i.weight) filter (where i.status = 'done'), 0)::int
              from sprints sp
              join tenant_settings ts on ts.tenant_id = sp.tenant_id and ts.sprints_enabled
              left join (select bf.tenant_id, bf.sprint_id, bf.issue_id as id, st.status,
                                case bf.size when 'M' then 2 when 'L' then 3 else 1 end as weight
                           from board_fields bf
                           join issue_status_all st on st.tenant_id = bf.tenant_id and st.issue_id = bf.issue_id) i
                     on i.tenant_id = sp.tenant_id and i.sprint_id = sp.id
             where (@tenantId::uuid is null or sp.tenant_id = @tenantId::uuid)
               and sp.state <> 'closed' and sp.start_date <= @day and sp.end_date >= @day
             group by sp.tenant_id, sp.id
            on conflict (tenant_id, sprint_id, day) do update
               set scope_count = excluded.scope_count, scope_weight = excluded.scope_weight,
                   done_count = excluded.done_count, done_weight = excluded.done_weight, recorded_at = now()
            """, new { day, tenantId }, cancellationToken: ct));
    }
}
