using WorkBoard.Api.Infrastructure.Credentials;

namespace WorkBoard.Api.Infrastructure.GitHub;

public sealed class GitHubCredentials(IGitHubInstallationTokens installationTokens, CredentialStore store) : IGitHubCredentials
{
    public async Task<GitHubCredential> GetAsync(GitHubConnectionRef connection, CancellationToken ct)
    {
        if (connection.Kind == GitHubAuthKind.App)
        {
            var installationId = connection.InstallationId
                ?? throw new GitHubSourceException(GitHubSourceErrorKind.Unauthorized, "The connection has no App installation.");
            return new GitHubCredential(await installationTokens.GetAsync(installationId, ct), GitHubAuthKind.App);
        }

        try
        {
            var token = await store.OpenAsync(connection.TenantId, connection.ConnectionId, ct);
            if (token is not null)
                return new GitHubCredential(token, GitHubAuthKind.Token);

            var state = await store.GetStateAsync(connection.TenantId, connection.ConnectionId, ct);
            throw state?.Status switch
            {
                "expired" => new GitHubSourceException(GitHubSourceErrorKind.CredentialExpired, "The stored token has expired."),
                "revoked" or "invalid" => new GitHubSourceException(GitHubSourceErrorKind.CredentialRevoked, "The stored token was rejected by GitHub."),
                _ => new GitHubSourceException(GitHubSourceErrorKind.Unauthorized, "The connection has no usable credential."),
            };
        }
        catch (Exception e) when (e is CredentialException or CredentialKeyException)
        {
            throw new GitHubSourceException(GitHubSourceErrorKind.Unexpected, "The stored credential could not be read.", inner: e);
        }
    }

    public void Invalidate(GitHubConnectionRef connection)
    {
        if (connection is { Kind: GitHubAuthKind.App, InstallationId: { } installationId })
            installationTokens.Invalidate(installationId);
    }

    public async Task ReportRejectedAsync(GitHubConnectionRef connection, string token, CancellationToken ct)
    {
        if (connection.Kind != GitHubAuthKind.Token)
            return;
        try
        {
            await store.RevokeAsync(connection.TenantId, connection.ConnectionId, token, ct);
        }
        catch (Exception e) when (e is CredentialException or CredentialKeyException)
        {
        }
    }
}
