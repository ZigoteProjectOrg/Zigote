using System.Collections.Concurrent;

namespace Zigote.Core.Assets;

/// <summary>
///     Ref-counted, deduplicating streaming asset cache. Keyed by <c>(AssetId, type)</c>, it turns
///     an <see cref="Acquire{T}" /> into a background load and resolves it on the main thread via
///     <see cref="Pump" />, so a heavy load never stalls the frame that requested it.
///     <list type="bullet">
///         <item><b>Dedup:</b> concurrent <see cref="Acquire{T}" /> for the same id share one load.</item>
///         <item><b>Ref-count:</b> the asset stays resident while any handle references it.</item>
///         <item>
///             <b>Threading:</b> <see cref="Acquire{T}" />/<see cref="Release{T}" />/
///             <see cref="Pump" />/
///             <see cref="EvictUnreferenced" /> run on the main thread; only
///             <see cref="IAssetLoader{T}.LoadOffThread" /> runs on a worker.
///         </item>
///     </list>
///     Path resolution is injected (an <see cref="AssetId" /> → absolute-path delegate) so the
///     manager stays decoupled from the editor's project/content-root notion.
/// </summary>
public sealed class AssetManager
{
    private readonly ConcurrentQueue<Completion> _completed = new();

    private readonly Dictionary<(AssetId, Type), AssetEntry> _entries = new();
    private readonly object _lock = new();
    private readonly Func<AssetId, string?> _resolvePath;
    private long _frame;
    private int _inFlight;

    /// <param name="resolvePath">
    ///     Resolve an <see cref="AssetId" /> to an absolute filesystem path (via
    ///     <see cref="AssetRegistry" /> + content root), or <see langword="null" /> if unknown.
    /// </param>
    public AssetManager(Func<AssetId, string?> resolvePath)
    {
        _resolvePath = resolvePath;
    }

    /// <summary>True while any load is in flight or awaiting a pump — feed this into the app idle gate.</summary>
    public bool WantsFrame => Volatile.Read(ref _inFlight) > 0 || !_completed.IsEmpty;

    /// <summary>Number of live entries (resident, loading, or weakly-held). Diagnostics.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    ///     Get a handle for <paramref name="id" />, starting a background load on first request and
    ///     bumping the ref-count on subsequent ones. Returns immediately; the handle resolves later
    ///     via <see cref="Pump" />. An empty id yields <see cref="AssetHandle{T}.None" />.
    /// </summary>
    public AssetHandle<T> Acquire<T>(AssetId id, IAssetLoader<T> loader) where T : class
    {
        if (id.IsEmpty) return AssetHandle<T>.None;

        AssetEntry<T> entry;
        string? path;
        lock (_lock)
        {
            var key = (id, typeof(T));
            if (_entries.TryGetValue(key, out var existing))
            {
                var typed = (AssetEntry<T>)existing;
                typed.Detached = false; // in the table, therefore live again
                typed.RefCount++;
                typed.LastTouchFrame = _frame;
                // A previously-evicted (Unloaded) or Failed entry that is being re-referenced restarts.
                if (typed.State is AssetLoadState.Unloaded or AssetLoadState.Failed)
                    StartLoad(typed);
                return new AssetHandle<T>(typed);
            }

            entry = new AssetEntry<T>(id, loader) {
                RefCount = 1,
                LastTouchFrame = _frame,
            };
            _entries[key] = entry;
            path = _resolvePath(id);
        }

        BeginLoad(entry, path);
        return new AssetHandle<T>(entry);
    }

    /// <summary>
    ///     Drop a reference. At zero the entry is kept resident (weak) until
    ///     <see cref="EvictUnreferenced" />.
    /// </summary>
    public void Release<T>(AssetHandle<T> handle) where T : class
    {
        if (handle.Entry is not { } entry) return;
        lock (_lock)
        {
            if (entry.RefCount > 0) entry.RefCount--;
            entry.LastTouchFrame = _frame;
        }
    }

