namespace WorkBoard.Api.Features.Members;

public sealed record Member(
    Guid Id,
    long GithubUserId,
    string GithubLogin,
    string? DisplayName,
    string? AvatarUrl,
    string Role,
    DateTime? RemovedAt = null);

internal sealed class MemberRow
{
    public Guid Id { get; set; }
    public long GithubUserId { get; set; }
    public string GithubLogin { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = "member";
    public DateTime? RemovedAt { get; set; }

    public Member ToMember() => new(Id, GithubUserId, GithubLogin, DisplayName, AvatarUrl, Role, RemovedAt);

    public const string Columns = "m.id, m.github_user_id, m.github_login, m.display_name, m.avatar_url, m.role";
}
