using Zigote.Core.Math3D;
using Zigote.Runtime.Scene;
using Zigote.Vfx;

namespace Zigote.Runtime.Vfx;

/// <summary>
///     Drives the editor-authored <see cref="NodeKind.VfxEmitter" /> nodes in play mode: builds a
///     <see cref="CpuParticleSimulator" /> per emitter from its compiled graph, glues each to its
///     node's
///     world transform, and steps them in <c>GameSession</c>'s fixed-timestep loop. The viewport reads
///     the
///     live pools to draw the particles. Game-agnostic, like the AudioSource path — no native required
///     (the native GPU render pass replaces the viewport 2D-projection draw in a later phase).
/// </summary>
public sealed class VfxScenePlayback
{
    private readonly List<(SceneNode node, CpuParticleSimulator sim)> _emitters = [];

    // Parallel GPU-compute driver per emitter (emission timing only — the GPU owns the particle state).
    // Built from the same compiled asset; the viewport drives these at render-rate when render.vfx_gpu is on.
    private readonly List<(SceneNode node, VfxGpuEmitter gpu)> _gpuEmitters = [];

    public IReadOnlyList<(SceneNode node, CpuParticleSimulator sim)> Emitters => _emitters;
    public IReadOnlyList<(SceneNode node, VfxGpuEmitter gpu)> GpuEmitters => _gpuEmitters;

    public void Build(SceneNode root)
    {
        _emitters.Clear();
        _gpuEmitters.Clear();
        Walk(root);
    }

    /// <summary>Register any VfxEmitter nodes in a subtree spawned mid-play (World.Spawn).</summary>
    public void Add(SceneNode subtree)
    {
        Walk(subtree);
    }

    /// <summary>Drop the simulators of any VfxEmitter nodes in a subtree destroyed mid-play.</summary>
    public void Remove(SceneNode subtree)
    {
        _emitters.RemoveAll(e => IsUnder(e.node, subtree));
        _gpuEmitters.RemoveAll(e => IsUnder(e.node, subtree));
    }

    private static bool IsUnder(SceneNode node, SceneNode subtree)
    {
        for (var n = node; n != null; n = n.Parent)
            if (ReferenceEquals(n, subtree))
                return true;
        return false;
    }

    private void Walk(SceneNode node)
    {
        if (node.Kind == NodeKind.VfxEmitter)
        {
            var asset = VfxAssets.Resolve(node);
            _emitters.Add(
                (node, new CpuParticleSimulator(asset) { Emitting = node.VfxPlayOnStart })
            );
            _gpuEmitters.Add((node, new VfxGpuEmitter(asset) { Emitting = node.VfxPlayOnStart }));
        }

        foreach (var c in node.Children) Walk(c);
    }

    public void Step(float dt)
    {
        foreach (var (node, sim) in _emitters)
        {
            var world = WorldTransform(node);
            sim.Position = world.Position;
            sim.Orientation = world.Rotation;
            sim.Tick(dt);
        }
    }

    public void Reset()
    {
        _emitters.Clear();
        _gpuEmitters.Clear();
    }

    private static Transform3D WorldTransform(SceneNode node)
    {
        var local = new Transform3D(node.Position, node.Rotation, node.Scale);
        return node.Parent is { } parent
            ? Transform3D.Combine(WorldTransform(parent), local)
            : local;
    }
}