    /// <summary>
    ///     Drain completed background loads on the main thread, applying up to
    ///     <paramref name="maxApplies" /> of them (a per-frame budget so a burst never stalls a frame).
    ///     Call once per frame with the current frame index. Returns how many loads were applied, so
    ///     an event-driven host can repaint when something actually landed.
    /// </summary>
    public int Pump(long frame, int maxApplies = int.MaxValue)
    {
        _frame = frame;
        var applied = 0;
        while (applied < maxApplies && _completed.TryDequeue(out var c))
        {
            var entry = c.Entry;
            if (c.Error is not null)
            {
                entry.Error = c.Error;
                entry.State = AssetLoadState.Failed;
                continue;
            }

            // Cancelled while loading (all refs released before the load finished), or the record was
            // dropped from the table entirely (evict / Clear). Either way the payload goes no further.
            //
            // Dropping it raw is safe by the loader contract: LoadOffThread is pure CPU work that may
            // not touch the FFI or the GPU, so a payload holds nothing but managed memory — the
            // native resources only exist after Apply, which is exactly what is being skipped.
            bool wanted;
            lock (_lock)
            {
                wanted = entry.RefCount > 0 && !entry.Detached;
            }

            if (!wanted)
            {
                if (!entry.Detached) entry.State = AssetLoadState.Unloaded;
                continue;
            }

            try
            {
                entry.ApplyLoaded(c.Payload!); // sets Value (plain write) ...
                entry.State = AssetLoadState.Loaded; // ... then publishes State (volatile release)
                applied++;
            }
            catch (Exception e)
            {
                entry.Error = e.Message;
                entry.State = AssetLoadState.Failed;
            }
        }

        return applied;
    }

    /// <summary>
    ///     Unload and forget every entry with no live handles (ref-count 0), oldest-touched first up to
    ///     <paramref name="max" />. Phase-1 policy hook: eviction is explicit (a memory-budget driver
    ///     will call this); a re-<see cref="Acquire{T}" /> reloads from disk.
    /// </summary>
    public int EvictUnreferenced(int max = int.MaxValue)
    {
        lock (_lock)
        {
            var victims = _entries
                .Where(kv => kv.Value.RefCount <= 0)
                .OrderBy(kv => kv.Value.LastTouchFrame)
                .Take(max)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in victims)
            {
                var entry = _entries[key];
                if (entry.State == AssetLoadState.Loaded) entry.UnloadValue();
                entry.State = AssetLoadState.Unloaded;
                entry.Detached = true; // a load still in flight for it must not be applied
                _entries.Remove(key);
            }

            return victims.Count;
        }
    }

    /// <summary>
    ///     Unload everything (e.g. on project close).
    ///     <para>
    ///         Safe against loads still in flight: their records are marked detached under the lock, so
    ///         whichever completions land afterwards are dropped by <see cref="Pump" /> instead of
    ///         being applied onto a record the table no longer holds. Draining the queue alone could
    ///         not do it — a worker that had not finished yet enqueues after the drain.
    ///     </para>
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.State == AssetLoadState.Loaded) entry.UnloadValue();
                entry.State = AssetLoadState.Unloaded;
                entry.Detached = true;
            }

            _entries.Clear();
            while (_completed.TryDequeue(out _))
            {
                // Already-queued payloads: their records are detached above, so nothing to unload.
            }
        }
    }

    // ── internals ─────────────────────────────────────────────────────────────

    private void BeginLoad(AssetEntry entry, string? path)
    {
        if (path is null)
        {
            entry.Error = $"Asset {entry.Id} could not be resolved to a path.";
            entry.State = AssetLoadState.Failed;
            return;
        }

        entry.State = AssetLoadState.Loading;
        Interlocked.Increment(ref _inFlight);
        Task.Run(() =>
            {
                try
                {
                    var payload = entry.LoadOffThread(path);
                    _completed.Enqueue(new Completion(entry, payload, null));
                }
                catch (Exception e)
                {
                    _completed.Enqueue(new Completion(entry, null, e.Message));
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }
        );
    }

    private void StartLoad(AssetEntry entry)
    {
        // Caller holds _lock. Resolve path under the lock (registry is not thread-safe) then dispatch.
        var path = _resolvePath(entry.Id);
        entry.Error = null;
        BeginLoad(entry, path);
    }

    private readonly record struct Completion(AssetEntry Entry, object? Payload, string? Error);
}