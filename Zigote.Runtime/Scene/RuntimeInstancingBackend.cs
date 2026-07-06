using Zigote.Core.Engine;
using Zigote.Scripting;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Backs the <see cref="Instancing" /> provider in play mode. Maps a component's
///     <see cref="Component.EntityId" /> (the scene node's <see cref="SceneNode.Id" />) to the node's
///     native handle and forwards the per-instance matrices straight to the renderer. The data is
///     copied native-side immediately, so the span need only be valid for the call (zero managed
///     copy).
/// </summary>
public sealed class RuntimeInstancingBackend : IInstancingBackend
{
    private readonly HashSet<int> _active = [];
    private readonly Dictionary<int, SceneNode> _byId = new();
    private readonly Dictionary<string, SceneNode> _byName = new();

    public RuntimeInstancingBackend(SceneNode root)
    {
        Index(root);
    }

    public void SetInstances(uint entityId, ReadOnlySpan<float> matrices, int count)
    {
        if (!_byId.TryGetValue((int)entityId, out var node)) return;
        Submit(node, matrices, count);
    }

    public void SetInstances(string nodeName, ReadOnlySpan<float> matrices, int count)
    {
        if (!_byName.TryGetValue(nodeName, out var node)) return;
        Submit(node, matrices, count);
    }

    private void Index(SceneNode node)
    {
        _byId[node.Id] = node;
        _byName.TryAdd(node.Name, node); // first node wins on a name clash
        foreach (var c in node.Children) Index(c);
    }

    private void Submit(SceneNode node, ReadOnlySpan<float> matrices, int count)
    {
        // Handle is assigned the first time the node syncs to native (already done in edit mode
        // before play starts). If it's somehow still 0 this frame, skip — the next frame catches up.
        if (node.Handle == 0) return;
        // Clamp to what the span actually holds (16 floats/instance) so a buggy script can't make
        // native read out of bounds.
        if (count > matrices.Length / 16) count = matrices.Length / 16;
        var n = count < 0 ? 0 : (uint)count;
        if (n > 0) _active.Add(node.Id);
        else _active.Remove(node.Id);
        ZigoteEngine.Instance?.SceneSetMeshInstances(node.Handle, matrices, n);
    }

    /// <summary>Clear every node that was drawing instanced, so edit mode reverts to single draws.</summary>
    public void ClearAll()
    {
        foreach (var id in _active)
            if (_byId.TryGetValue(id, out var node) && node.Handle != 0)
                ZigoteEngine.Instance?.SceneSetMeshInstances(
                    node.Handle,
                    ReadOnlySpan<float>.Empty,
                    0
                );
        _active.Clear();
    }
}