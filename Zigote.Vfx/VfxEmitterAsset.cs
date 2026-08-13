using Zigote.Core;
using Zigote.Core.Math3D;

namespace Zigote.Vfx;

public enum SimulationSpace
{
    /// <summary>Particles live in world space and are unaffected by later emitter motion.</summary>
    World,

    /// <summary>Particles store emitter-local positions; the consumer applies the emitter transform.</summary>
    Local,
}

public enum VfxBlendMode
{
    Additive,
    AlphaBlend,
}

public enum EmissionShape
{
    Point,
    Sphere,
    Hemisphere,
    Box,
    Cone,
    Circle,
}

/// <summary>
///     A one-shot spawn of <see cref="Count" /> particles at <see cref="Time" /> within an
///     emitter cycle.
/// </summary>
public readonly struct VfxBurst(float time, int count)
{
    public readonly float Time = time;
    public readonly int Count = count;
}

/// <summary>
///     The backend-agnostic "module stack" a VFX graph compiles to. The CPU simulator (now) and the
///     future
///     GPU compute kernel are both pure consumers of this — the node graph never talks to either
///     simulator
///     directly, it only produces a <see cref="VfxEmitterAsset" />. The emitter "main" block
///     (capacity,
///     spawn, emission shape, initial values) plus an ordered <see cref="UpdateModules" /> list mirror
///     the
///     Unity-VFX / Niagara model and lower cleanly to a per-particle GPU update kernel.
/// </summary>
public sealed class VfxEmitterAsset
{
    // ── Render ───────────────────────────────────────────────────────────────
    public VfxBlendMode Blend = VfxBlendMode.Additive;

    // ── Emitter ──────────────────────────────────────────────────────────────
    public int Capacity = 1024;
    public float ConeAngleDegrees = 25f;

    /// <summary>Cycle length in seconds; 0 = infinite (bursts fire once).</summary>
    public float Duration = 0f;

    /// <summary>Local emission axis (the direction particles are launched along).</summary>
    public Vec3 EmitDirection = Vec3.Up;

    public bool Looping = true;
    public uint Seed = 0x1234_5678;

    // ── Emission shape ───────────────────────────────────────────────────────
    public EmissionShape Shape = EmissionShape.Cone;
    public Vec3 ShapeBoxHalfExtents = new(0.5f, 0.5f, 0.5f);
    public float ShapeRadius = 0.25f;
    public bool SoftParticles = true;

    public SimulationSpace Space = SimulationSpace.World;

    // ── Spawn ────────────────────────────────────────────────────────────────
    /// <summary>Continuous emission rate in particles/second.</summary>
    public float SpawnRate = 24f;

    public FloatRange StartAngularVelocity = new(0f, 0f);

    /// <summary>Birth colour; a random lerp toward <see cref="StartColorVariation" /> (equal = constant).</summary>
    public Color StartColor = Color.White;

    public Color StartColorVariation = Color.White;

    // ── Initial particle values ──────────────────────────────────────────────
    public FloatRange StartLifetime = new(1.5f, 2.5f);
    public FloatRange StartRotation = new(0f, 0f);
    public FloatRange StartSize = new(0.15f, 0.3f);
    public FloatRange StartSpeed = new(2f, 4f);
    public string? TexturePath;

    public List<VfxBurst> Bursts { get; } = [];

    // ── Update modules (applied in order, every step) ────────────────────────
    public List<VfxUpdateModule> UpdateModules { get; } = [];
}
