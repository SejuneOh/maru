namespace WorkBoard.Api.Features.Repositories;

public sealed record LanguageShare(string Name, long Bytes, double Ratio);

// Type is "dir" or "file". Children is null for files; a folder always has a list, empty when the depth or node
// limit cut it off.
public sealed record RepoTreeNode(string Name, string Path, string Type, IReadOnlyList<RepoTreeNode>? Children);

public sealed record DependencyList(string File, string Manager, IReadOnlyList<string> Items);

// Counts come from the mirror, not from GitHub.
public sealed record RepositoryActivity(int OpenIssues, int OpenPrs, int DoneThisWeek);

// The GitHub-derived part of the answer; this is what the memory cache holds.
public sealed record RepositoryCodeInfo(
    string DefaultBranch,
    string BaseCommit,
    IReadOnlyList<LanguageShare> Languages,
    string? Readme,
    IReadOnlyList<RepoTreeNode> Tree,
    bool Truncated,
    IReadOnlyList<DependencyList> Dependencies,
    DateTimeOffset GeneratedAt);

public sealed record RepositoryInfo(
    string FullName,
    string DefaultBranch,
    string BaseCommit,
    IReadOnlyList<LanguageShare> Languages,
    string? Readme,
    IReadOnlyList<RepoTreeNode> Tree,
    bool Truncated,
    IReadOnlyList<DependencyList> Dependencies,
    RepositoryActivity Activity,
    DateTimeOffset GeneratedAt,
    bool Cached);
