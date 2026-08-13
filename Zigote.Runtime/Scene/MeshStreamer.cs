using Zigote.Core.Assets;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Demand-driven mesh residency: the <see cref="LodSystem.IResidencySink" /> that loads a mesh
///     node's <c>.zmesh</c> blob off the main thread (via <see cref="AssetManager" /> +
///     <see cref="FileBytesLoader" />) when it comes into range and drops it when it goes out. The
///     blob
///     upload (an FFI call) runs on the main thread when <see cref="Want" /> observes the load has
///     completed — never on the worker. Built-in primitives (<c>#cube</c>…) and non-mesh nodes are
///     skipped (they carry no streamable file).
///     <para>
///         Dependencies are injected (register-path, upload, unload) so the mechanism is
///         headless-testable
///         against a real <see cref="AssetManager" /> without the native engine. Idempotent:
///         <see cref="Want" />
///         acquires once and uploads once; <see cref="Drop" /> releases + hides once.
///     </para>
/// </summary>
public sealed class MeshStreamer : LodSystem.IResidencySink
{
    private readonly AssetManager _assets;
    private readonly Dictionary<int, AssetHandle<byte[]>> _handles = new();
    private readonly Func<SceneNode, AssetId> _resolve;
    private readonly Action<SceneNode> _unload;
    private readonly Action<SceneNode, byte[]> _upload;
    private readonly HashSet<int> _uploaded = [];

    public MeshStreamer(AssetManager assets, Func<SceneNode, AssetId> resolve,
        Action<SceneNode, byte[]> upload, Action<SceneNode> unload)
    {
        _assets = assets;
        _resolve = resolve;
        _upload = upload;
        _unload = unload;
    }

    public void Want(SceneNode node, float distance)
    {
        if (!IsStreamable(node)) return;

        if (!_handles.TryGetValue(key: node.Id, value: out var handle))
        {
            var id = _resolve(node);
            if (id.IsEmpty) return;
            handle = _assets.Acquire(id: id, loader: FileBytesLoader.Instance);
            _handles[node.Id] = handle;
        }

        // Upload once the async read completes (the pump has applied it). Runs on the main thread.
        if (handle.IsLoaded && _uploaded.Add(node.Id))
            _upload(arg1: node, arg2: handle.Value!);
    }

    public void Drop(SceneNode node)
    {
        if (_handles.Remove(key: node.Id, value: out var handle))
            _assets.Release(handle);
        if (_uploaded.Remove(node.Id))
            _unload(node);
    }

    /// <summary>True for a mesh node backed by a streamable <c>.zmesh</c> file (not a built-in primitive).</summary>
    public static bool IsStreamable(SceneNode node)
    {
        return node.Kind == NodeKind.Mesh
               && !string.IsNullOrEmpty(node.MeshPath)
               && !node.MeshPath.StartsWith('#');
    }

    /// <summary>Release every held handle (e.g. on scene close). Does not unload native residency.</summary>
    public void Clear()
    {
        foreach (var h in _handles.Values) _assets.Release(h);
        _handles.Clear();
        _uploaded.Clear();
    }
}
