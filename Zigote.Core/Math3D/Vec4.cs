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
            x: x,
            y: y,
            z: z,
            w: w
        );
    }

    internal Vec4(Vector4 v) => V = v;

    public float X => V.X;
    public float Y => V.Y;
    public float Z => V.Z;
    public float W => V.W;

    public static readonly Vec4 Zero = new(
        x: 0,
        y: 0,
        z: 0,
        w: 0
    );

    public static readonly Vec4 One = new(
        x: 1,
        y: 1,
        z: 1,
        w: 1
    );

    public static Vec4 Splat(float v) => new(new Vector4(v));

    public static Vec4 operator +(Vec4 a, Vec4 b) => new(a.V + b.V);

    public static Vec4 operator -(Vec4 a, Vec4 b) => new(a.V - b.V);

    public static Vec4 operator *(Vec4 v, float s) => new(v.V * s);

    public static Vec4 operator *(float s, Vec4 v) => new(v.V * s);

    public static Vec4 operator -(Vec4 v) => new(-v.V);

    public float Dot(Vec4 b) => Vector4.Dot(vector1: V, vector2: b.V);

    public Vec3 Xyz() => new(x: X, y: Y, z: Z);

    public float GetComp(int i) => i switch { 0 => X, 1 => Y, 2 => Z, 3 => W, _ => 0f };

    public float[] ToArray() => [X, Y, Z, W];

    // Exact value equality — consistent with GetHashCode, so Vec4 is safe as a dictionary/set key.
    // For tolerant comparison use ApproxEquals.
    public bool Equals(Vec4 other) => V.Equals(other.V);

    /// <summary>Tolerant component-wise comparison; default tolerance suits physics/gameplay.</summary>
    public bool ApproxEquals(Vec4 other, float tolerance = Tolerance.PhysicsValue)
    {
        return Math.Abs(X - other.X) < tolerance &&
               Math.Abs(Y - other.Y) < tolerance &&
               Math.Abs(Z - other.Z) < tolerance &&
               Math.Abs(W - other.W) < tolerance;
    }

    public override bool Equals(object? obj) => obj is Vec4 v && Equals(v);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            value1: X,
            value2: Y,
            value3: Z,
            value4: W
        );
    }

    public override string ToString() => $"Vec4({X:F3}, {Y:F3}, {Z:F3}, {W:F3})";

    public static bool operator ==(Vec4 a, Vec4 b) => a.Equals(b);

    public static bool operator !=(Vec4 a, Vec4 b) => !a.Equals(b);
}
