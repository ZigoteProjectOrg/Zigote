namespace Zigote.Core.Assets;

/// <summary>
///     Strategy for turning a resolved asset path into a resident value of type
///     <typeparamref name="T" />, split across the streaming pipeline's two threads.
///     <para>
///         <see cref="LoadOffThread" /> runs on a background worker and must be pure CPU work
///         (read/parse/decode) — it may <b>not</b> touch the FFI, the GPU, or any shared engine
///         state, because those are single-threaded. <see cref="Apply" /> and <see cref="Unload" />
///         run on the main thread (the <see cref="AssetManager.Pump" /> and eviction), so they are
///         the only place a loader may call into native/GPU code.
///     </para>
/// </summary>
public interface IAssetLoader<T> where T : class
{
    /// <summary>
    ///     Background thread. Read/parse/decode the file at <paramref name="path" /> into an opaque
    ///     payload handed back to <see cref="Apply" /> on the main thread. Throw to fail the load.
    /// </summary>
    object LoadOffThread(AssetId id, string path);

    /// <summary>
    ///     Main thread. Turn the payload produced by <see cref="LoadOffThread" /> into the resident
    ///     asset. May touch FFI/GPU (e.g. upload a mesh blob). Throw to fail the load.
    /// </summary>
    T Apply(AssetId id, object payload);

    /// <summary>
    ///     Main thread. Release the native/GPU resources held by a resident asset when it is evicted.
    ///     Called by <see cref="AssetManager.EvictUnreferenced" /> for entries no longer referenced.
    /// </summary>
    void Unload(AssetId id, T value);
}