using System.Text;
using System.Text.Json.Serialization;

namespace WorkBoard.Api.Infrastructure.GitHub;

public sealed partial class GitHubSource
{
    private static string RepoUrl(string owner, string name) =>
        $"{ApiBase}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";

    public async Task<RepositoryHead> GetHeadAsync(GitHubConnectionRef connection, string owner, string name, CancellationToken ct)
    {
        var (repo, _) = await GetRestAsync<RestRepoHead>(connection, RepoUrl(owner, name), ct);
        if (string.IsNullOrEmpty(repo.DefaultBranch))
            throw Unexpected();
        var (branch, _) = await GetRestAsync<RestBranchHead>(
            connection, $"{RepoUrl(owner, name)}/branches/{Uri.EscapeDataString(repo.DefaultBranch)}", ct);
        return new RepositoryHead(repo.DefaultBranch, branch.Commit?.Sha ?? throw Unexpected());
    }

    public async Task<IReadOnlyDictionary<string, long>> GetLanguagesAsync(
        GitHubConnectionRef connection, string owner, string name, CancellationToken ct) =>
        (await GetRestAsync<Dictionary<string, long>>(connection, $"{RepoUrl(owner, name)}/languages", ct)).Body;

    public async Task<string?> GetReadmeAsync(
        GitHubConnectionRef connection, string owner, string name, int maxBytes, CancellationToken ct)
    {
        try
        {
            var (file, _) = await GetRestAsync<RestContent>(connection, $"{RepoUrl(owner, name)}/readme", ct);
            return DecodeContent(file, long.MaxValue, maxBytes);
        }
        catch (GitHubSourceException e) when (e.Kind == GitHubSourceErrorKind.NotFound)
        {
            return null;
        }
    }

    public async Task<TreeListing> GetTreeAsync(
        GitHubConnectionRef connection, string owner, string name, string treeSha, bool recursive, CancellationToken ct)
    {
        var url = $"{RepoUrl(owner, name)}/git/trees/{Uri.EscapeDataString(treeSha)}{(recursive ? "?recursive=1" : "")}";
        var (tree, _) = await GetRestAsync<RestTree>(connection, url, ct);
        return new TreeListing(
            (tree.Tree ?? []).Select(t => new TreeEntry(t.Path, t.Type, t.Sha, t.Size)).ToList(), tree.Truncated);
    }

    public async Task<string?> GetBlobTextAsync(
        GitHubConnectionRef connection, string owner, string name, string blobSha, int maxBytes, CancellationToken ct)
    {
        try
        {
            var (blob, _) = await GetRestAsync<RestContent>(connection, $"{RepoUrl(owner, name)}/git/blobs/{Uri.EscapeDataString(blobSha)}", ct);
            return DecodeContent(blob, maxBytes, int.MaxValue);
        }
        catch (GitHubSourceException e) when (e.Kind == GitHubSourceErrorKind.NotFound)
        {
            return null;
        }
    }

    // Refuses content over maxSize, binary content (invalid UTF-8) and non-base64 encodings; cuts text at maxBytes.
    private static string? DecodeContent(RestContent c, long maxSize, int maxBytes)
    {
        if (c.Size > maxSize || c.Content is null || c.Encoding != "base64")
            return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(c.Content.Replace("\n", "").Replace("\r", ""));
        }
        catch (FormatException)
        {
            return null;
        }
        if (bytes.Length > maxSize)
            return null;
        var take = Math.Min(bytes.Length, maxBytes);
        // Do not end inside a multi-byte character.
        while (take > 0 && take < bytes.Length && (bytes[take] & 0xC0) == 0x80)
            take--;
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes, 0, take);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private sealed record RestRepoHead([property: JsonPropertyName("default_branch")] string? DefaultBranch);

    private sealed record RestBranchHead(RestCommitRef? Commit);

    private sealed record RestCommitRef(string? Sha);

    private sealed record RestContent(string? Content, string? Encoding, long Size);

    private sealed record RestTree(List<RestTreeItem>? Tree, bool Truncated);

    private sealed record RestTreeItem(string Path, string Type, string Sha, long? Size);
}
