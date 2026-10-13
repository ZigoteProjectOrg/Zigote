using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>
///     Overlays a count pill on the top-right corner of its child — the same indicator
///     <see cref="AdwViewSwitcher" /> shows for a page that needs attention: a pill in the accent
///     colour (or <see cref="Color" />) with bold 10px numerals, inset into the corner rather than
///     hanging off it. Hides when <see cref="Count" /> is 0; a single digit is a circle.
/// </summary>
public sealed class AdwBadge(Widget? child = null, int count = 0) : Widget
{
    // Paint runs per repaint frame; only allocate the count string when the count changed.
    private readonly CachedText _countText = new(8);
    private Size _childSize;
    private ThemeData _theme = ThemeData.Dark;

    public Widget? Child { get; set; } = child;
    public int Count { get; set; } = count;
    public Color? Color { get; set; }

    public override int DebugStateHash() => Count;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _childSize = Child?.Measure(c) ?? new Size(width: 0, height: 0);
        return _childSize;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _childSize.Width,
            height: _childSize.Height
        );
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        Child?.Paint(paint);
        if (Count <= 0) return;

        string text = Count > 99 ? "99+" : _countText.Update($"{Count}");
        const float fs = 10f; // `indicatorbin > .badge { font-size: 0.7em }`
        var textSize = TextMeasure.Measure(text: text, fontSize: fs, weight: FontWeight.Bold);

        // Capsule pill: fixed compact height; min width equals height so single digits are a circle.
        float bh = MathF.Max(x: 16f, y: textSize.Height + Spacing.Xxs);
        float bw = MathF.Max(x: bh, y: textSize.Width + 12f);
        // Inset into the child's top-right corner, as AdwIndicatorBin places it: a quarter of the
        // pill overhangs, so it reads as belonging to the child instead of floating beside it.
        float bx = Bounds.Right - (bw * 0.75f);
        float by = Bounds.Y - (bh * 0.25f);

        // Explicit half-height radius → guaranteed capsule regardless of renderer radius clamping.
        paint.AddRect(
            bounds: new Rect(
                x: bx,
                y: by,
                width: bw,
                height: bh
            ),
            color: Color ?? _theme.Primary,
            radius: bh * 0.5f
        );

        float tx = bx + ((bw - textSize.Width) / 2f);
        float ty = by + ((bh - textSize.Height) / 2f) + (fs * 0.8f);
        paint.AddText(
            text: text,
            baselineX: tx,
            baselineY: ty,
            color: _theme.OnPrimary,
            fontSize: fs,
            fontWeight: FontWeight.Bold
        );
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
