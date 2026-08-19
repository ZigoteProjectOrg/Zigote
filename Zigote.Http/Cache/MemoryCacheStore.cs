
namespace Zigote.Http.Cache;

/// <summary>
///     An in-process LRU store with a byte budget. The default everywhere, and the only store WASM
///     gets.
/// </summary>
/// <remarks>
///     Budgeted in bytes rather than entries because that is the resource that actually runs out:
///     ten thousand JSON manifests and ten 4 MB textures are the same entry count and three orders
///     of magnitude apart in cost. Eviction is strict LRU under one lock — a cache that is contended
///     enough for that lock to matter is a cache in front of a network, so the lock is never the
///     slow part.
/// </remarks>
public sealed class MemoryCacheStore(long budgetBytes = 32L * 1024 * 1024) : IHttpCacheStore
{
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    /// <summary>Bytes currently held.</summary>
    public long Bytes
    {
        get
        {
            lock (_lock) return _bytes;
        }
    }

    /// <summary>Entries currently held.</summary>
    public int Count
    {
        get
        {
            lock (_lock) return _index.Count;
        }
    }

    /// <inheritdoc />
    public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(key, out var node)) return ValueTask.FromResult<CachedResponse?>(null);
            _lru.Remove(node);
            _lru.AddFirst(node);
            return ValueTask.FromResult<CachedResponse?>(node.Value.Response);
        }
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, CachedResponse entry, CancellationToken ct = default)
    {
        long cost = entry.Body.LongLength + 512; // headers and object overhead, near enough
        lock (_lock)
        {
            RemoveLocked(key);
            var node = _lru.AddFirst(new Entry(key, entry, cost));
            _index[key] = node;
            _bytes += cost;

            while (_bytes > budgetBytes && _lru.Last is { } last) RemoveLocked(last.Value.Key);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken ct = default)
    {
        lock (_lock) RemoveLocked(key);
        return ValueTask.CompletedTask;
    }

    /// <summary>Drop everything.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _index.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    private void RemoveLocked(string key)
    {
        if (!_index.Remove(key, out var node)) return;
        _lru.Remove(node);
        _bytes -= node.Value.Cost;
    }

    private readonly record struct Entry(string Key, CachedResponse Response, long Cost);
}
