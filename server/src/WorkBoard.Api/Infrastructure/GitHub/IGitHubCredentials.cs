namespace WorkBoard.Api.Infrastructure.GitHub;

public enum GitHubAuthKind
{
    App,
    Token,
}

// Identifies the connection a GitHub call runs for. InstallationId is set for App connections. AccountLogin is the
// connected account; TokenOwnerLogin is the GitHub login the stored token writes as (token connections only).
public sealed record GitHubConnectionRef(
    Guid TenantId, Guid ConnectionId, GitHubAuthKind Kind, long? InstallationId,
    string? AccountLogin = null, string? TokenOwnerLogin = null);

public sealed record GitHubCredential(string Token, GitHubAuthKind Kind);

// The token to call GitHub with for a connection: an installation token for App connections, the decrypted
// stored token for token connections. Throws GitHubSourceException when there is none.
public interface IGitHubCredentials
{
    Task<GitHubCredential> GetAsync(GitHubConnectionRef connection, CancellationToken ct);

    // Drops a cached token, e.g. after GitHub answered 401 for it.
    void Invalidate(GitHubConnectionRef connection);

    // GitHub answered 401 for a token connection's token: marks the stored credential revoked (only while it still
    // holds that token). Does nothing for App connections.
    Task ReportRejectedAsync(GitHubConnectionRef connection, string token, CancellationToken ct) => Task.CompletedTask;
}
