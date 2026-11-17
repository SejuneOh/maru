namespace WorkBoard.Api.Infrastructure.GitHub;

public sealed record GitHubUser(long Id, string Login);

public sealed record GitHubInstallation(long AccountId, string AccountLogin, string AccountType, string? RepositorySelection);

public sealed record GitHubAppInstallation(
    long Id, long AccountId, string AccountLogin, string AccountType, string? RepositorySelection, DateTimeOffset CreatedAt);

public enum OrgMembership
{
    NotMember,
    Member,
    Owner,

    // The installation has not been granted the "Members: read" organization permission.
    NotGranted,
}

public interface IGitHubAppClient
{
    // Null when GitHub rejects the code.
    Task<string?> ExchangeCodeAsync(string code, CancellationToken ct);

    // Null when GitHub rejects the user token.
    Task<GitHubUser?> GetUserAsync(string userToken, CancellationToken ct);

    // Null when GitHub rejects the user token.
    Task<IReadOnlyList<long>?> ListUserInstallationIdsAsync(string userToken, CancellationToken ct);

    // Authenticated as the App itself. Null when the installation does not exist.
    Task<GitHubInstallation?> GetInstallationAsync(long installationId, CancellationToken ct);

    // Every installation of the App, authenticated as the App itself.
    Task<IReadOnlyList<GitHubAppInstallation>> ListAppInstallationsAsync(CancellationToken ct);

    // With the installation's token. Only an active membership whose user id equals userId counts; login only
    // addresses the request and may be stale.
    Task<OrgMembership> GetOrganizationMembershipAsync(
        long installationId, string organization, long userId, string login, CancellationToken ct);

    // With the installation's token. Null when the installation lacks "Members: read".
    Task<IReadOnlyList<GitHubUser>?> ListOrganizationMembersAsync(long installationId, string organization, CancellationToken ct);
}
