using System.Numerics;

namespace Zigote.Core.Math3D;

// Backed by System.Numerics.Vector2 for SIMD arithmetic. Public API, 2-float layout and
// tolerance-based equality preserved exactly.
public readonly struct Vec2 : IEquatable<Vec2>
{
    internal readonly Vector2 V;

    public Vec2(float x, float y) => V = new Vector2(x: x, y: y);

    internal Vec2(Vector2 v) => V = v;

    public float X => V.X;
    public float Y => V.Y;

    public static readonly Vec2 Zero = new(x: 0, y: 0);
    public static readonly Vec2 One = new(x: 1, y: 1);
    public static readonly Vec2 Up = new(x: 0, y: 1);
    public static readonly Vec2 Right = new(x: 1, y: 0);

    public static Vec2 Splat(float v) => new(new Vector2(v));

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.V + b.V);

    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.V - b.V);

    public static Vec2 operator *(Vec2 v, float s) => new(v.V * s);

    public static Vec2 operator *(float s, Vec2 v) => new(v.V * s);

    public static Vec2 operator /(Vec2 v, float s) => new(v.V / s);

    public static Vec2 operator -(Vec2 v) => new(-v.V);

    public float Dot(Vec2 b) => Vector2.Dot(value1: V, value2: b.V);

    public float LengthSq() => V.LengthSquared();

    public float Length() => V.Length();

    public float Distance(Vec2 b) => Vector2.Distance(value1: V, value2: b.V);

    public Vec2 Normalize()
    {
        float l = Length();
        return l < float.Epsilon ? Zero : new Vec2(V * (1f / l));
    }

    public Vec2 Lerp(Vec2 b, float t) => new(Vector2.Lerp(value1: V, value2: b.V, amount: t));

    public float[] ToArray() => [X, Y];

    // Exact value equality — consistent with GetHashCode, so Vec2 is safe as a dictionary/set key.
    // For tolerant comparison use ApproxEquals.
    public bool Equals(Vec2 other) => V.Equals(other.V);

    /// <summary>Tolerant component-wise comparison; default tolerance suits physics/gameplay.</summary>
    public bool ApproxEquals(Vec2 other, float tolerance = Tolerance.PhysicsValue)
    {
        return Math.Abs(X - other.X) < tolerance &&
               Math.Abs(Y - other.Y) < tolerance;
    }

    public override bool Equals(object? obj) => obj is Vec2 v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(value1: X, value2: Y);

    public override string ToString() => $"Vec2({X:F3}, {Y:F3})";

    public static bool operator ==(Vec2 a, Vec2 b) => a.Equals(b);

    public static bool operator !=(Vec2 a, Vec2 b) => !a.Equals(b);
}
