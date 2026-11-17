using System.Globalization;
using WorkBoard.Api.Infrastructure.Data;

namespace WorkBoard.Api.Features.Projects;

public sealed record ProjectWeeks(Guid ProjectId, IReadOnlyList<int> Done);

/// <summary>Weeks are Monday dates (yyyy-MM-dd) in Asia/Seoul, oldest first; the last one is the current week.</summary>
public sealed record DoneByWeek(IReadOnlyList<string> Weeks, IReadOnlyList<ProjectWeeks> Projects);

public static class DoneByWeekQuery
{
    public const int Weeks = 8;

    // Done = closed and not not_planned, as in issue_status. closed_at is the latest close,
    // so an issue reopened and closed again counts once, in the week of that close.
    public static async Task<DoneByWeek> BuildAsync(TenantSession s)
    {
        var current = DateOnly.ParseExact(await s.QuerySingleOrDefaultAsync<string>(
            "select date_trunc('week', now() at time zone 'Asia/Seoul')::date::text") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var weeks = Enumerable.Range(0, Weeks)
            .Select(i => current.AddDays(-7 * (Weeks - 1 - i)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();

        var projects = await s.QueryAsync<Guid>("select id from projects order by name, id");
        var rows = await s.QueryAsync<Row>("""
            select pr.project_id,
                   date_trunc('week', i.closed_at at time zone 'Asia/Seoul')::date::text as week,
                   count(distinct i.id)::int as done
              from project_repositories pr
              join issues i on i.repository_id = pr.repository_id
             where i.state = 'closed'
               and i.state_reason is distinct from 'not_planned'
               and i.closed_at >= (cast(@from as date)::timestamp at time zone 'Asia/Seoul')
             group by 1, 2
            """, new { from = weeks[0] });

        var counts = rows.ToDictionary(r => (r.ProjectId, r.Week), r => r.Done);
        return new DoneByWeek(weeks,
            projects.Select(p => new ProjectWeeks(p, weeks.Select(w => counts.GetValueOrDefault((p, w))).ToList())).ToList());
    }

    private sealed class Row
    {
        public Guid ProjectId { get; set; }
        public string Week { get; set; } = "";
        public int Done { get; set; }
    }
}
