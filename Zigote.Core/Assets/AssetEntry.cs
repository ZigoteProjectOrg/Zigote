namespace Zigote.Core.Assets;

/// <summary>
///     The shared, heap-allocated residency record for one <see cref="AssetId" /> of a given type.
///     One is created on first <see cref="AssetManager.Acquire{T}" /> and lives until evicted.
///     <see cref="AssetHandle{T}" /> is a thin value wrapper over this record, so handle reads are
///     allocation-free. The non-generic base lets <see cref="AssetManager" /> store mixed types in
///     one dictionary and drive load/apply/unload type-erased.
/// </summary>
internal abstract class AssetEntry
{
    public string? Error;
    public AssetId Id;

    /// <summary>Frame index of the last <see cref="AssetManager.Acquire{T}" /> — LRU ordering key.</summary>
    public long LastTouchFrame;

    /// <summary>Number of live handles keeping this resident. Mutated only on the main thread.</summary>
    public int RefCount;

    /// <summary>
    ///     This record is no longer in the manager's table — evicted, or dropped by
    ///     <see cref="AssetManager.Clear" /> — so a load still in flight for it must be discarded
    ///     rather than applied.
    ///     <para>
    ///         Without this, a load that finished after its entry was dropped would still be applied,
    ///         producing a resident value (a GPU upload, in the loaders that matter) attached to a
    ///         record nothing tracks: unreachable from the table, so no later evict or clear could
    ///         ever unload it. Set and read under the manager's lock.
    ///     </para>
    /// </summary>
    public bool Detached;

    /// <summary>Written by the main-thread pump/evict with release semantics; readable from any thread.</summary>
    public volatile AssetLoadState State;

    /// <summary>Background thread: produce the opaque payload for <see cref="ApplyLoaded" />.</summary>
    public abstract object LoadOffThread(string path);

    /// <summary>Main thread: turn the payload into the resident value (sets it before the caller flips State).</summary>
    public abstract void ApplyLoaded(object payload);

    /// <summary>Main thread: release the resident value's native/GPU resources.</summary>
    public abstract void UnloadValue();
}

internal sealed class AssetEntry<T> : AssetEntry where T : class
{
    public readonly IAssetLoader<T> Loader;
    public T? Value;

    public AssetEntry(AssetId id, IAssetLoader<T> loader)
    {
        Id = id;
        Loader = loader;
    }

    public override object LoadOffThread(string path)
    {
        return Loader.LoadOffThread(Id, path);
    }

    public override void ApplyLoaded(object payload)
    {
        // Plain write; the caller publishes State with a release write AFTER this returns, so a
        // handle reader that observes State==Loaded is guaranteed to see this Value.
        Value = Loader.Apply(Id, payload);
    }

    public override void UnloadValue()
    {
        if (Value is null) return;
        Loader.Unload(Id, Value);
        Value = null;
    }
}
