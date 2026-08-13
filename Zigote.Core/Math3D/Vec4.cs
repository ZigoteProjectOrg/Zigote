using System.Numerics;

namespace Zigote.Core.Math3D;

// Backed by System.Numerics.Vector4 for SIMD arithmetic. Public API, 4-float layout and
// tolerance-based equality preserved exactly (the FFI boundary copies components, not this type).
public readonly struct Vec4 : IEquatable<Vec4>
{
    internal readonly Vector4 V;

    public Vec4(float x, float y, float z, float w)
    {
        V = new Vector4(
            x,
            y,
            z,
            w
        );
    }

    internal Vec4(Vector4 v)
    {
        V = v;
    }

    public float X => V.X;
    public float Y => V.Y;
    public float Z => V.Z;
    public float W => V.W;

    public static readonly Vec4 Zero = new(
        0,
        0,
        0,
        0
    );

    public static readonly Vec4 One = new(
        1,
        1,
        1,
        1
    );

    public static Vec4 Splat(float v)
    {
        return new Vec4(new Vector4(v));
    }

    public static Vec4 operator +(Vec4 a, Vec4 b)
    {
        return new Vec4(a.V + b.V);
    }

    public static Vec4 operator -(Vec4 a, Vec4 b)
    {
        return new Vec4(a.V - b.V);
    }

    public static Vec4 operator *(Vec4 v, float s)
    {
        return new Vec4(v.V * s);
    }

    public static Vec4 operator *(float s, Vec4 v)
    {
        return new Vec4(v.V * s);
    }

    public static Vec4 operator -(Vec4 v)
    {
        return new Vec4(-v.V);
    }

    public float Dot(Vec4 b)
    {
        return Vector4.Dot(V, b.V);
    }

    public Vec3 Xyz()
    {
        return new Vec3(X, Y, Z);
    }

    public float GetComp(int i)
    {
        return i switch { 0 => X, 1 => Y, 2 => Z, 3 => W, _ => 0f };
    }

    public float[] ToArray()
    {
        return [X, Y, Z, W];
    }

    // Exact value equality — consistent with GetHashCode, so Vec4 is safe as a dictionary/set key.
    // For tolerant comparison use ApproxEquals.
    public bool Equals(Vec4 other)
    {
        return V.Equals(other.V);
    }

    /// <summary>Tolerant component-wise comparison; default tolerance suits physics/gameplay.</summary>
    public bool ApproxEquals(Vec4 other, float tolerance = Tolerance.PhysicsValue)
    {
        return Math.Abs(X - other.X) < tolerance &&
               Math.Abs(Y - other.Y) < tolerance &&
               Math.Abs(Z - other.Z) < tolerance &&
               Math.Abs(W - other.W) < tolerance;
    }

    public override bool Equals(object? obj)
    {
        return obj is Vec4 v && Equals(v);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            X,
            Y,
            Z,
            W
        );
    }

    public override string ToString()
    {
        return $"Vec4({X:F3}, {Y:F3}, {Z:F3}, {W:F3})";
    }

    public static bool operator ==(Vec4 a, Vec4 b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Vec4 a, Vec4 b)
    {
        return !a.Equals(b);
    }
}
