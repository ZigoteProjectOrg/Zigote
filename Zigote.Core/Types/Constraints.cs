using Zigote.Core.Math3D;

namespace Zigote.Core;

public readonly struct Constraints
    : IEquatable<Constraints>
{
    public readonly float MinWidth;
    public readonly float MaxWidth;
    public readonly float MinHeight;
    public readonly float MaxHeight;

    public Constraints(
        float minWidth = 0,
        float maxWidth = float.PositiveInfinity,
        float minHeight = 0,
        float maxHeight = float.PositiveInfinity)
    {
        MinWidth = MathF.Max(0f, minWidth);
        MaxWidth = MathF.Max(MinWidth, maxWidth);
        MinHeight = MathF.Max(0f, minHeight);
        MaxHeight = MathF.Max(MinHeight, maxHeight);
    }

    public static Constraints Tight(float width, float height)
    {
        return new Constraints(
            width,
            width,
            height,
            height
        );
    }

    public static Constraints Loose(float width, float height)
    {
        return new Constraints(
            0,
            width,
            0,
            height
        );
    }

    public static readonly Constraints Unbounded = new();

    public Size Constrain(Size size)
    {
        return new Size(
            Math.Clamp(size.Width, MinWidth, MaxWidth),
            Math.Clamp(size.Height, MinHeight, MaxHeight)
        );
    }

    public Constraints Deflate(EdgeInsets e)
    {
        return new Constraints(
            MathF.Max(0, MinWidth - e.Horizontal),
            MathF.Max(0, MaxWidth - e.Horizontal),
            MathF.Max(0, MinHeight - e.Vertical),
            MathF.Max(0, MaxHeight - e.Vertical)
        );
    }

    public bool Equals(Constraints other)
    {
        // Exact value equality — consistent with GetHashCode, and correct for the measure-cache gate
        // (a sub-tolerance constraint change must re-measure, not silently reuse a stale size). Use
        // ApproxEquals for tolerant comparison.
        return MinWidth.Equals(other.MinWidth) && MaxWidth.Equals(other.MaxWidth) &&
               MinHeight.Equals(other.MinHeight) && MaxHeight.Equals(other.MaxHeight);
    }

    /// <summary>Tolerant component-wise comparison.</summary>
    public bool ApproxEquals(Constraints other, float tolerance = Tolerance.StandardValue)
    {
        return Math.Abs(MinWidth - other.MinWidth) < tolerance &&
               Math.Abs(MaxWidth - other.MaxWidth) < tolerance &&
               Math.Abs(MinHeight - other.MinHeight) < tolerance &&
               Math.Abs(MaxHeight - other.MaxHeight) < tolerance;
    }

    public override bool Equals(object? obj)
    {
        return obj is Constraints c && Equals(c);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            MinWidth,
            MaxWidth,
            MinHeight,
            MaxHeight
        );
    }

    public static bool operator ==(Constraints a, Constraints b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Constraints a, Constraints b)
    {
        return !a.Equals(b);
    }

    public override string ToString()
    {
        return $"Constraints(w=[{MinWidth},{MaxWidth}] h=[{MinHeight},{MaxHeight}])";
    }
}