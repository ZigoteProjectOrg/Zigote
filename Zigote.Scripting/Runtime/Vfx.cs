using Zigote.Core.Math3D;
using Zigote.Vfx;

namespace Zigote.Scripting;

/// <summary>A lightweight, copyable handle to a script-spawned particle emitter owned by the host.</summary>
public readonly struct VfxHandle(uint id) : IEquatable<VfxHandle>
{
    public static VfxHandle None => new(0);

    public uint Id { get; } = id;
    public bool IsValid => Id != 0;

    public bool Equals(VfxHandle other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is VfxHandle h && Equals(h);

    public override int GetHashCode() => (int)Id;

    public static bool operator ==(VfxHandle a, VfxHandle b) => a.Id == b.Id;

    public static bool operator !=(VfxHandle a, VfxHandle b) => a.Id != b.Id;
}

/// <summary>
///     The contract the host (editor play session / game runtime) implements to back the generic
///     <see cref="Vfx" /> scripting API with a real particle runtime. Strongly-typed (not multiplexed
///     delegates) so it stays debuggable and a headless test can inject a fake. Mirrors
///     <see cref="IAudioBackend" /> / <see cref="IInstancingBackend" />.
/// </summary>
public interface IVfxBackend
{
    /// <summary>Spawn a runtime emitter from a <see cref="VfxEmitterAsset" /> at a world position.</summary>
    VfxHandle Create(VfxEmitterAsset asset, Vec3 position);

    void SetPosition(VfxHandle handle, Vec3 position);

    /// <summary>Enable/disable continuous emission (live particles keep simulating either way).</summary>
    void SetEmitting(VfxHandle handle, bool emitting);

    /// <summary>Spawn <paramref name="count" /> particles immediately (a one-shot burst).</summary>
    void Burst(VfxHandle handle, int count);

    /// <summary>Stop + free the emitter and its particles.</summary>
    void Destroy(VfxHandle handle);
}

/// <summary>
///     Generic particle/VFX access for scripts: a game <see cref="Component" /> spawns and drives
///     emitters
///     from a <see cref="VfxEmitterAsset" /> (build one in code, or load one your game ships).
///     Engine-generic
///     — it knows nothing about the editor. The host assigns <see cref="Backend" /> in play mode (and
///     clears
///     it on stop); outside play every call is a safe no-op. Mirrors <see cref="Input" />/
///     <see cref="Audio" />.
///     <para>
///         Emitters are load-once / control-explicitly — the script owns their lifetime and should
///         <see cref="Destroy" /> them in <c>OnDestroy</c>. The host simulates them on the CPU and
///         renders
///         them (2D overlay or the native GPU billboard pass).
///     </para>
/// </summary>
public static class Vfx
{
    /// <summary>Set by the host (or a test) to route calls to a real particle runtime.</summary>
    public static IVfxBackend? Backend { get; set; }

    public static bool IsAvailable => Backend != null;

    public static VfxHandle Create(VfxEmitterAsset asset, Vec3 position) =>
        Backend?.Create(asset: asset, position: position) ?? VfxHandle.None;

    public static void SetPosition(VfxHandle handle, Vec3 position) =>
        Backend?.SetPosition(handle: handle, position: position);

    public static void SetEmitting(VfxHandle handle, bool emitting) =>
        Backend?.SetEmitting(handle: handle, emitting: emitting);

    public static void Burst(VfxHandle handle, int count) =>
        Backend?.Burst(handle: handle, count: count);

    public static void Destroy(VfxHandle handle) => Backend?.Destroy(handle);
}
