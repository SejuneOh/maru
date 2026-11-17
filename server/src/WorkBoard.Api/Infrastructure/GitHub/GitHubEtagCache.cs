using System.Security.Cryptography;
using System.Text;

namespace WorkBoard.Api.Infrastructure.GitHub;

// Remembers the ETag and body of GET answers for token connections, so the next request can send If-None-Match and a
// 304 (which GitHub does not count against the rate limit) reuses the body. In memory only, per connection and per
// token (a replaced token may see other data). Bounded by a byte budget: bodies above maxBodyBytes are not kept, and
// the oldest entries are dropped to make room.
public sealed class GitHubEtagCache(long budgetBytes = 32L * 1024 * 1024, int maxBodyBytes = 1024 * 1024)
{
    public sealed record Entry(string Etag, string Body);

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Entry Entry, long Size)>> _index = new();
    private readonly LinkedList<(string Key, Entry Entry, long Size)> _order = new();
    private long _bytes;

    public int Count
    {
        get
        {
            lock (_gate)
                return _index.Count;
        }
    }

    public long Bytes
    {
        get
        {
            lock (_gate)
                return _bytes;
        }
    }

    public static string Key(GitHubConnectionRef connection, string token, string url) =>
        $"{connection.ConnectionId:N}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)), 0, 6)}:{url}";

    public Entry? Get(string key)
    {
        lock (_gate)
            return _index.TryGetValue(key, out var node) ? node.Value.Entry : null;
    }

    public void Set(string key, Entry entry)
    {
        var size = (long)Encoding.UTF8.GetByteCount(entry.Body) + entry.Etag.Length + key.Length;
        lock (_gate)
        {
            RemoveLocked(key);
            if (Encoding.UTF8.GetByteCount(entry.Body) > maxBodyBytes || size > budgetBytes)
                return;
            while (_bytes + size > budgetBytes && _order.First is { } oldest)
                RemoveLocked(oldest.Value.Key);
            _index[key] = _order.AddLast((key, entry, size));
            _bytes += size;
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
            RemoveLocked(key);
    }

    private void RemoveLocked(string key)
    {
        if (!_index.Remove(key, out var node))
            return;
        _order.Remove(node);
        _bytes -= node.Value.Size;
    }
}
