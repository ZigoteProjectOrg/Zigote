using System.Numerics;

namespace Zigote.Core.Math3D;

// Backed by System.Numerics.Vector3 so arithmetic lowers to SIMD (SSE/NEON) via the JIT.
// The public API, field layout (3 contiguous floats, X@0/Y@4/Z@8) and tolerance-based equality
// are preserved exactly — the FFI boundary copies X/Y/Z into raw-float structs, never this type.
public readonly struct Vec3 : IEquatable<Vec3>
{
    internal readonly Vector3 V;

    public Vec3(float x, float y, float z) => V = new Vector3(x: x, y: y, z: z);

    internal Vec3(Vector3 v) => V = v;

    public float X => V.X;
    public float Y => V.Y;
    public float Z => V.Z;

    public static readonly Vec3 Zero = new(x: 0, y: 0, z: 0);
    public static readonly Vec3 One = new(x: 1, y: 1, z: 1);
    public static readonly Vec3 Up = new(x: 0, y: 1, z: 0);
    public static readonly Vec3 Down = new(x: 0, y: -1, z: 0);
    public static readonly Vec3 Right = new(x: 1, y: 0, z: 0);
    public static readonly Vec3 Left = new(x: -1, y: 0, z: 0);
    public static readonly Vec3 Forward = new(x: 0, y: 0, z: -1);
    public static readonly Vec3 Back = new(x: 0, y: 0, z: 1);

    public static Vec3 Splat(float v) => new(new Vector3(v));

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.V + b.V);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.V - b.V);

    public static Vec3 operator *(Vec3 a, Vec3 b) => new(a.V * b.V);

    public static Vec3 operator *(Vec3 v, float s) => new(v.V * s);

    public static Vec3 operator *(float s, Vec3 v) => new(v.V * s);

    public static Vec3 operator /(Vec3 v, float s) => new(v.V / s);

    public static Vec3 operator -(Vec3 v) => new(-v.V);

    public float Dot(Vec3 b) => Vector3.Dot(vector1: V, vector2: b.V);

    public float LengthSq() => V.LengthSquared();

    public float Length() => V.Length();

    public float Distance(Vec3 b) => Vector3.Distance(value1: V, value2: b.V);

    public Vec3 Cross(Vec3 b) => new(Vector3.Cross(vector1: V, vector2: b.V));

    public Vec3 Normalize()
    {
        float l = Length();
        return l < float.Epsilon ? Zero : new Vec3(V * (1f / l));
    }

    public Vec3 Lerp(Vec3 b, float t) => new(Vector3.Lerp(value1: V, value2: b.V, amount: t));

    public Vec3 Reflect(Vec3 n) => this - (n * (2f * Dot(n)));

    public Vec4 ToVec4(float w)
    {
        return new Vec4(
            x: X,
            y: Y,
            z: Z,
            w: w
        );
    }

    public float[] ToArray() => [X, Y, Z];

    // Exact value equality — consistent with GetHashCode, so Vec3 is safe as a dictionary/set key.
    // For tolerant comparison (physics/scene-sync change detection etc.) use ApproxEquals.
    public bool Equals(Vec3 other) => V.Equals(other.V);

    /// <summary>Tolerant component-wise comparison; default tolerance suits physics/gameplay.</summary>
    public bool ApproxEquals(Vec3 other, float tolerance = Tolerance.PhysicsValue)
    {
        return Math.Abs(X - other.X) < tolerance &&
               Math.Abs(Y - other.Y) < tolerance &&
               Math.Abs(Z - other.Z) < tolerance;
    }

    public override bool Equals(object? obj) => obj is Vec3 v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(value1: X, value2: Y, value3: Z);

    public override string ToString() => $"Vec3({X:F3}, {Y:F3}, {Z:F3})";

    public static bool operator ==(Vec3 a, Vec3 b) => a.Equals(b);

    public static bool operator !=(Vec3 a, Vec3 b) => !a.Equals(b);
}
