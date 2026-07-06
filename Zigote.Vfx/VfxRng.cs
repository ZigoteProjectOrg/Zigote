using Zigote.Core.Math3D;

namespace Zigote.Vfx;

// Deterministic xorshift32 PRNG. A value type so an emitter's RNG state is trivially copied/reset and a
// seeded run reproduces frame-for-frame — the CPU simulator is both the test oracle and the parity
// reference the future GPU compute kernel must match, so the sequence has to be fixed, not platform RNG.
public struct VfxRng(uint seed)
{
    private uint _state = seed == 0 ? 0x9E3779B9u : seed;

    public uint NextUInt()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>Uniform float in [0, 1).</summary>
    public float NextFloat()
    {
        return (NextUInt() >> 8) * (1f / 16777216f);
    }

    /// <summary>Uniform float in [-1, 1).</summary>
    public float Signed()
    {
        return NextFloat() * 2f - 1f;
    }

    public float Range(float min, float max)
    {
        return min == max ? min : min + (max - min) * NextFloat();
    }

    /// <summary>Uniformly distributed point on the unit sphere.</summary>
    public Vec3 OnUnitSphere()
    {
        var z = Signed();
        var a = NextFloat() * MathF.Tau;
        var r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vec3(r * MathF.Cos(a), r * MathF.Sin(a), z);
    }

    /// <summary>Uniformly distributed point inside the unit sphere.</summary>
    public Vec3 InsideUnitSphere()
    {
        return OnUnitSphere() * MathF.Cbrt(NextFloat());
    }
}