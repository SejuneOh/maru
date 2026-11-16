using System.Text.Json;
using WorkBoard.Api.Infrastructure.Data;
using WorkBoard.Api.Infrastructure.GitHub;
using WorkBoard.Api.Infrastructure.Identity;
using WorkBoard.Api.Infrastructure.Sync;
using WorkBoard.Api.Infrastructure.Writes;

namespace WorkBoard.Api.Features.Issues;

public sealed record AddCommentRequest(string? Body);

// 201 body. Id is the mirrored comment, null when the mirror could not store it yet (MirrorPending): the comment is on
// GitHub and the next sync brings it.
public sealed record PostedComment(
    Guid? Id, CommentAuthor Author, string Body, DateTime CreatedAt, string? HtmlUrl, bool MirrorPending);

// Posts one comment through the App (bot author, footer naming the caller). Not safe to repeat, so every comment
// carries a marker derived from the Idempotency-Key; a retry that takes over a stale reservation looks for it before
// posting again. Order: GitHub, then the mirror and the audit row; nothing local is written when GitHub refuses.
public sealed class CommentWrites(
    ICurrentUser user, TenantDb db, IAuditLog audit, IGitHubWriteTargets targets, IGitHubWriter github, MirrorWriter mirror,
    ILogger<CommentWrites> log)
{
    public const int BodyMaxLength = 60_000;

    private sealed record Current(Guid RepositoryId, int Number);

    // Null when the body is not an object with a non-blank body of at most BodyMaxLength characters.
    public static string? Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var body = JsonSerializer.Deserialize<AddCommentRequest>(bytes, JsonSerializerOptions.Web)?.Body?.Trim();
            return string.IsNullOrEmpty(body) || body.Length > BodyMaxLength ? null : body;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<IdempotentResponse> AddAsync(Guid issueId, string body, string key, string requestHash, CancellationToken ct) =>
        Idempotency.RunExternalAsync(db, user, key, requestHash, async (callCt, attempt) =>
        {
            var current = await db.ReadAsync(s => s.QuerySingleOrDefaultAsync<Current>(
                "select repository_id, number from issues where id = @issueId", new { issueId }), callCt);
            if (current is null)
                return Fail(StatusCodes.Status404NotFound, ApiErrors.NotFound);
            var target = await targets.ResolveAsync(current.RepositoryId, callCt);
            if (target is null)
                return Fail(StatusCodes.Status404NotFound, ApiErrors.NotFound);
            if (target.Archived)
                return Fail(StatusCodes.Status409Conflict, ApiErrors.RepositoryArchived);

            var marker = GitHubWriter.CommentMarker(IssueCreation.MarkerId(user.Identity.TenantId, key, requestHash));
            SourceComment? posted = null;
            if (attempt.TakenOverFrom is { } since)
            {
                try
                {
                    posted = await github.FindCreatedCommentAsync(
                        target.Repo, current.Number, marker, since - IssueCreation.LookupMargin, callCt);
                }
                catch (GitHubWriteException e)
                {
                    // Unknown whether the earlier attempt posted: posting now could duplicate it.
                    throw new GitHubWriteException(GitHubWriteErrorKind.Unavailable, "Lookup of an earlier comment failed.",
                        noResponse: true, inner: e);
                }
            }

            if (posted is null)
                try
                {
                    posted = await github.AddCommentAsync(target.Repo, user.Identity.Member.GithubLogin, current.Number, body, callCt, marker);
                }
                catch (GitHubWriteException e) when (!e.NoResponse)
                {
                    return GitHubWriteErrors.ToOutcome(e);
                }

            // GitHub has the comment from here on, so a local failure still completes the key.
            var author = new CommentAuthor(user.Identity.Member.GithubLogin, user.Identity.Member.DisplayName);
            Guid? id = null;
            try
            {
                await mirror.ApplyWrittenCommentAsync(user.Identity.TenantId, current.RepositoryId, posted, CancellationToken.None);
                id = await db.WriteAsync(async s =>
                {
                    await audit.WriteAsync(s, "issue.comment", "issue", issueId, null,
                        new { githubCommentId = posted.Id, number = current.Number });
                    return await s.QuerySingleOrDefaultAsync<Guid?>(
                        "select id from comments where github_comment_id = @githubId", new { githubId = posted.Id });
                }, CancellationToken.None);
            }
            catch (Exception e)
            {
                log.LogError(e, "Comment {CommentId} was posted on GitHub but saving it locally failed", posted.Id);
            }

            return new ExternalOutcome(new WriteResult(StatusCodes.Status201Created,
                new PostedComment(id, author, body, posted.CreatedAt.UtcDateTime, posted.HtmlUrl, id is null)), true);
        }, ct, log);

    private static ExternalOutcome Fail(int status, string code) => new(new WriteResult(status, new ErrorBody(code)), false);
}
