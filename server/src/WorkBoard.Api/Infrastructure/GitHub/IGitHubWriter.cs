namespace WorkBoard.Api.Infrastructure.GitHub;

public enum GitHubWriteErrorKind
{
    NotGranted,
    AssigneeNotCollaborator,
    InvalidRequest,
    NotFound,
    Unavailable,
}

// Messages carry the HTTP status only, never request or response data.
public sealed class GitHubWriteException(
    GitHubWriteErrorKind kind, string message, bool sideEffectPossible = false, bool noResponse = false, Exception? inner = null)
    : Exception(message, inner)
{
    public GitHubWriteErrorKind Kind { get; } = kind;

    // GitHub may have applied the change (5xx, or no answer). False when the call was rejected or never sent.
    public bool SideEffectPossible { get; } = sideEffectPossible || noResponse;

    // The request was sent but no response arrived (network error or the client's own timeout).
    public bool NoResponse { get; } = noResponse;
}

public sealed record GitHubRepoRef(long InstallationId, string Owner, string Name);

public enum IssueCloseReason
{
    Completed,
    NotPlanned,
}

// Writes through the GitHub App installation, so GitHub shows the App bot as the actor. ActorLogin is the board
// member who asked; it goes into a footer of created bodies and comments. Every call returns the GitHub response
// as the same records the sync reads, ready for MirrorWriter. Throws GitHubWriteException.
public interface IGitHubWriter
{
    // marker, when given, is appended to the body as an invisible HTML comment (see CreateMarker) so a later
    // FindCreatedIssueAsync can recognise the issue.
    Task<SourceIssue> CreateIssueAsync(
        GitHubRepoRef repo, string actorLogin, string title, string? body,
        IReadOnlyList<string> labels, IReadOnlyList<string> assignees, CancellationToken ct, string? marker = null);

    // The issue (or null) in the repository whose body contains marker, created at or after since. Reads the issue
    // list newest first, a bounded number of pages, never the search index. Throws GitHubWriteException when the
    // list cannot be read: the answer is then unknown.
    Task<SourceIssue?> FindCreatedIssueAsync(GitHubRepoRef repo, string marker, DateTimeOffset since, CancellationToken ct);

    // Replaces the assignee list.
    Task<SourceIssue> SetAssigneesAsync(GitHubRepoRef repo, int number, IReadOnlyList<string> assignees, CancellationToken ct);

    // marker works as for CreateIssueAsync, found again by FindCreatedCommentAsync.
    Task<SourceComment> AddCommentAsync(
        GitHubRepoRef repo, string actorLogin, int number, string body, CancellationToken ct, string? marker = null);

    // The comment (or null) on the issue whose body contains marker, updated at or after since and written by a bot.
    // Reads a bounded number of pages; throws GitHubWriteException when they cannot be read or do not cover since.
    Task<SourceComment?> FindCreatedCommentAsync(
        GitHubRepoRef repo, int number, string marker, DateTimeOffset since, CancellationToken ct);

    Task<SourceIssue> CloseIssueAsync(GitHubRepoRef repo, int number, IssueCloseReason reason, CancellationToken ct);

    Task<SourceIssue> ReopenIssueAsync(GitHubRepoRef repo, int number, CancellationToken ct);
}
