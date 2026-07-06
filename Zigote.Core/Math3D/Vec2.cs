using System.Numerics;

namespace Zigote.Core.Math3D;

// Backed by System.Numerics.Vector2 for SIMD arithmetic. Public API, 2-float layout and
// tolerance-based equality preserved exactly.
public readonly struct Vec2 : IEquatable<Vec2>
{
    internal readonly Vector2 V;

    public Vec2(float x, float y)
    {
        V = new Vector2(x, y);
    }

    internal Vec2(Vector2 v)
    {
        V = v;
    }

    public float X => V.X;
    public float Y => V.Y;

    public static readonly Vec2 Zero = new(0, 0);
    public static readonly Vec2 One = new(1, 1);
    public static readonly Vec2 Up = new(0, 1);
    public static readonly Vec2 Right = new(1, 0);

    public static Vec2 Splat(float v)
    {
        return new Vec2(new Vector2(v));
    }

    public static Vec2 operator +(Vec2 a, Vec2 b)
    {
        return new Vec2(a.V + b.V);
    }

    public static Vec2 operator -(Vec2 a, Vec2 b)
    {
        return new Vec2(a.V - b.V);
    }

    public static Vec2 operator *(Vec2 v, float s)
    {
        return new Vec2(v.V * s);
    }

    public static Vec2 operator *(float s, Vec2 v)
    {
        return new Vec2(v.V * s);
    }

    public static Vec2 operator /(Vec2 v, float s)
    {
        return new Vec2(v.V / s);
    }

    public static Vec2 operator -(Vec2 v)
    {
        return new Vec2(-v.V);
    }

    public float Dot(Vec2 b)
    {
        return Vector2.Dot(V, b.V);
    }

    public float LengthSq()
    {
        return V.LengthSquared();
    }

    public float Length()
    {
        return V.Length();
    }

    public float Distance(Vec2 b)
    {
        return Vector2.Distance(V, b.V);
    }

    public Vec2 Normalize()
    {
        var l = Length();
        return l < float.Epsilon ? Zero : new Vec2(V * (1f / l));
    }

    public Vec2 Lerp(Vec2 b, float t)
    {
        return new Vec2(Vector2.Lerp(V, b.V, t));
    }

    public float[] ToArray()
    {
        return [X, Y];
    }

    // Exact value equality — consistent with GetHashCode, so Vec2 is safe as a dictionary/set key.
    // For tolerant comparison use ApproxEquals.
    public bool Equals(Vec2 other)
    {
        return V.Equals(other.V);
    }

    /// <summary>Tolerant component-wise comparison; default tolerance suits physics/gameplay.</summary>
    public bool ApproxEquals(Vec2 other, float tolerance = Tolerance.PhysicsValue)
    {
        return Math.Abs(X - other.X) < tolerance &&
               Math.Abs(Y - other.Y) < tolerance;
    }

    public override bool Equals(object? obj)
    {
        return obj is Vec2 v && Equals(v);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y);
    }

    public override string ToString()
    {
        return $"Vec2({X:F3}, {Y:F3})";
    }

    public static bool operator ==(Vec2 a, Vec2 b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Vec2 a, Vec2 b)
    {
        return !a.Equals(b);
    }
}