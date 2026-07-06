using Zigote.Core.Math3D;

namespace Zigote.Core;

public readonly struct Offset(float x, float y) : IEquatable<Offset>
{
    public readonly float X = x;
    public readonly float Y = y;

    public static readonly Offset Zero = new(0, 0);

    public Offset Translate(float dx, float dy)
    {
        return new Offset(X + dx, Y + dy);
    }

    public static Offset operator +(Offset a, Offset b)
    {
        return new Offset(a.X + b.X, a.Y + b.Y);
    }

    public static Offset operator -(Offset a, Offset b)
    {
        return new Offset(a.X - b.X, a.Y - b.Y);
    }

    // Exact value equality — consistent with GetHashCode. Use ApproxEquals for tolerant comparison.
    public bool Equals(Offset other)
    {
        return X.Equals(other.X) && Y.Equals(other.Y);
    }

    /// <summary>Tolerant component-wise comparison.</summary>
    public bool ApproxEquals(Offset other, float tolerance = Tolerance.StandardValue)
    {
        return Math.Abs(X - other.X) < tolerance &&
               Math.Abs(Y - other.Y) < tolerance;
    }

    public override bool Equals(object? obj)
    {
        return obj is Offset o && Equals(o);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(X, Y);
    }

    public static bool operator ==(Offset a, Offset b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Offset a, Offset b)
    {
        return !a.Equals(b);
    }

    public override string ToString()
    {
        return $"Offset({X}, {Y})";
    }
}