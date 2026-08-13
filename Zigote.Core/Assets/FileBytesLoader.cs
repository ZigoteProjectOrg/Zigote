namespace Zigote.Core.Assets;

/// <summary>
///     The simplest streaming loader: reads a file's raw bytes on the background thread and hands
///     them back unchanged. The building block for demand-loaded <c>.zmesh</c> blobs — the shared,
///     node-independent CPU payload that the scene-residency layer then uploads into each referencing
///     native node via <c>SceneSetMeshBlob</c> on the main thread (the FFI stays out of the cache, so
///     one decoded blob can feed many nodes). Stateless and thread-safe; use <see cref="Instance" />.
/// </summary>
public sealed class FileBytesLoader : IAssetLoader<byte[]>
{
    public static readonly FileBytesLoader Instance = new();

    public object LoadOffThread(AssetId id, string path)
    {
        return File.ReadAllBytes(path);
    }

    public byte[] Apply(AssetId id, object payload)
    {
        return (byte[])payload;
    }

    public void Unload(AssetId id, byte[] value)
    {
        // Plain managed bytes — the GC reclaims them when the entry is dropped. Nothing to release.
    }
}
