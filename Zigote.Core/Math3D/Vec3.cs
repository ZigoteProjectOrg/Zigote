using System.Numerics;

namespace Zigote.Core.Math3D;

// Backed by System.Numerics.Vector3 so arithmetic lowers to SIMD (SSE/NEON) via the JIT.
// The public API, field layout (3 contiguous floats, X@0/Y@4/Z@8) and tolerance-based equality
// are preserved exactly — the FFI boundary copies X/Y/Z into raw-float structs, never this type.
public readonly struct Vec3 : IEquatable<Vec3>
{
    internal readonly Vector3 V;

    public Vec3(float x, float y, float z)
    {
        V = new Vector3(x, y, z);
    }

    internal Vec3(Vector3 v)
    {
        V = v;
    }

    public float X => V.X;
    public float Y => V.Y;
    public float Z => V.Z;

    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 One = new(1, 1, 1);
    public static readonly Vec3 Up = new(0, 1, 0);
    public static readonly Vec3 Down = new(0, -1, 0);
    public static readonly Vec3 Right = new(1, 0, 0);
    public static readonly Vec3 Left = new(-1, 0, 0);
    public static readonly Vec3 Forward = new(0, 0, -1);
    public static readonly Vec3 Back = new(0, 0, 1);

    public static Vec3 Splat(float v)
    {
        return new Vec3(new Vector3(v));
    }

    public static Vec3 operator +(Vec3 a, Vec3 b)
    {
        return new Vec3(a.V + b.V);
    }

    public static Vec3 operator -(Vec3 a, Vec3 b)
    {
        return new Vec3(a.V - b.V);
    }

    public static Vec3 operator *(Vec3 a, Vec3 b)
    {
        return new Vec3(a.V * b.V);
    }

    public static Vec3 operator *(Vec3 v, float s)
    {
        return new Vec3(v.V * s);
    }

    public static Vec3 operator *(float s, Vec3 v)
    {
        return new Vec3(v.V * s);
    }

    public static Vec3 operator /(Vec3 v, float s)
    {
        return new Vec3(v.V / s);
    }

    public static Vec3 operator -(Vec3 v)
    {
        return new Vec3(-v.V);
    }

    public float Dot(Vec3 b)
    {
        return Vector3.Dot(V, b.V);
    }

    public float LengthSq()
    {
        return V.LengthSquared();
    }

    public float Length()
    {
        return V.Length();
    }

    public float Distance(Vec3 b)
    {
        return Vector3.Distance(V, b.V);
    }

    public Vec3 Cross(Vec3 b)
    {
        return new Vec3(Vector3.Cross(V, b.V));
    }

    public Vec3 Normalize()
    {
        var l = Length();
        return l < float.Epsilon ? Zero : new Vec3(V * (1f / l));
    }

    public Vec3 Lerp(Vec3 b, float t)
    {
        return new Vec3(Vector3.Lerp(V, b.V, t));
    }

    public Vec3 Reflect(Vec3 n)
    {
        return this - n * (2f * Dot(n));
    }

    public Vec4 ToVec4(float w)
    {
        return new Vec4(
            X,
            Y,
            Z,
            w
        );
    }

    public float[] ToArray()
    {
        return [X, Y, Z];
    }

    // Exact value equality — consistent with GetHashCode, so Vec3 is safe as a dictionary/set key.
    // For tolerant comparison (physics/scene-sync change detection etc.) use ApproxEquals.
    public bool Equals(Vec3 other)
    {
        return V.Equals(other.V);
    }

    /// <summary>Tolerant component-wise comparison; default tolerance suits physics/gameplay.</summary>
    public bool ApproxEquals(Vec3 other, float tolerance = Tolerance.PhysicsValue)
    {
        return Math.Abs(X - other.X) < tolerance &&
               Math.Abs(Y - other.Y) < tolerance &&
               Math.Abs(Z - other.Z) < tolerance;
    }

    public override bool Equals(object? obj)
    {
        return obj is Vec3 v && Equals(v);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Z);
    }

    public override string ToString()
    {
        return $"Vec3({X:F3}, {Y:F3}, {Z:F3})";
    }

    public static bool operator ==(Vec3 a, Vec3 b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Vec3 a, Vec3 b)
    {
        return !a.Equals(b);
    }
}
