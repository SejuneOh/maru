namespace WorkBoard.Api.Infrastructure.GitHub;

public sealed record RepositoryHead(string DefaultBranch, string CommitSha);

// Type is "tree" (folder), "blob" (file) or "commit" (submodule). Size is null for folders.
public sealed record TreeEntry(string Path, string Type, string Sha, long? Size);

// Truncated is true when GitHub cut the listing short.
public sealed record TreeListing(IReadOnlyList<TreeEntry> Entries, bool Truncated);

// Reads one repository's code-side metadata with the connection's own credential. Nothing is stored.
// Throws GitHubSourceException; "not found" answers for optional parts (README, blob) come back as null.
public interface IGitHubRepositoryReader
{
    Task<RepositoryHead> GetHeadAsync(GitHubConnectionRef connection, string owner, string name, CancellationToken ct);

    Task<IReadOnlyDictionary<string, long>> GetLanguagesAsync(GitHubConnectionRef connection, string owner, string name, CancellationToken ct);

    // README text cut to maxBytes (UTF-8); null when the repository has none.
    Task<string?> GetReadmeAsync(GitHubConnectionRef connection, string owner, string name, int maxBytes, CancellationToken ct);

    // Entry paths are relative to the root of the tree read. Non-recursive lists one folder only.
    Task<TreeListing> GetTreeAsync(
        GitHubConnectionRef connection, string owner, string name, string treeSha, bool recursive, CancellationToken ct);

    // File text; null when the blob is missing, larger than maxBytes or not UTF-8 text.
    Task<string?> GetBlobTextAsync(
        GitHubConnectionRef connection, string owner, string name, string blobSha, int maxBytes, CancellationToken ct);
}
