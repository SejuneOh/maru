using WorkBoard.Api.Infrastructure.Data;

namespace WorkBoard.Api.Features.Issues;

public static class IssueDetailQueries
{
    public static async Task<IssueDetail?> GetAsync(TenantSession s, Guid id)
    {
        var issue = await IssueQueries.GetAsync(s, id);
        if (issue is null)
            return null;

        var bodyRow = await s.QuerySingleOrDefaultAsync<BodyRow>("""
            select i.body, gu.login as author_login
              from issues i
              left join github_users gu on gu.github_user_id = i.author_github_id
             where i.id = @id
            """, new { id });
        var body = bodyRow?.Body is { } raw && BoardFooter.IsBot(bodyRow.AuthorLogin) && BoardFooter.Strip(raw) is { } own
            ? own.Body
            : bodyRow?.Body;

        var parent = issue.ParentIssueId is { } parentId
            ? await s.QuerySingleOrDefaultAsync<ParentRow>(
                "select id, number, title from issues where id = @parentId", new { parentId })
            : null;

        var children = await s.QueryAsync<ChildRow>("""
            select c.id, r.name as repository_name, c.number, c.title, st.status
              from issues c
              join repositories r on r.id = c.repository_id
              join issue_status st on st.issue_id = c.id
             where c.parent_issue_id = @id
             order by r.name, c.number
            """, new { id });

        var pullRequests = await s.QueryAsync<PullRequestRow>("""
            select p.id, p.number, p.title, p.state, p.draft, p.review_requested, p.html_url
              from issue_pull_requests ipr
              join pull_requests p on p.id = ipr.pull_request_id
             where ipr.issue_id = @id
             order by p.number
            """, new { id });

        var comments = await s.QueryAsync<CommentRow>("""
            select c.id, m.github_login, m.display_name, gu.login as author_login, c.body, c.github_created_at as created_at
              from comments c
              left join members m on m.github_user_id = c.author_github_id
              left join github_users gu on gu.github_user_id = c.author_github_id
             where c.issue_id = @id
             order by c.github_created_at, c.id
            """, new { id });

        var meetingNotes = await s.QueryAsync<NoteRow>("""
            select n.id, n.title, n.held_at
              from meeting_notes n
             where exists (select 1 from meeting_action_items a where a.note_id = n.id and a.issue_id = @id)
             order by n.held_at desc
            """, new { id });

        // Sync records closed/reopened without a member, so a member marks a change made on the board.
        var boardChange = await s.QuerySingleOrDefaultAsync<BoardChangeRow>("""
            select e.at, m.github_login, m.display_name
              from issue_events e
              join members m on m.id = e.member_id
             where e.issue_id = @id
               and e.kind in ('status_set', 'status_cleared', 'priority_set', 'size_set', 'closed', 'reopened')
             order by e.at desc, e.id
             limit 1
            """, new { id });

        return new IssueDetail(issue)
        {
            Body = body,
            Parent = parent is null ? null : new ParentRef(parent.Id, parent.Number, parent.Title),
            Children = children.Select(c => new ChildIssue(c.Id, c.RepositoryName, c.Number, c.Title, c.Status)).ToList(),
            PullRequests = pullRequests
                .Select(p => new PullRequestDto(p.Id, p.Number, p.Title, p.State, p.Draft, p.ReviewRequested, p.HtmlUrl))
                .ToList(),
            Comments = await CommentsAsync(s, comments),
            MeetingNotes = meetingNotes.Select(n => new LinkedMeetingNote(n.Id, n.Title, n.HeldAt)).ToList(),
            LastBoardChange = boardChange is null
                ? null
                : new BoardChange(boardChange.At, new BoardChangeBy(boardChange.GithubLogin, boardChange.DisplayName)),
        };
    }

    // A comment the App wrote for a member is shown as that member's, without the footer.
    private static async Task<List<CommentDto>> CommentsAsync(TenantSession s, IEnumerable<CommentRow> rows)
    {
        var all = rows
            .Select(r => (Row: r, Own: r.GithubLogin is null && BoardFooter.IsBot(r.AuthorLogin) ? BoardFooter.Strip(r.Body) : null))
            .ToList();
        var logins = all.Where(b => b.Own is not null).Select(b => b.Own!.Value.Login.ToLowerInvariant()).Distinct().ToArray();
        var names = logins.Length == 0
            ? []
            : (await s.QueryAsync<MemberName>(
                    "select github_login, display_name from members where lower(github_login) = any(@logins)", new { logins }))
                .ToDictionary(m => m.GithubLogin.ToLowerInvariant(), m => m.DisplayName);
        return all.Select(b => b.Own is { } own
                ? new CommentDto(b.Row.Id, new CommentAuthor(own.Login, names.GetValueOrDefault(own.Login.ToLowerInvariant())),
                    own.Body, b.Row.CreatedAt)
                : new CommentDto(b.Row.Id, new CommentAuthor(b.Row.GithubLogin, b.Row.DisplayName), b.Row.Body, b.Row.CreatedAt))
            .ToList();
    }

    private sealed class BodyRow
    {
        public string? Body { get; set; }
        public string? AuthorLogin { get; set; }
    }

    private sealed class MemberName
    {
        public string GithubLogin { get; set; } = "";
        public string? DisplayName { get; set; }
    }

    private sealed class ParentRow
    {
        public Guid Id { get; set; }
        public int Number { get; set; }
        public string Title { get; set; } = "";
    }

    private sealed class ChildRow
    {
        public Guid Id { get; set; }
        public string RepositoryName { get; set; } = "";
        public int Number { get; set; }
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private sealed class PullRequestRow
    {
        public Guid Id { get; set; }
        public int Number { get; set; }
        public string Title { get; set; } = "";
        public string State { get; set; } = "";
        public bool Draft { get; set; }
        public bool ReviewRequested { get; set; }
        public string? HtmlUrl { get; set; }
    }

    private sealed class CommentRow
    {
        public Guid Id { get; set; }
        public string? GithubLogin { get; set; }
        public string? DisplayName { get; set; }
        public string? AuthorLogin { get; set; }
        public string Body { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    private sealed class BoardChangeRow
    {
        public DateTime At { get; set; }
        public string? GithubLogin { get; set; }
        public string? DisplayName { get; set; }
    }

    private sealed class NoteRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public DateTime HeldAt { get; set; }
    }
}
