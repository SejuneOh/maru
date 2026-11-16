using System.Text.Json;
using WorkBoard.Api.Features.Sprints;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Issues;

// A field is changed only when its Set flag is true; a null value then clears it.
public sealed record BoardPatch(
    bool SetStatus, string? Status,
    bool SetPriority, string? Priority,
    bool SetSize, string? Size,
    bool CheckExpected, DateTime? ExpectedUpdatedAt)
{
    public static readonly string[] Statuses = ["todo", "in_progress", "in_review", "done", "cancelled"];
    public static readonly string[] Priorities = ["urgent", "high", "normal", "low"];
    public static readonly string[] Sizes = ["S", "M", "L"];

    // Null when the body is not an object with at least one field, or a value is invalid.
    public static BoardPatch? Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            BoardPatch patch = new(false, null, false, null, false, null, false, null);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                switch (p.Name.ToLowerInvariant())
                {
                    case "status":
                        if (!TryChoice(p.Value, Statuses, out var status))
                            return null;
                        patch = patch with { SetStatus = true, Status = status };
                        break;
                    case "priority":
                        if (!TryChoice(p.Value, Priorities, out var priority))
                            return null;
                        patch = patch with { SetPriority = true, Priority = priority };
                        break;
                    case "size":
                        if (!TryChoice(p.Value, Sizes, out var size))
                            return null;
                        patch = patch with { SetSize = true, Size = size };
                        break;
                    case "expectedupdatedat":
                        if (p.Value.ValueKind == JsonValueKind.Null)
                        {
                            patch = patch with { CheckExpected = true, ExpectedUpdatedAt = null };
                            break;
                        }
                        if (p.Value.ValueKind != JsonValueKind.String || !p.Value.TryGetDateTimeOffset(out var at))
                            return null;
                        patch = patch with { CheckExpected = true, ExpectedUpdatedAt = at.UtcDateTime };
                        break;
                }
            }
            return patch.SetStatus || patch.SetPriority || patch.SetSize ? patch : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryChoice(JsonElement value, string[] allowed, out string? result)
    {
        result = null;
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        result = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return result is not null && allowed.Contains(result);
    }
}

