using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.MeetingNotes;

// Every method runs on the caller's tenant transaction (inside Idempotency.RunAsync): the note, its attendees,
// decisions, action items and the audit row commit together. Non-2xx results are returned before anything is written.
public sealed class MeetingNoteWrites(ICurrentUser user, IAuditLog audit)
{
    public const string ActionItemLinked = "action_item_linked";

    private sealed record NoteHead(Guid Id, Guid? CreatedBy, DateTime UpdatedAt);

    private sealed record ExistingItem(Guid Id, Guid? IssueId);

    private static WriteResult Error(int status, string code) => new(status, new ErrorBody(code));

    private static WriteResult Invalid() => Error(StatusCodes.Status400BadRequest, ApiErrors.InvalidRequest);

    private static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd");

    public async Task<WriteResult> CreateAsync(TenantSession s, MeetingNoteValues v)
    {
        if (!await ReferencesExistAsync(s, v))
            return Invalid();

        var id = await s.QuerySingleOrDefaultAsync<Guid>("""
            insert into meeting_notes (tenant_id, project_id, title, held_at, body_md, decisions, created_by)
            values (@tenantId, @projectId, @title, @heldAt, @bodyMd, @decisions, @memberId)
            returning id
            """, new
        {
            tenantId = user.Identity.TenantId, projectId = v.ProjectId, title = v.Title, heldAt = v.HeldAt.UtcDateTime,
            bodyMd = v.BodyMd, decisions = v.Decisions, memberId = user.Identity.Member.Id,
        });
        await AddAttendeesAsync(s, id, v.AttendeeIds);
        for (var i = 0; i < v.ActionItems.Length; i++)
            await InsertItemAsync(s, id, v.ActionItems[i], i);

        var note = (await MeetingNotesEndpoints.GetAsync(s, id))!;
        await audit.WriteAsync(s, "meeting_note.create", "meeting_note", id, null, Snapshot(note));
        return new WriteResult(StatusCodes.Status201Created, note);
    }

    public async Task<WriteResult> UpdateAsync(TenantSession s, Guid id, MeetingNoteValues v, DateTimeOffset expectedUpdatedAt)
    {
        var head = await LockAsync(s, id);
        if (head is null)
            return Error(StatusCodes.Status404NotFound, ApiErrors.NotFound);
        if (head.UpdatedAt != expectedUpdatedAt.UtcDateTime)
            return Error(StatusCodes.Status409Conflict, ApiErrors.Conflict);
        if (!await ReferencesExistAsync(s, v))
            return Invalid();

        var existing = await s.QueryAsync<ExistingItem>(
            "select id, issue_id from meeting_action_items where note_id = @id", new { id });
        var existingIds = existing.Select(e => e.Id).ToHashSet();
        if (v.ActionItems.Any(i => i.Id is { } itemId && !existingIds.Contains(itemId)))
            return Invalid();
        var kept = v.ActionItems.Where(i => i.Id is not null).Select(i => i.Id!.Value).ToHashSet();
        var removed = existing.Where(e => !kept.Contains(e.Id)).ToList();
        var linked = removed.Where(e => e.IssueId is not null).Select(e => e.Id).Order().ToArray();
        if (linked.Length > 0)
            return new WriteResult(StatusCodes.Status409Conflict, new { error = ActionItemLinked, actionItemIds = linked });

        var before = Snapshot((await MeetingNotesEndpoints.GetAsync(s, id))!);

        await s.ExecuteAsync("""
            update meeting_notes
               set title = @title, held_at = @heldAt, project_id = @projectId, body_md = @bodyMd,
                   decisions = @decisions, updated_at = now()
             where id = @id
            """, new { id, title = v.Title, heldAt = v.HeldAt.UtcDateTime, projectId = v.ProjectId, bodyMd = v.BodyMd, decisions = v.Decisions });

        await s.ExecuteAsync(
            "delete from meeting_attendees where note_id = @id and not (member_id = any(@ids))", new { id, ids = v.AttendeeIds });
        await AddAttendeesAsync(s, id, v.AttendeeIds);

        if (removed.Count > 0)
            await s.ExecuteAsync("delete from meeting_action_items where id = any(@ids)",
                new { ids = removed.Select(r => r.Id).ToArray() });
        for (var i = 0; i < v.ActionItems.Length; i++)
        {
            var item = v.ActionItems[i];
            if (item.Id is { } itemId)
                await s.ExecuteAsync("""
                    update meeting_action_items
                       set text = @text, assignee_member_id = @assigneeId, due_date = cast(@due as date), position = @position
                     where id = @itemId
                    """, new { itemId, text = item.Text, assigneeId = item.AssigneeId, due = Iso(item.DueDate), position = i });
            else
                await InsertItemAsync(s, id, item, i);
        }

        var note = (await MeetingNotesEndpoints.GetAsync(s, id))!;
        await audit.WriteAsync(s, "meeting_note.update", "meeting_note", id, before, Snapshot(note));
        return new WriteResult(StatusCodes.Status200OK, note);
    }

