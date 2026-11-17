namespace WorkBoard.Api.Infrastructure.GitHub;

public sealed record OrgInfo(long Id, string Login, string? Name, string? Description, string? HtmlUrl, string? AvatarUrl);

public sealed record OrgRepository(
    long Id, string Name, string FullName, string? HtmlUrl, bool Archived, bool Private, string? Description);

public sealed record OrgTeam(long Id, string Name, string Slug, string? Description, string? Privacy);

public sealed record OrgMemberInfo(long Id, string Login, string? AvatarUrl);

// Truncated is true when the page cap was reached before GitHub ran out of items.
public sealed record Capped<T>(IReadOnlyList<T> Items, bool Truncated);

// Reads an organization's settings with the connection's own credential (IGitHubCredentials). Read-only; lists are
// capped at MaxPages pages of 100. Throws GitHubSourceException.
public interface IGitHubOrgReader
{
    public const int MaxPages = 10;

    Task<OrgInfo> GetOrgInfoAsync(GitHubConnectionRef connection, CancellationToken ct);

    Task<Capped<OrgRepository>> ReadOrgRepositoriesAsync(GitHubConnectionRef connection, CancellationToken ct);

    Task<Capped<OrgTeam>> ReadOrgTeamsAsync(GitHubConnectionRef connection, CancellationToken ct);

    Task<Capped<OrgMemberInfo>> ReadOrgMembersAsync(GitHubConnectionRef connection, CancellationToken ct);

    // GitHub ids of the repositories a team can access.
    Task<Capped<long>> ReadTeamRepositoryIdsAsync(GitHubConnectionRef connection, string teamSlug, CancellationToken ct);

    Task<int> CountLabelsAsync(GitHubConnectionRef connection, string repositoryFullName, CancellationToken ct);

    Task<int> CountMilestonesAsync(GitHubConnectionRef connection, string repositoryFullName, CancellationToken ct);
}
