using WorkBoard.Api.Features.Members;

namespace WorkBoard.Api.Features.MeetingNotes;

public sealed record ProjectRef(Guid Id, string Name);

public sealed record MeetingNoteSummary(
    Guid Id,
    string Title,
    DateTime HeldAt,
    ProjectRef? Project,
    int ActionItemCount,
    int LinkedActionItemCount);

// State is the mirrored GitHub state (open, closed); Status the derived board status.
public sealed record ActionItemIssue(Guid Id, int Number, string RepositoryName, string Status, string State, string? HtmlUrl);

// An issue GitHub created for the item that is not in the mirror yet.
public sealed record ActionItemPendingIssue(string RepositoryName, int Number);

// CreatingIssue: a request is creating the issue right now. Items with any of Issue, PendingIssue, CreatingIssue
// cannot be removed from the note.
public sealed record ActionItemDto(
    Guid Id, string Text, Member? Assignee, DateOnly? DueDate, ActionItemIssue? Issue,
    ActionItemPendingIssue? PendingIssue = null, bool CreatingIssue = false);

public sealed record MeetingNote(
    Guid Id,
    string Title,
    DateTime HeldAt,
    string BodyMd,
    ProjectRef? Project,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<Member> Attendees,
    IReadOnlyList<ActionItemDto> ActionItems,
    Member? CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

// Id is null for a new item; an existing item keeps its id (and its issue link) across edits.
public sealed record ActionItemInput(Guid? Id, string? Text, Guid? AssigneeId, DateOnly? DueDate);

// POST and PATCH share this body. PATCH replaces the whole note, so every list must be sent.
// Id is POST only: the id the client chose for the new note, so a resent POST cannot create a second note.
public sealed record MeetingNoteInput(
    string? Title,
    DateTimeOffset? HeldAt,
    Guid? ProjectId,
    string? BodyMd,
    string[]? Decisions,
    Guid[]? AttendeeIds,
    ActionItemInput[]? ActionItems,
    DateTimeOffset? ExpectedUpdatedAt,
    Guid? Id = null);

// A validated MeetingNoteInput: trimmed texts, distinct attendees, items in display order.
public sealed record MeetingNoteValues(
    string Title,
    DateTimeOffset HeldAt,
    Guid? ProjectId,
    string BodyMd,
    string[] Decisions,
    Guid[] AttendeeIds,
    ActionItemValues[] ActionItems);

public sealed record ActionItemValues(Guid? Id, string Text, Guid? AssigneeId, DateOnly? DueDate);
