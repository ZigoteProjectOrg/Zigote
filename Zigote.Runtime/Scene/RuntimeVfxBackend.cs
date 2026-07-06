using Zigote.Core.Math3D;
using Zigote.Scripting;
using Zigote.Vfx;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Backs the generic <see cref="Vfx" /> scripting API in play mode: owns a
///     <see cref="CpuParticleSimulator" /> per script-spawned emitter, stepped in <c>GameSession</c>'s
///     fixed loop and drawn by the viewport alongside editor-authored <see cref="NodeKind.VfxEmitter" />
///     nodes. Mirrors <c>RuntimeAudioBackend</c> / <c>RuntimeInstancingBackend</c>. Fire-and-forget emitters
///     (non-looping, finished, empty) are reaped automatically.
/// </summary>
public sealed class RuntimeVfxBackend : IVfxBackend
{
    private readonly Dictionary<uint, CpuParticleSimulator> _emitters = new();
    private uint _nextId = 1;

    public IReadOnlyDictionary<uint, CpuParticleSimulator> Emitters => _emitters;

    public VfxHandle Create(VfxEmitterAsset asset, Vec3 position)
    {
        var id = _nextId++;
        _emitters[id] = new CpuParticleSimulator(asset) { Position = position };
        return new VfxHandle(id);
    }

    public void SetPosition(VfxHandle handle, Vec3 position)
    {
        if (_emitters.TryGetValue(handle.Id, out var sim)) sim.Position = position;
    }

    public void SetEmitting(VfxHandle handle, bool emitting)
    {
        if (_emitters.TryGetValue(handle.Id, out var sim)) sim.Emitting = emitting;
    }

    public void Burst(VfxHandle handle, int count)
    {
        if (_emitters.TryGetValue(handle.Id, out var sim)) sim.Burst(count);
    }

    public void Destroy(VfxHandle handle)
    {
        _emitters.Remove(handle.Id);
    }

    /// <summary>Step every script emitter one slice; reap any that have finished emitting and emptied.</summary>
    public void Step(float dt)
    {
        if (_emitters.Count == 0) return;
        List<uint>? dead = null;
        foreach (var (id, sim) in _emitters)
        {
            sim.Tick(dt);
            if (!sim.IsAlive) (dead ??= []).Add(id);
        }

        if (dead == null) return;
        foreach (var id in dead) _emitters.Remove(id);
    }
}