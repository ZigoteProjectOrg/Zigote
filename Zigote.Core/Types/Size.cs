using Zigote.Core.Math3D;

namespace Zigote.Core;

public readonly struct Size(float width, float height) : IEquatable<Size>
{
    public readonly float Width = width;
    public readonly float Height = height;

    public static readonly Size Zero = new(width: 0, height: 0);

    public static readonly Size Infinite = new(
        width: float.PositiveInfinity,
        height: float.PositiveInfinity
    );

    public bool IsFinite => float.IsFinite(Width) && float.IsFinite(Height);

    public bool Equals(Size other)
    {
        // Exact value equality — consistent with GetHashCode. Use ApproxEquals for tolerant comparison.
        return Width.Equals(other.Width) && Height.Equals(other.Height);
    }

    /// <summary>Tolerant component-wise comparison.</summary>
    public bool ApproxEquals(Size other, float tolerance = Tolerance.StandardValue)
    {
        return Math.Abs(Width - other.Width) < tolerance &&
               Math.Abs(Height - other.Height) < tolerance;
    }

    public override bool Equals(object? obj) => obj is Size s && Equals(s);

    public override int GetHashCode() => HashCode.Combine(value1: Width, value2: Height);

    public static bool operator ==(Size a, Size b) => a.Equals(b);

    public static bool operator !=(Size a, Size b) => !a.Equals(b);

    public override string ToString() => $"Size({Width}×{Height})";
}
