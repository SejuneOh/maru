using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace WorkBoard.Api.Infrastructure.GitHub;

// Remembers the ETag and body of GET answers for token connections, so the next request can send If-None-Match and a
// 304 (which GitHub does not count against the rate limit) reuses the body. In memory only, per connection and per
// token (a replaced token may see other data), and bounded: when full it starts over.
public sealed class GitHubEtagCache(int capacity = 2000)
{
    public sealed record Entry(string Etag, string Body);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public int Count => _entries.Count;

    public static string Key(GitHubConnectionRef connection, string token, string url) =>
        $"{connection.ConnectionId:N}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)), 0, 6)}:{url}";

    public Entry? Get(string key) => _entries.TryGetValue(key, out var entry) ? entry : null;

    public void Set(string key, Entry entry)
    {
        if (_entries.Count >= capacity && !_entries.ContainsKey(key))
            _entries.Clear();
        _entries[key] = entry;
    }

    public void Remove(string key) => _entries.TryRemove(key, out _);
}
