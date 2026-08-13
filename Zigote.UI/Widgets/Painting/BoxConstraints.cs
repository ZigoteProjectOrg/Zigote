using Zigote.Core;

namespace Zigote.UI.Widgets;

/// <summary>
///     Min/max width and height. Implicitly converts to the engine's
///     <see cref="Constraints" />, so
///     <c>ConstrainedBox(constraints: new BoxConstraints(maxWidth: 200))</c>
///     works directly. (<see cref="Constraints" />'s own constructor already uses the same
///     <c>minWidth/maxWidth/minHeight/maxHeight</c> parameter names.)
/// </summary>
public readonly struct BoxConstraints
{
    public BoxConstraints(
        double minWidth = 0,
        double maxWidth = double.PositiveInfinity,
        double minHeight = 0,
        double maxHeight = double.PositiveInfinity)
    {
        MinWidth = (float)minWidth;
        MaxWidth = (float)maxWidth;
        MinHeight = (float)minHeight;
        MaxHeight = (float)maxHeight;
    }

    public float MinWidth { get; }
    public float MaxWidth { get; }
    public float MinHeight { get; }
    public float MaxHeight { get; }

    /// <summary>Both dimensions forced to <paramref name="width" /> × <paramref name="height" />.</summary>
    public static BoxConstraints Tight(Size size)
    {
        return new BoxConstraints(
            size.Width,
            size.Width,
            size.Height,
            size.Height
        );
    }

    /// <summary><c>tightFor</c>: pin the given axes, leave the others free.</summary>
    public static BoxConstraints TightFor(double? width = null, double? height = null)
    {
        return new BoxConstraints(
            width ?? 0,
            width ?? double.PositiveInfinity,
            height ?? 0,
            height ?? double.PositiveInfinity
        );
    }

    /// <summary><c>loose</c>: 0 → the given size.</summary>
    public static BoxConstraints Loose(Size size)
    {
        return new BoxConstraints(
            0,
            size.Width,
            0,
            size.Height
        );
    }

    /// <summary><c>expand</c>: force to the given size or infinity.</summary>
    public static BoxConstraints Expand(double? width = null, double? height = null)
    {
        return new BoxConstraints(
            width ?? double.PositiveInfinity,
            width ?? double.PositiveInfinity,
            height ?? double.PositiveInfinity,
            height ?? double.PositiveInfinity
        );
    }

    public Constraints ToConstraints()
    {
        return new Constraints(
            MinWidth,
            MaxWidth,
            MinHeight,
            MaxHeight
        );
    }

    public static implicit operator Constraints(BoxConstraints b)
    {
        return b.ToConstraints();
    }
}
