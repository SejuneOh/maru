using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WorkBoard.Api.Features.Repositories;
using WorkBoard.Api.Infrastructure.GitHub;

namespace WorkBoard.Api.Tests;

public class RepositoryInfoTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid RepoId = Guid.NewGuid();

    private static GitHubRepoRef Repo(Guid tenant) =>
        new(new GitHubConnectionRef(tenant, Guid.NewGuid(), GitHubAuthKind.App, 1), "acme", "app");

    // An in-memory GitHub: files by path (text, or null size-only), served as recursive or per-folder trees.
    private sealed class FakeReader : IGitHubRepositoryReader
    {
        public string Commit { get; set; } = "c1";
        public bool TruncateRecursive { get; set; }
        public int ReadDelayMs { get; set; }
        public Dictionary<string, (long Size, string? Text)> Files { get; } = [];
        public int HeadCalls, TreeCalls, BlobCalls, LanguageCalls;
        public List<string> BlobsRead { get; } = [];

        public Task<RepositoryHead> GetHeadAsync(GitHubConnectionRef c, string o, string n, CancellationToken ct)
        {
            HeadCalls++;
            return Task.FromResult(new RepositoryHead("main", Commit));
        }

        public async Task<IReadOnlyDictionary<string, long>> GetLanguagesAsync(GitHubConnectionRef c, string o, string n, CancellationToken ct)
        {
            Interlocked.Increment(ref LanguageCalls);
            if (ReadDelayMs > 0)
                await Task.Delay(ReadDelayMs, ct);
            return new Dictionary<string, long> { ["C#"] = 750, ["TypeScript"] = 250 };
        }

        public Task<string?> GetReadmeAsync(GitHubConnectionRef c, string o, string n, int max, CancellationToken ct) =>
            Task.FromResult<string?>("# hello");

        private IEnumerable<TreeEntry> AllEntries()
        {
            var dirs = Files.Keys.SelectMany(p =>
            {
                var parts = p.Split('/');
                return Enumerable.Range(1, parts.Length - 1).Select(i => string.Join('/', parts[..i]));
            }).Distinct();
            foreach (var d in dirs)
                yield return new TreeEntry(d, "tree", "tree:" + d, null);
            foreach (var (p, f) in Files)
                yield return new TreeEntry(p, "blob", "blob:" + p, f.Size);
        }

        public Task<TreeListing> GetTreeAsync(GitHubConnectionRef c, string o, string n, string sha, bool recursive, CancellationToken ct)
        {
            TreeCalls++;
            if (recursive)
                return Task.FromResult(new TreeListing(
                    TruncateRecursive ? AllEntries().Take(3).ToList() : AllEntries().ToList(), TruncateRecursive));
            var folder = sha.StartsWith("tree:") ? sha[5..] : "";
            var prefix = folder.Length == 0 ? "" : folder + "/";
            var direct = AllEntries()
                .Where(e => e.Path.StartsWith(prefix) && !e.Path[prefix.Length..].Contains('/'))
                .Select(e => e with { Path = e.Path[prefix.Length..] }).ToList();
            return Task.FromResult(new TreeListing(direct, false));
        }

        public Task<string?> GetBlobTextAsync(GitHubConnectionRef c, string o, string n, string sha, int max, CancellationToken ct)
        {
            BlobCalls++;
            var path = sha["blob:".Length..];
            BlobsRead.Add(path);
            return Task.FromResult(Files[path].Text);
        }
    }

    private static RepositoryCodeReader Reader(FakeReader github, IMemoryCache cache, Action<RepositoriesOptions>? tune = null)
    {
        var o = new RepositoriesOptions();
        tune?.Invoke(o);
        return new RepositoryCodeReader(github, cache, Options.Create(o));
    }

    private static MemoryCache NewCache() => new(new MemoryCacheOptions());

    [Fact]
    public async Task Languages_get_bytes_and_ratio_largest_first()
    {
        var github = new FakeReader();
        var (info, _) = await Reader(github, NewCache()).GetAsync(TenantA, RepoId, Repo(TenantA), default);

        Assert.Equal(["C#", "TypeScript"], info.Languages.Select(l => l.Name));
        Assert.Equal(0.75, info.Languages[0].Ratio);
        Assert.Equal(250, info.Languages[1].Bytes);
        Assert.Equal("main", info.DefaultBranch);
        Assert.Equal("c1", info.BaseCommit);
    }

    [Fact]
    public async Task Tree_stops_at_node_limit_and_keeps_shallow_levels()
    {
        var github = new FakeReader();
        for (var i = 0; i < 10; i++)
            github.Files[$"src/f{i}.cs"] = (10, null);
        github.Files["a.txt"] = (1, null);
        github.Files["b.txt"] = (1, null);

        var (info, _) = await Reader(github, NewCache(), o => o.MaxTreeNodes = 4).GetAsync(TenantA, RepoId, Repo(TenantA), default);

        Assert.True(info.Truncated);
        // Level 1 (src, a.txt, b.txt) is complete before the limit cuts into level 2.
        Assert.Equal(["src", "a.txt", "b.txt"], info.Tree.Select(n => n.Name));
        Assert.Single(info.Tree[0].Children!);
        Assert.Null(info.Tree[1].Children);
    }

    [Fact]
    public async Task Tree_hides_levels_below_depth_without_marking_truncated()
    {
        var github = new FakeReader();
        github.Files["a/b/c/d/deep.txt"] = (1, null);

        var (info, _) = await Reader(github, NewCache()).GetAsync(TenantA, RepoId, Repo(TenantA), default);

        Assert.False(info.Truncated);
        var c = info.Tree[0].Children![0].Children![0];
        Assert.Equal("a/b/c", c.Path);
        Assert.Empty(c.Children!);
    }

    [Fact]
    public async Task Truncated_recursive_tree_is_completed_folder_by_folder()
    {
        var github = new FakeReader { TruncateRecursive = true };
        github.Files["src/app/main.cs"] = (1, null);
        github.Files["docs/readme.md"] = (1, null);
        github.Files["package.json"] = (20, """{"dependencies":{"react":"1"}}""");

        var (info, _) = await Reader(github, NewCache()).GetAsync(TenantA, RepoId, Repo(TenantA), default);

        Assert.False(info.Truncated);
        Assert.Equal(["docs", "src", "package.json"], info.Tree.Select(n => n.Name));
        Assert.Equal("src/app/main.cs", info.Tree[1].Children![0].Children![0].Path);
        Assert.Equal(["package.json"], info.Dependencies.Select(d => d.File));
    }

    [Fact]
    public async Task Truncated_recursive_tree_stops_at_request_budget_and_says_so()
    {
        var github = new FakeReader { TruncateRecursive = true };
        for (var i = 0; i < 6; i++)
            github.Files[$"d{i}/x.txt"] = (1, null);

        var (info, _) = await Reader(github, NewCache(), o => o.MaxTreeRequests = 3).GetAsync(TenantA, RepoId, Repo(TenantA), default);

        Assert.True(info.Truncated);
        Assert.Equal(1 + 3, github.TreeCalls);
    }

    [Fact]
    public async Task Dependency_files_are_limited_to_five_and_one_megabyte()
    {
        var github = new FakeReader();
        github.Files["package.json"] = (50, """{"dependencies":{"next":"1"},"devDependencies":{"vitest":"1"}}""");
        github.Files["huge/package.json"] = (2 * 1024 * 1024, "{}");
        for (var i = 0; i < 8; i++)
            github.Files[$"svc{i}/svc{i}.csproj"] = (100, $"""<Project><ItemGroup><PackageReference Include="Pkg{i}" Version="1" /></ItemGroup></Project>""");

        var (info, _) = await Reader(github, NewCache()).GetAsync(TenantA, RepoId, Repo(TenantA), default);

        Assert.Equal(5, info.Dependencies.Count);
        Assert.Equal("package.json", info.Dependencies[0].File);
        Assert.Equal("npm", info.Dependencies[0].Manager);
        Assert.Equal(["next", "vitest"], info.Dependencies[0].Items);
        Assert.Equal("nuget", info.Dependencies[1].Manager);
        Assert.DoesNotContain("huge/package.json", github.BlobsRead);
        Assert.Equal(5, github.BlobCalls);
    }

    [Fact]
    public async Task Unreadable_manifest_is_skipped_and_does_not_use_a_slot()
    {
        var github = new FakeReader();
        github.Files["package.json"] = (10, null);
        github.Files["go.mod"] = (10, "module x\n\nrequire (\n\tgithub.com/a/b v1.0.0\n\tgithub.com/c/d v1.0.0 // indirect\n)\n");

        var (info, _) = await Reader(github, NewCache()).GetAsync(TenantA, RepoId, Repo(TenantA), default);

        var go = Assert.Single(info.Dependencies);
        Assert.Equal("go", go.Manager);
        Assert.Equal(["github.com/a/b"], go.Items);
    }

    [Fact]
    public async Task Same_base_commit_is_served_from_cache()
    {
        var github = new FakeReader();
        github.Files["a.txt"] = (1, null);
        var reader = Reader(github, NewCache());

        var (first, firstCached) = await reader.GetAsync(TenantA, RepoId, Repo(TenantA), default);
        var treeCalls = github.TreeCalls;
        var (second, secondCached) = await reader.GetAsync(TenantA, RepoId, Repo(TenantA), default);

        Assert.False(firstCached);
        Assert.True(secondCached);
        Assert.Same(first, second);
        Assert.Equal(treeCalls, github.TreeCalls);
        Assert.Equal(2, github.HeadCalls);
    }

    [Fact]
    public async Task Concurrent_first_reads_call_github_once()
    {
        var github = new FakeReader { ReadDelayMs = 50 };
        var reader = Reader(github, NewCache());

        // Several requests for the same repository and commit arrive before the first finishes; the gate lets
        // one read GitHub and the rest take the stored entry.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => reader.GetAsync(TenantA, RepoId, Repo(TenantA), default)));

        Assert.Equal(1, github.LanguageCalls);
        Assert.Equal(1, results.Count(r => !r.Cached));
    }

    [Fact]
    public async Task New_base_commit_is_read_again()
    {
        var github = new FakeReader();
        var reader = Reader(github, NewCache());

        await reader.GetAsync(TenantA, RepoId, Repo(TenantA), default);
        github.Commit = "c2";
        var (info, cached) = await reader.GetAsync(TenantA, RepoId, Repo(TenantA), default);

        Assert.False(cached);
        Assert.Equal("c2", info.BaseCommit);
    }

    [Fact]
    public async Task Cache_is_separate_per_company_even_for_the_same_repository_and_commit()
    {
        var github = new FakeReader();
        var cache = NewCache();
        var reader = Reader(github, cache);

        var (a, _) = await reader.GetAsync(TenantA, RepoId, Repo(TenantA), default);
        github.Files["secret-of-b.txt"] = (1, null);
        var (b, bCached) = await reader.GetAsync(TenantB, RepoId, Repo(TenantB), default);

        Assert.False(bCached);
        Assert.NotSame(a, b);
        Assert.DoesNotContain(a.Tree, n => n.Name == "secret-of-b.txt");
        Assert.NotNull(cache.Get<RepositoryCodeInfo>(RepositoryCodeReader.CacheKey(TenantA, RepoId, "c1")));
        Assert.NotEqual(
            RepositoryCodeReader.CacheKey(TenantA, RepoId, "c1"), RepositoryCodeReader.CacheKey(TenantB, RepoId, "c1"));
    }

    [Theory]
    [InlineData("npm", """{"dependencies":{"b":"1","a":"1"},"devDependencies":{"a":"2"}}""", "a,b")]
    [InlineData("nuget", """<Project><PackageReference Include="X" Version="1"/><PackageReference Version="2" Include="Y"/></Project>""", "X,Y")]
    [InlineData("go", "require github.com/a/b v1\nrequire (\n github.com/c/d v2\n)", "github.com/a/b,github.com/c/d")]
    [InlineData("pip", "[project]\nname='x'\ndependencies = [\n  \"fastapi>=0.1\",\n  \"uvicorn[standard]>=0.2\",\n]\n[tool.x]\ny=1", "fastapi,uvicorn")]
    [InlineData("pip", "[tool.poetry.dependencies]\npython = \"^3.11\"\nrequests = \"^2\"\n", "requests")]
    public void Manifests_yield_their_dependency_names(string manager, string text, string expected)
    {
        Assert.Equal(expected.Split(','), DependencyParsers.Parse(manager, text, 100));
    }

    [Fact]
    public void Broken_package_json_is_not_a_manifest()
    {
        Assert.Null(DependencyParsers.Parse("npm", "{not json", 100));
    }

    [Fact]
    public void Broken_csproj_is_not_a_manifest()
    {
        Assert.Null(DependencyParsers.Parse("nuget", "<Project><ItemGroup>", 100));
    }

    [Fact]
    public void A_hostile_csproj_is_parsed_quickly()
    {
        // A '<PackageReference' with a long run of whitespace and no closing tag drove the old
        // backtracking regex to quadratic time; the XML parser rejects it in linear time.
        var hostile = "<PackageReference" + new string(' ', 200_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = DependencyParsers.Parse("nuget", hostile, 100);
        sw.Stop();
        Assert.Null(result);
        Assert.True(sw.ElapsedMilliseconds < 1_000, $"parse took {sw.ElapsedMilliseconds}ms");
    }

    // ---- GitHubSource decoding ----

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request.RequestUri!));
    }

    private sealed class Tokens : IGitHubCredentials
    {
        public Task<GitHubCredential> GetAsync(GitHubConnectionRef connection, CancellationToken ct) =>
            Task.FromResult(new GitHubCredential("tok", GitHubAuthKind.App));

        public void Invalidate(GitHubConnectionRef connection) { }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Blob(byte[] bytes) =>
        $$"""{"size":{{bytes.Length}},"encoding":"base64","content":"{{Convert.ToBase64String(bytes)}}"}""";

    private static GitHubSource Source(Func<Uri, HttpResponseMessage> respond) =>
        new(new HttpClient(new Handler(respond)), new Tokens());

    [Fact]
    public async Task Blob_over_limit_or_binary_reads_as_null_and_text_decodes()
    {
        var text = Encoding.UTF8.GetBytes("{\"name\":\"한글\"}");
        var source = Source(u => u.AbsolutePath.EndsWith("/text") ? Json(Blob(text))
            : u.AbsolutePath.EndsWith("/bin") ? Json(Blob([0xFF, 0xFE, 0x00, 0x80]))
            : Json(Blob(new byte[2000])));
        var conn = Repo(TenantA).Connection;

        Assert.Equal("{\"name\":\"한글\"}", await source.GetBlobTextAsync(conn, "o", "r", "text", 1000, default));
        Assert.Null(await source.GetBlobTextAsync(conn, "o", "r", "bin", 1000, default));
        Assert.Null(await source.GetBlobTextAsync(conn, "o", "r", "big", 1000, default));
    }

    [Fact]
    public async Task Readme_is_cut_at_the_byte_limit_on_a_character_boundary_and_missing_readme_is_null()
    {
        var readme = Encoding.UTF8.GetBytes("가나다라");
        var source = Source(u => u.AbsolutePath.Contains("/readme") && u.AbsolutePath.Contains("/has/")
            ? Json(Blob(readme))
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        var conn = Repo(TenantA).Connection;

        // 4 bytes would end inside the second character; it is dropped whole.
        Assert.Equal("가", await source.GetReadmeAsync(conn, "has", "r", 4, default));
        Assert.Null(await source.GetReadmeAsync(conn, "none", "r", 100, default));
    }
}
