namespace WorkBoard.Api.Infrastructure.GitHub;

// Scopes is null for a token that sends no X-OAuth-Scopes header (fine-grained tokens).
public sealed record TokenIdentity(GitHubUser User, IReadOnlyList<string>? Scopes, DateTimeOffset? ExpiresAt);

public sealed record OrgRef(long Id, string Login);

public enum OrgAccessStatus
{
    Active,
    NotActive,
    NotMember,
    SsoRequired,
    Forbidden,
}

// SsoUrl is set for SsoRequired when GitHub sent an authorization link.
public sealed record OrgAccess(OrgAccessStatus Status, long? OrgId = null, string? OrgLogin = null, string? SsoUrl = null);

// Calls GitHub with a personal access token that is not stored yet. Throws GitHubSourceException when GitHub
// cannot answer (RateLimited, Unexpected); messages never carry the token.
public interface IGitHubTokenClient
{
    // Null when GitHub rejects the token.
    Task<TokenIdentity?> GetIdentityAsync(string token, CancellationToken ct);

    // The caller's membership in the organization and whether the token can list its repositories.
    // Null when GitHub rejects the token.
    Task<OrgAccess?> GetOrganizationAccessAsync(string token, string organization, CancellationToken ct);

    // The organization's current login by its numeric id, to follow a rename. Null when GitHub does not show it to the
    // token (or the implementation cannot look it up).
    Task<OrgRef?> GetOrganizationByIdAsync(string token, long organizationId, CancellationToken ct) =>
        Task.FromResult<OrgRef?>(null);
}
