using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WorkBoard.Api.Infrastructure.GitHub;

namespace WorkBoard.Api.Features.Repositories;

// Reads a repository's languages, README, file tree and dependency lists from GitHub. Results live only in the
// server's memory, keyed by company, repository and default-branch commit, so a company never sees another's entry
// and a new commit is read afresh. Nothing is written to the database.
public sealed class RepositoryCodeReader(IGitHubRepositoryReader github, IMemoryCache cache, IOptions<RepositoriesOptions> options)
{
    private const int MaxDependencyAttempts = 10;

    // One read runs at a time per cache key, so concurrent first-time requests for the same repository and
    // commit do not each call GitHub. Gates are removed once no one holds them.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    public static string CacheKey(Guid tenantId, Guid repositoryId, string baseCommit) =>
        $"repo-info:{tenantId:N}:{repositoryId:N}:{baseCommit}";

    public async Task<(RepositoryCodeInfo Info, bool Cached)> GetAsync(
        Guid tenantId, Guid repositoryId, GitHubRepoRef repo, CancellationToken ct)
    {
        var o = options.Value;
        var head = await github.GetHeadAsync(repo.Connection, repo.Owner, repo.Name, ct);
        var key = CacheKey(tenantId, repositoryId, head.CommitSha);
        if (cache.TryGetValue(key, out RepositoryCodeInfo? hit) && hit is not null)
            return (hit, true);

        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // A request that waited behind the gate takes the entry the first one just stored.
            if (cache.TryGetValue(key, out hit) && hit is not null)
                return (hit, true);

            var languages = Languages(await github.GetLanguagesAsync(repo.Connection, repo.Owner, repo.Name, ct));
            var readme = await github.GetReadmeAsync(repo.Connection, repo.Owner, repo.Name, o.ReadmeMaxBytes, ct);
            var (tree, truncated, seen) = await ReadTreeAsync(repo, head.CommitSha, o, ct);
            var dependencies = await ReadDependenciesAsync(repo, seen, o, ct);

            var info = new RepositoryCodeInfo(
                head.DefaultBranch, head.CommitSha, languages, readme, tree, truncated, dependencies, DateTimeOffset.UtcNow);
            cache.Set(key, info, TimeSpan.FromSeconds(o.CacheTtlSeconds));
            return (info, false);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
                Gates.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key, gate));
        }
    }

    public static IReadOnlyList<LanguageShare> Languages(IReadOnlyDictionary<string, long> bytes)
    {
        var total = bytes.Values.Where(b => b > 0).Sum();
        return total == 0
            ? []
            : bytes.Where(p => p.Value > 0)
                .OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new LanguageShare(p.Key, p.Value, Math.Round((double)p.Value / total, 4)))
                .ToList();
    }

    private sealed class Node(string name, string path, bool dir)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public bool Dir { get; } = dir;
        public List<Node> Children { get; } = [];
    }

    // Returns the depth- and node-limited tree and every entry that was read (for dependency discovery).
    // One recursive read normally; when GitHub reports it truncated, folders are read one by one within a request budget.
    private async Task<(IReadOnlyList<RepoTreeNode> Tree, bool Truncated, List<TreeEntry> Seen)> ReadTreeAsync(
        GitHubRepoRef repo, string commit, RepositoriesOptions o, CancellationToken ct)
    {
        var rootListing = await github.GetTreeAsync(repo.Connection, repo.Owner, repo.Name, commit, true, ct);
        var truncated = rootListing.Truncated;
        var seen = new List<TreeEntry>();
        Func<Node, Task<IReadOnlyList<TreeEntry>>> children;

        if (!rootListing.Truncated)
        {
            seen.AddRange(rootListing.Entries);
            var byParent = rootListing.Entries.ToLookup(e => ParentOf(e.Path));
            children = n => Task.FromResult<IReadOnlyList<TreeEntry>>(byParent[n.Path].ToList());
        }
        else
        {
            // The recursive listing is discarded; the tree is rebuilt from folder reads and is truncated only if those fall short.
            truncated = false;
            var requests = 0;
            var shaByPath = new Dictionary<string, string> { [""] = commit };
            children = async n =>
            {
                if (requests >= o.MaxTreeRequests || !shaByPath.TryGetValue(n.Path, out var sha))
                {
                    truncated = true;
                    return [];
                }
                requests++;
                var listing = await github.GetTreeAsync(repo.Connection, repo.Owner, repo.Name, sha, false, ct);
                truncated |= listing.Truncated;
                var entries = listing.Entries
                    .Select(e => e with { Path = n.Path.Length == 0 ? e.Path : n.Path + "/" + e.Path }).ToList();
                foreach (var e in entries.Where(e => e.Type == "tree"))
                    shaByPath[e.Path] = e.Sha;
                seen.AddRange(entries);
                return entries;
            };
        }

        var root = new Node("", "", true);
        var nodes = 0;
        var level = new List<Node> { root };
        for (var depth = 1; depth <= o.TreeDepth && level.Count > 0; depth++)
        {
            var next = new List<Node>();
            foreach (var folder in level)
            {
                var entries = (await children(folder))
                    .Where(e => e.Type is "tree" or "blob")
                    .OrderBy(e => e.Type == "tree" ? 0 : 1)
                    .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase);
                foreach (var e in entries)
                {
                    if (nodes >= o.MaxTreeNodes)
                        return (Freeze(root).Children!, true, seen);
                    nodes++;
                    var node = new Node(e.Path[(e.Path.LastIndexOf('/') + 1)..], e.Path, e.Type == "tree");
                    folder.Children.Add(node);
                    if (node.Dir)
                        next.Add(node);
                }
            }
            level = next;
        }
        return (Freeze(root).Children!, truncated, seen);
    }

    private static RepoTreeNode Freeze(Node n) =>
        new(n.Name, n.Path, n.Dir ? "dir" : "file", n.Dir ? n.Children.Select(Freeze).ToList() : null);

    private static string ParentOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    private async Task<IReadOnlyList<DependencyList>> ReadDependenciesAsync(
        GitHubRepoRef repo, List<TreeEntry> seen, RepositoriesOptions o, CancellationToken ct)
    {
        var candidates = seen
            .Where(e => e.Type == "blob" && e.Size is { } size && size <= o.MaxDependencyFileBytes
                        && DependencyParsers.ManagerFor(e.Path) is not null
                        && !e.Path.Split('/')[..^1].Any(s => DependencyParsers.SkippedFolders.Contains(s)))
            .OrderBy(e => e.Path.Count(c => c == '/')).ThenBy(e => e.Path, StringComparer.Ordinal);

        var result = new List<DependencyList>();
        var attempts = 0;
        foreach (var e in candidates)
        {
            if (result.Count >= o.MaxDependencyFiles || attempts++ >= MaxDependencyAttempts)
                break;
            var text = await github.GetBlobTextAsync(
                repo.Connection, repo.Owner, repo.Name, e.Sha, o.MaxDependencyFileBytes, ct);
            var manager = DependencyParsers.ManagerFor(e.Path)!;
            if (text is not null && DependencyParsers.Parse(manager, text, o.MaxDependencyItems) is { } items)
                result.Add(new DependencyList(e.Path, manager, items));
        }
        return result;
    }
}
