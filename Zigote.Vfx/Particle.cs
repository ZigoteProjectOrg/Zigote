using Zigote.Core;
using Zigote.Core.Math3D;

namespace Zigote.Vfx;

// A live particle. Intentionally a MUTABLE struct: the pool stores these contiguously and the simulator
// mutates them in place through `ref`, which is what keeps the steady-state step zero-alloc (no boxing,
// no per-particle objects). `StartSize`/`StartColor` are kept so over-life modules can modulate from the
// birth value rather than the (already-modulated) current value.
public struct Particle
{
    public Vec3 Position;
    public Vec3 Velocity;
    public float Age;
    public float Lifetime;
    public float Size;
    public float StartSize;
    public float Rotation;
    public float AngularVelocity;
    public Color Color;
    public Color StartColor;
    public uint Seed;

    public readonly float NormalizedAge =>
        Lifetime > 0f ? Math.Clamp(value: Age / Lifetime, min: 0f, max: 1f) : 1f;
}
