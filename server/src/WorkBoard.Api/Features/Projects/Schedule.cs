using WorkBoard.Api.Features.Overview;
using WorkBoard.Api.Features.Sprints;
using WorkBoard.Api.Infrastructure.Data;

namespace WorkBoard.Api.Features.Projects;

/// <summary>Dates are yyyy-MM-dd in Asia/Seoul. <c>OverdueDays</c> is 0 unless the row is past its date and not finished.</summary>
public sealed record ScheduleMilestone(
    Guid Id, RepositoryRef Repository, int Number, string Title, string State, string? DueDate, string? HtmlUrl,
    int IssueCount, int DoneCount, int OverdueDays);

/// <summary>Counts cover only this project's issues in the sprint.</summary>
public sealed record ScheduleSprint(
    Guid Id, string Name, string StartDate, string EndDate, string State,
    int IssueCount, int DoneCount, int UnfinishedCount, int OverdueDays);

public sealed record ProjectSchedule(
    Guid ProjectId, string Today, bool SprintsEnabled,
    IReadOnlyList<ScheduleMilestone> Milestones, IReadOnlyList<ScheduleSprint> Sprints);

public static class ScheduleQuery
{
    /// <summary>Closed milestones and ended sprints stay on the schedule this many days.</summary>
    public const int RecentDays = 60;

    private static readonly string Today = $"(now() at time zone '{OverviewQueries.TimeZone}')::date";

    public static async Task<ProjectSchedule?> BuildAsync(TenantSession s, Guid projectId)
    {
        var today = await s.QuerySingleOrDefaultAsync<string>($"""
            select {Today}::text from projects where id = @projectId
            """, new { projectId });
        if (today is null)
            return null;

        // A project's milestones are those of its repositories.
        var milestones = await s.QueryAsync<MilestoneRow>($"""
            select m.id, m.repository_id, r.name as repository_name, m.number, m.title, m.state,
                   (m.due_on at time zone '{OverviewQueries.TimeZone}')::date::text as due_date, m.html_url,
                   count(i.id) filter (where st.status <> 'cancelled')::int as issue_count,
                   count(i.id) filter (where st.status = 'done')::int as done_count,
                   case when m.state = 'open' and (m.due_on at time zone '{OverviewQueries.TimeZone}')::date < {Today}
                        then {Today} - (m.due_on at time zone '{OverviewQueries.TimeZone}')::date else 0 end as overdue_days
              from project_repositories pr
              join milestones m on m.repository_id = pr.repository_id
              join repositories r on r.id = m.repository_id
              left join issues i on i.milestone_id = m.id
              left join issue_status st on st.issue_id = i.id
             where pr.project_id = @projectId
               and (m.state = 'open'
                    or coalesce(m.closed_at, m.due_on) >= now() - make_interval(days => @recent))
             group by m.id, r.name
             order by m.due_on nulls last, m.number, m.id
            """, new { projectId, recent = RecentDays });

        var enabled = await SprintQueries.EnabledAsync(s);
        var sprints = enabled
            ? await s.QueryAsync<SprintRow>($"""
                with project_issues as (
                    select bf.sprint_id, st.status
                      from board_fields bf
                      join issues i on i.id = bf.issue_id
                      join project_repositories pr on pr.repository_id = i.repository_id and pr.project_id = @projectId
                      join issue_status st on st.issue_id = i.id
                     where bf.sprint_id is not null
                ), counted as (
                    select sp.id, sp.name, sp.start_date, sp.end_date, sp.state as stored_state,
                           count(pi.sprint_id) filter (where pi.status <> 'cancelled')::int as issue_count,
                           count(pi.sprint_id) filter (where pi.status = 'done')::int as done_count,
                           count(pi.sprint_id) filter (where pi.status not in ('done', 'cancelled'))::int as unfinished_count
                      from sprints sp
                      left join project_issues pi on pi.sprint_id = sp.id
                     group by sp.id
                )
                select id, name, start_date::text as start_date, end_date::text as end_date,
                       case when stored_state = 'closed' or end_date < {Today} then 'closed'
                            when start_date > {Today} then 'planned'
                            else 'active' end as state,
                       issue_count, done_count, unfinished_count,
                       case when end_date < {Today} and unfinished_count > 0 then {Today} - end_date else 0 end as overdue_days
                  from counted
                 where (stored_state <> 'closed' and end_date >= {Today})
                    or unfinished_count > 0
                    or (issue_count > 0 and end_date >= {Today} - @recent)
                 order by start_date, id
                """, new { projectId, recent = RecentDays })
            : [];

        return new ProjectSchedule(
            projectId, today, enabled,
            milestones.Select(m => new ScheduleMilestone(
                m.Id, new RepositoryRef(m.RepositoryId, m.RepositoryName), m.Number, m.Title, m.State, m.DueDate, m.HtmlUrl,
                m.IssueCount, m.DoneCount, m.OverdueDays)).ToList(),
            sprints.Select(r => new ScheduleSprint(
                r.Id, r.Name, r.StartDate, r.EndDate, r.State, r.IssueCount, r.DoneCount, r.UnfinishedCount, r.OverdueDays)).ToList());
    }

    private sealed class MilestoneRow
    {
        public Guid Id { get; set; }
        public Guid RepositoryId { get; set; }
        public string RepositoryName { get; set; } = "";
        public int Number { get; set; }
        public string Title { get; set; } = "";
        public string State { get; set; } = "";
        public string? DueDate { get; set; }
        public string? HtmlUrl { get; set; }
        public int IssueCount { get; set; }
        public int DoneCount { get; set; }
        public int OverdueDays { get; set; }
    }

    private sealed class SprintRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string StartDate { get; set; } = "";
        public string EndDate { get; set; } = "";
        public string State { get; set; } = "";
        public int IssueCount { get; set; }
        public int DoneCount { get; set; }
        public int UnfinishedCount { get; set; }
        public int OverdueDays { get; set; }
    }
}
