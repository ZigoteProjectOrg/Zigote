using Zigote.Core;

namespace Zigote.UI.Widgets;

/// <summary>A single corner radius. Only circular radii are modelled.</summary>
public readonly struct Radius
{
    private Radius(float value) => Value = value;

    public float Value { get; }

    public static Radius Circular(double radius) => new((float)radius);

    public static readonly Radius Zero = new(0f);

    public static implicit operator Radius(double v) => new((float)v);
}

/// <summary>
///     A corner radius. The renderer applies a <b>single uniform</b> corner radius today,
///     so per-corner values collapse to <see cref="Uniform" /> (the largest supplied corner). The
///     factories accept per-corner values; a non-uniform radius renders as its uniform equivalent.
/// </summary>
public readonly struct BorderRadius
{
    public BorderRadius(float topLeft, float topRight, float bottomRight, float bottomLeft)
    {
        TopLeft = topLeft;
        TopRight = topRight;
        BottomRight = bottomRight;
        BottomLeft = bottomLeft;
    }

    public float TopLeft { get; }
    public float TopRight { get; }
    public float BottomRight { get; }
    public float BottomLeft { get; }

    /// <summary>The single radius the renderer applies (max of the four corners).</summary>
    public float Uniform => MathF.Max(
        x: MathF.Max(x: TopLeft, y: TopRight),
        y: MathF.Max(x: BottomRight, y: BottomLeft)
    );

    public static readonly BorderRadius Zero = new(
        topLeft: 0,
        topRight: 0,
        bottomRight: 0,
        bottomLeft: 0
    );

    public static BorderRadius Circular(double radius)
    {
        float r = (float)radius;
        return new BorderRadius(
            topLeft: r,
            topRight: r,
            bottomRight: r,
            bottomLeft: r
        );
    }

    public static BorderRadius All(Radius radius)
    {
        return new BorderRadius(
            topLeft: radius.Value,
            topRight: radius.Value,
            bottomRight: radius.Value,
            bottomLeft: radius.Value
        );
    }

    public static BorderRadius Only(
        double topLeft = 0, double topRight = 0, double bottomRight = 0, double bottomLeft = 0)
    {
        return new BorderRadius(
            topLeft: (float)topLeft,
            topRight: (float)topRight,
            bottomRight: (float)bottomRight,
            bottomLeft: (float)bottomLeft
        );
    }

    public static BorderRadius Vertical(double top = 0, double bottom = 0)
    {
        return new BorderRadius(
            topLeft: (float)top,
            topRight: (float)top,
            bottomRight: (float)bottom,
            bottomLeft: (float)bottom
        );
    }

    public static BorderRadius Horizontal(double left = 0, double right = 0)
    {
        return new BorderRadius(
            topLeft: (float)left,
            topRight: (float)right,
            bottomRight: (float)right,
            bottomLeft: (float)left
        );
    }

    public static implicit operator BorderRadius(double uniform) => Circular(uniform);
}

/// <summary>Colour + width of one edge.</summary>
public readonly struct BorderSide
{
    public BorderSide(Color? color = null, double width = 1.0)
    {
        Color = color ?? Color.Black;
        Width = (float)width;
    }

    public Color Color { get; }
    public float Width { get; }

    public static readonly BorderSide None = new(color: Color.Transparent, width: 0);
}

/// <summary>
///     A box border. The renderer draws a single uniform stroke, so only a symmetric border
///     (as produced by <see cref="All" />) is represented; <see cref="Color" />/<see cref="Width" />
///     expose it.
/// </summary>
public readonly struct Border
{
    public Border(BorderSide side)
    {
        Color = side.Color;
        Width = side.Width;
    }

    public Border(Color color, double width = 1.0)
    {
        Color = color;
        Width = (float)width;
    }

    public Color Color { get; }
    public float Width { get; }

    public static Border All(Color? color = null, double width = 1.0) =>
        new(color: color ?? Color.Black, width: width);

    public static Border FromBorderSide(BorderSide side) => new(side);
}

/// <summary>
///     A box shadow. The renderer approximates a shadow
///     from <see cref="BlurRadius" /> via the elevation buckets (offset/spread are advisory).
/// </summary>
public readonly struct BoxShadow
{
    public BoxShadow(Color? color = null, Offset offset = default, double blurRadius = 0,
        double spreadRadius = 0)
    {
        Color = color ?? new Color(0x33000000);
        Offset = offset;
        BlurRadius = (float)blurRadius;
        SpreadRadius = (float)spreadRadius;
    }

    public Color Color { get; }
    public Offset Offset { get; }
    public float BlurRadius { get; }
    public float SpreadRadius { get; }
}

/// <summary>
///     A box decoration. Flattens onto the engine's <c>Container</c>/<c>DecoratedBox</c>
///     surface props: <see cref="Color" />→fill, <see cref="BorderRadius" />→corner radius,
///     <see cref="Border" />→stroke, <see cref="BoxShadow" />[0]→elevation. <c>gradient</c>/
///     <c>image</c>
///     are not modelled (a solid <see cref="Color" /> is used instead).
/// </summary>
public sealed class BoxDecoration
{
    public BoxDecoration(
        Color? color = null,
        BorderRadius borderRadius = default,
        Border? border = null,
        List<BoxShadow>? boxShadow = null)
    {
        Color = color;
        BorderRadius = borderRadius;
        Border = border;
        BoxShadow = boxShadow;
    }

    public Color? Color { get; }
    public BorderRadius BorderRadius { get; }
    public Border? Border { get; }
    public List<BoxShadow>? BoxShadow { get; }
}
