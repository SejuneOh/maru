namespace WorkBoard.Api.Infrastructure.Identity;

public interface IMemberLookup
{
    // Active memberships only: a member an admin removed is skipped.
    Task<IReadOnlyList<DevIdentity>> FindByUserIdAsync(long githubUserId, CancellationToken ct);

    // Development sign-in only. Null when the login matches no member or invitation, or several user ids.
    Task<long?> FindUserIdByLoginAsync(string githubLogin, CancellationToken ct);

    Task<IReadOnlyList<DevUser>> ListAsync(CancellationToken ct);
}