// Changes status override, priority and size of one card. Done and cancelled close the issue on GitHub; moving a
// closed issue back to an open status reopens it. GitHub is written first, with no transaction open; only after it
// succeeds are the mirror and board_fields changed, so a GitHub failure leaves the database untouched.
public sealed class BoardFieldWrites(
    ICurrentUser user, TenantDb db, IAuditLog audit, IIssueEvents events, IGitHubWriteTargets targets,
    IGitHubWriter github, MirrorWriter mirror, ILogger<BoardFieldWrites> log)
{
    private sealed record Current(
        Guid RepositoryId, int Number, string State, string? StateReason, bool Archived, bool PriorityEnabled, bool SizeEnabled);

    private sealed record Board(string? StatusOverride, string? Priority, string? Size, DateTime? UpdatedAt);

    public Task<IdempotentResponse> ChangeAsync(Guid issueId, BoardPatch patch, string? key, string hash, CancellationToken ct) =>
        Idempotency.RunExternalAsync(db, user, key, hash, c => RunAsync(issueId, patch, c), ct, log);

    private static ExternalOutcome Fail(int status, string code, bool sideEffect = false) =>
        new(new WriteResult(status, new ErrorBody(code)), sideEffect);

    private async Task<ExternalOutcome> RunAsync(Guid issueId, BoardPatch patch, CancellationToken ct)
    {
        var (current, board) = await db.ReadAsync(async s => (
            await s.QuerySingleOrDefaultAsync<Current>("""
                select i.repository_id, i.number, i.state, i.state_reason, r.archived,
                       coalesce(ts.priority_enabled, false) as priority_enabled,
                       coalesce(ts.size_enabled, false) as size_enabled
                  from issues i
                  join repositories r on r.id = i.repository_id
                  left join tenant_settings ts on ts.tenant_id = i.tenant_id
                 where i.id = @issueId
                """, new { issueId }),
            await ReadBoardAsync(s, issueId)), ct);
        if (current is null)
            return Fail(StatusCodes.Status404NotFound, ApiErrors.NotFound);
        if ((patch.SetPriority && !current.PriorityEnabled) || (patch.SetSize && !current.SizeEnabled))
            return Fail(StatusCodes.Status409Conflict, ApiErrors.FieldDisabled);
        if (IsStale(patch, board))
            return Fail(StatusCodes.Status409Conflict, ApiErrors.Conflict);

        IssueStateChange? change = null;
        var closedAsCancelled = current.State == "closed" && current.StateReason == "not_planned";
        if (patch.Status is "done" or "cancelled"
            && (current.State == "open" || closedAsCancelled != (patch.Status == "cancelled")))
            change = IssueStateChange.Closed;
        else if (patch.Status is "todo" or "in_progress" or "in_review" && current.State == "closed")
            change = IssueStateChange.Reopened;

        if (change is not null)
        {
            var target = await targets.ResolveAsync(current.RepositoryId, ct);
            if (target is null)
                return Fail(StatusCodes.Status404NotFound, ApiErrors.NotFound);
            if (target.Archived)
                return Fail(StatusCodes.Status409Conflict, ApiErrors.RepositoryArchived);

            SourceIssue written;
            try
            {
                written = change == IssueStateChange.Closed
                    ? await github.CloseIssueAsync(target.Repo, current.Number,
                        patch.Status == "cancelled" ? IssueCloseReason.NotPlanned : IssueCloseReason.Completed, ct)
                    : await github.ReopenIssueAsync(target.Repo, current.Number, ct);
            }
            catch (GitHubWriteException e) when (!e.NoResponse)
            {
                return GitHubWriteErrors.ToOutcome(e);
            }

            var mirrored = await mirror.ApplyWrittenIssueAsync(
                user.Identity.TenantId, current.RepositoryId, written, user.Identity.Member.Id, change, CancellationToken.None);
            if (mirrored is null)
                log.LogWarning("Issue {IssueId} was written on GitHub but its repository is not in the mirror", issueId);
        }

        return await db.WriteAsync(s => ApplyBoardAsync(s, issueId, patch, change), CancellationToken.None);
    }

    private static bool IsStale(BoardPatch patch, Board? board) =>
        patch.CheckExpected && patch.ExpectedUpdatedAt != board?.UpdatedAt;

    private static Task<Board?> ReadBoardAsync(TenantSession s, Guid issueId) =>
        s.QuerySingleOrDefaultAsync<Board>(
            "select status_override, priority, size, updated_at from board_fields where issue_id = @issueId", new { issueId });

    // Runs under the assignment lock and re-reads the row, so from-values see the latest write. The concurrency
    // check happens here only when GitHub was not called; after a GitHub write the change is always applied.
    private async Task<ExternalOutcome> ApplyBoardAsync(TenantSession s, Guid issueId, BoardPatch patch, IssueStateChange? change)
    {
        await SprintWrites.LockAssignmentsAsync(s, user.Identity.TenantId);
        var board = await ReadBoardAsync(s, issueId);
        var githubCalled = change is not null;
        if (!githubCalled && IsStale(patch, board))
            return Fail(StatusCodes.Status409Conflict, ApiErrors.Conflict);

        var newOverride = patch.Status is "todo" or "in_progress" or "in_review" ? patch.Status : null;
        var setStatus = patch.SetStatus && newOverride != board?.StatusOverride;
        var setPriority = patch.SetPriority && patch.Priority != board?.Priority;
        var setSize = patch.SetSize && patch.Size != board?.Size;

        if (setStatus || setPriority || setSize)
        {
            await s.ExecuteAsync("""
                insert into board_fields (issue_id, tenant_id, status_override, priority, size)
                values (@issueId, @tenantId, @status, @priority, @size)
                on conflict (issue_id) do update set
                    status_override = case when @setStatus then excluded.status_override else board_fields.status_override end,
                    priority = case when @setPriority then excluded.priority else board_fields.priority end,
                    size = case when @setSize then excluded.size else board_fields.size end,
                    updated_at = now()
                """, new
            {
                issueId, tenantId = user.Identity.TenantId,
                status = setStatus ? newOverride : null,
                priority = setPriority ? patch.Priority : null,
                size = setSize ? patch.Size : null,
                setStatus, setPriority, setSize,
            });
            if (setStatus)
                await events.RecordAsync(s, new IssueEvent(issueId,
                    newOverride is null ? IssueEventKind.StatusCleared : IssueEventKind.StatusSet,
                    board?.StatusOverride, newOverride));
            if (setPriority)
                await events.RecordAsync(s, new IssueEvent(issueId, IssueEventKind.PrioritySet, board?.Priority, patch.Priority));
            if (setSize)
                await events.RecordAsync(s, new IssueEvent(issueId, IssueEventKind.SizeSet, board?.Size, patch.Size));
        }

        if (githubCalled || setStatus || setPriority || setSize)
            await audit.WriteAsync(s, "issue.board_update", "issue", issueId,
                new { statusOverride = board?.StatusOverride, priority = board?.Priority, size = board?.Size },
                new
                {
                    statusOverride = setStatus ? newOverride : board?.StatusOverride,
                    priority = setPriority ? patch.Priority : board?.Priority,
                    size = setSize ? patch.Size : board?.Size,
                    github = change?.ToString().ToLowerInvariant(),
                });

        return new ExternalOutcome(
            new WriteResult(StatusCodes.Status200OK, await IssueQueries.GetAsync(s, issueId)), githubCalled);
    }
}
