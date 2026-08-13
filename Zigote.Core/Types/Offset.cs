using Zigote.Core.Math3D;

namespace Zigote.Core;

public readonly struct Offset(float x, float y) : IEquatable<Offset>
{
    public readonly float X = x;
    public readonly float Y = y;

    public static readonly Offset Zero = new(x: 0, y: 0);

    public Offset Translate(float dx, float dy) => new(x: X + dx, y: Y + dy);

    public static Offset operator +(Offset a, Offset b) => new(x: a.X + b.X, y: a.Y + b.Y);

    public static Offset operator -(Offset a, Offset b) => new(x: a.X - b.X, y: a.Y - b.Y);

    // Exact value equality — consistent with GetHashCode. Use ApproxEquals for tolerant comparison.
    public bool Equals(Offset other) => X.Equals(other.X) && Y.Equals(other.Y);

    /// <summary>Tolerant component-wise comparison.</summary>
    public bool ApproxEquals(Offset other, float tolerance = Tolerance.StandardValue)
    {
        return Math.Abs(X - other.X) < tolerance &&
               Math.Abs(Y - other.Y) < tolerance;
    }

    public override bool Equals(object? obj) => obj is Offset o && Equals(o);

    public override int GetHashCode() => HashCode.Combine(value1: X, value2: Y);

    public static bool operator ==(Offset a, Offset b) => a.Equals(b);

    public static bool operator !=(Offset a, Offset b) => !a.Equals(b);

    public override string ToString() => $"Offset({X}, {Y})";
}
