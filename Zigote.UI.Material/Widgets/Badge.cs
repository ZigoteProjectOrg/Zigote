using Zigote.UI.TextShaping;

namespace Zigote.UI.Material;

/// <summary>
///     Overlays a small accent count pill on the top-right corner of its child.
///     A flat, macOS-style badge: capsule shape, white caption text on
///     <see cref="Color" /> (defaults to the theme accent). Hides when
///     <see cref="Count" /> is 0; collapses to a circle for single digits.
/// </summary>
public sealed class Badge(Widget? child = null, int count = 0) : RenderWidget
{
    private Size _childSize;
    private ThemeData _theme = ThemeData.Dark;

    public Widget? Child { get; set; } = child;
    public int Count { get; set; } = count;
    public Color? Color { get; set; }

    public override int DebugStateHash()
    {
        return Count;
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _childSize = Child?.Measure(c) ?? new Size(0, 0);
        return _childSize;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _childSize.Width,
            _childSize.Height
        );
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        Child?.Paint(paint);
        if (Count <= 0) return;

        var text = Count > 99 ? "99+" : Count.ToString();
        var fs = _theme.FontSizeCaption;
        var textSize = TextMeasure.Measure(text, fs, FontWeight.Bold);

        // Capsule pill: fixed compact height; min width equals height so single digits are a circle.
        var bh = MathF.Max(16f, textSize.Height + Spacing.Xxs);
        var bw = MathF.Max(bh, textSize.Width + Spacing.Sm);
        // Anchor the pill to the child's top-right corner (its centre sits on the corner).
        var bx = Bounds.Right - bw * 0.5f;
        var by = Bounds.Y - bh * 0.5f;

        // Explicit half-height radius → guaranteed capsule regardless of renderer radius clamping.
        paint.AddRect(
            new Rect(
                bx,
                by,
                bw,
                bh
            ),
            Color ?? _theme.Primary,
            bh * 0.5f
        );

        var tx = bx + (bw - textSize.Width) / 2f;
        var ty = by + (bh - textSize.Height) / 2f + fs * 0.8f;
        paint.AddText(
            text,
            tx,
            ty,
            _theme.OnPrimary,
            fs,
            fontWeight: FontWeight.Bold
        );
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
}