    public async Task<WriteResult> DeleteAsync(TenantSession s, Guid id)
    {
        var head = await LockAsync(s, id);
        if (head is null)
            return Error(StatusCodes.Status404NotFound, ApiErrors.NotFound);
        var me = user.Identity.Member;
        if (head.CreatedBy != me.Id && me.Role != "admin")
            return Error(StatusCodes.Status403Forbidden, ApiErrors.Forbidden);

        var before = Snapshot((await MeetingNotesEndpoints.GetAsync(s, id))!);
        await s.ExecuteAsync("delete from meeting_notes where id = @id", new { id });
        await audit.WriteAsync(s, "meeting_note.delete", "meeting_note", id, before, null);
        return new WriteResult(StatusCodes.Status204NoContent);
    }

    private static Task<NoteHead?> LockAsync(TenantSession s, Guid id) =>
        s.QuerySingleOrDefaultAsync<NoteHead>(
            "select id, created_by, updated_at from meeting_notes where id = @id for update", new { id });

    // Row security hides other tenants' rows, so a count short of the ids means an id outside the caller's tenant.
    private static async Task<bool> ReferencesExistAsync(TenantSession s, MeetingNoteValues v)
    {
        var memberIds = v.AttendeeIds
            .Concat(v.ActionItems.Where(i => i.AssigneeId is not null).Select(i => i.AssigneeId!.Value))
            .Distinct().ToArray();
        if (memberIds.Length > 0)
        {
            var found = await s.QuerySingleOrDefaultAsync<int>(
                "select count(*)::int from members where id = any(@memberIds)", new { memberIds });
            if (found != memberIds.Length)
                return false;
        }
        return v.ProjectId is not { } projectId
            || await s.QuerySingleOrDefaultAsync<bool>("select exists (select 1 from projects where id = @projectId)", new { projectId });
    }

    private Task AddAttendeesAsync(TenantSession s, Guid noteId, Guid[] memberIds) =>
        memberIds.Length == 0
            ? Task.CompletedTask
            : s.ExecuteAsync("""
                insert into meeting_attendees (tenant_id, note_id, member_id)
                select @tenantId, @noteId, unnest(@memberIds)
                on conflict do nothing
                """, new { tenantId = user.Identity.TenantId, noteId, memberIds });

    private Task InsertItemAsync(TenantSession s, Guid noteId, ActionItemValues item, int position) =>
        s.ExecuteAsync("""
            insert into meeting_action_items (tenant_id, note_id, text, assignee_member_id, due_date, position)
            values (@tenantId, @noteId, @text, @assigneeId, cast(@due as date), @position)
            """, new
        {
            tenantId = user.Identity.TenantId, noteId, text = item.Text, assigneeId = item.AssigneeId,
            due = Iso(item.DueDate), position,
        });

    private static object Snapshot(MeetingNote n) => new
    {
        n.Title,
        n.HeldAt,
        projectId = n.Project?.Id,
        bodyLength = n.BodyMd.Length,
        n.Decisions,
        attendeeIds = n.Attendees.Select(a => a.Id),
        actionItems = n.ActionItems.Select(a => new { a.Id, a.Text, assigneeId = a.Assignee?.Id, a.DueDate, issueId = a.Issue?.Id }),
    };
}
