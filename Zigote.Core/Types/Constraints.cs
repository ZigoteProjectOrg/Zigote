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
        MinWidth = MathF.Max(x: 0f, y: minWidth);
        MaxWidth = MathF.Max(x: MinWidth, y: maxWidth);
        MinHeight = MathF.Max(x: 0f, y: minHeight);
        MaxHeight = MathF.Max(x: MinHeight, y: maxHeight);
    }

    public static Constraints Tight(float width, float height)
    {
        return new Constraints(
            minWidth: width,
            maxWidth: width,
            minHeight: height,
            maxHeight: height
        );
    }

    public static Constraints Loose(float width, float height)
    {
        return new Constraints(
            minWidth: 0,
            maxWidth: width,
            minHeight: 0,
            maxHeight: height
        );
    }

    public static readonly Constraints Unbounded = new();

    public Size Constrain(Size size)
    {
        return new Size(
            width: Math.Clamp(value: size.Width, min: MinWidth, max: MaxWidth),
            height: Math.Clamp(value: size.Height, min: MinHeight, max: MaxHeight)
        );
    }

    public Constraints Deflate(EdgeInsets e)
    {
        return new Constraints(
            minWidth: MathF.Max(x: 0, y: MinWidth - e.Horizontal),
            maxWidth: MathF.Max(x: 0, y: MaxWidth - e.Horizontal),
            minHeight: MathF.Max(x: 0, y: MinHeight - e.Vertical),
            maxHeight: MathF.Max(x: 0, y: MaxHeight - e.Vertical)
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

    public override bool Equals(object? obj) => obj is Constraints c && Equals(c);

    public override int GetHashCode()
    {
        return HashCode.Combine(
            value1: MinWidth,
            value2: MaxWidth,
            value3: MinHeight,
            value4: MaxHeight
        );
    }

    public static bool operator ==(Constraints a, Constraints b) => a.Equals(b);

    public static bool operator !=(Constraints a, Constraints b) => !a.Equals(b);

    public override string ToString() =>
        $"Constraints(w=[{MinWidth},{MaxWidth}] h=[{MinHeight},{MaxHeight}])";
}
