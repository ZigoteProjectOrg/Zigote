using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     A single-child paint primitive: an optional drop shadow, a rounded fill, and a hairline border,
///     painted behind its child. This is the composition building block controls use instead of
///     hand-rolling the same <c>AddElevation</c>/<c>AddRect</c>/<c>AddBorder</c> block — a surface,
///     button background, chip, or checkbox box is a <see cref="DecoratedBox" /> with the right
///     tokens.
///     Sizes to its child (or to the constraints' minimum when childless), like
///     <see cref="ColoredBox" />.
/// </summary>
public sealed class DecoratedBox : Widget
{
    private Size _size;

    public Widget? Child { get; set; }
    public Color Fill { get; set; } = Color.Transparent;
    public float Radius { get; set; }
    public Color BorderColor { get; set; } = Color.Transparent;
    public float BorderWidth { get; set; } = 1f;

    /// <summary>Optional soft drop shadow drawn under the fill. Null disables it.</summary>
    public ShadowStyle? Elevation { get; set; }

    // The elevation shadow paints beyond Bounds (blur + spread, shifted down by OffsetY). Expand the
    // damage region to cover it so a partial repaint driven by this box never leaves a stale shadow halo.
    public override Rect DamageBounds =>
        Elevation is { IsNone: false } e
            ? Bounds.Inflate(e.Blur + e.Spread + MathF.Abs(e.OffsetY))
            : Bounds;

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? c.Constrain(Size.Zero);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        if (Elevation is { } e)
            paint.AddElevation(Bounds, Radius, e);
        if (Fill.A > 0f)
            paint.AddRect(Bounds, Fill, Radius);
        if (BorderColor.A > 0f)
            paint.AddBorder(
                Bounds,
                BorderColor,
                Radius,
                BorderWidth
            );

        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            Fill,
            BorderColor,
            Radius,
            Elevation,
            Child?.DebugStateHash() ?? 0
        );
    }
}