namespace Zigote.UI.Material;

/// <summary>
///     A flat, macOS-style window toolbar. A horizontal bar of
///     <see cref="ControlMetrics.ToolbarHeight" />
///     painted on the opaque <see cref="ThemeData.Surface" /> with a 1px
///     <see cref="ThemeData.Separator" />
///     hairline along its bottom edge. <see cref="Leading" /> children are packed against the left, a
///     flexible spacer follows, then <see cref="Trailing" /> children pack against the right — all
///     vertically centred, with horizontal padding of <see cref="Spacing.Md" /> and a gap of
///     <see cref="Spacing.Sm" /> between siblings.
/// </summary>
public sealed class Toolbar : Widget
{
    private const float HPad = Spacing.Md;
    private const float Gap = Spacing.Sm;

    private Size[] _leadSizes = [];
    private float _overflow;

    /// <summary>How far the strip is scrolled when its children are wider than the bar.</summary>
    private float _scrollX;

    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private Size[] _trailSizes = [];

    public Toolbar(IEnumerable<Widget>? leading = null, IEnumerable<Widget>? trailing = null)
    {
        if (leading is not null) Leading.AddRange(leading);
        if (trailing is not null) Trailing.AddRange(trailing);
    }

    /// <summary>Children packed against the leading (left) edge.</summary>
    public List<Widget> Leading { get; } = [];

    /// <summary>Children packed against the trailing (right) edge.</summary>
    public List<Widget> Trailing { get; } = [];

    /// <summary>
    ///     Opt-in translucency. When set <em>and</em> the ambient theme has
    ///     <see cref="ThemeData.UseLiquidGlass" /> enabled, the bar fill is left to the glass chrome
    ///     behind it rather than painting an opaque <see cref="ThemeData.Surface" />. Default flat.
    /// </summary>
    public bool Translucent { get; set; }

    public float Height { get; set; } = ControlMetrics.ToolbarHeight;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);

        float h = Height;
        float childMaxW = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 0f;
        var child = Constraints.Loose(width: childMaxW, height: h);

        if (_leadSizes.Length != Leading.Count) _leadSizes = new Size[Leading.Count];
        if (_trailSizes.Length != Trailing.Count) _trailSizes = new Size[Trailing.Count];

        for (int i = 0; i < Leading.Count; i++) _leadSizes[i] = Leading[i].Measure(child);
        for (int i = 0; i < Trailing.Count; i++) _trailSizes[i] = Trailing[i].Measure(child);

        float width = float.IsFinite(c.MaxWidth) ? c.MaxWidth : MeasureIntrinsicWidth();
        _size = c.Constrain(new Size(width: width, height: h));

        // Leading packs left and trailing packs right with nothing arbitrating the middle, so on a
        // narrow bar the two groups used to overlap and paint past the edges. Treat the bar as one
        // horizontally scrollable strip instead: everything stays reachable, nothing bleeds out.
        _overflow = MathF.Max(x: 0f, y: MeasureIntrinsicWidth() - _size.Width);
        _scrollX = Math.Clamp(value: _scrollX, min: 0f, max: _overflow);
        return _size;
    }

    private float MeasureIntrinsicWidth()
    {
        float w = HPad * 2f;
        for (int i = 0; i < _leadSizes.Length; i++)
        {
            if (i > 0) w += Gap;
            w += _leadSizes[i].Width;
        }

        for (int i = 0; i < _trailSizes.Length; i++)
        {
            if (i > 0 || _leadSizes.Length > 0) w += Gap;
            w += _trailSizes[i].Width;
        }

        return w;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );

        float x = origin.X + HPad - _scrollX;
        for (int i = 0; i < Leading.Count; i++)
        {
            var sz = i < _leadSizes.Length ? _leadSizes[i] : Size.Zero;
            float cy = origin.Y + ((_size.Height - sz.Height) / 2f);
            Leading[i].Layout(new Offset(x: x, y: cy));
            x += sz.Width + Gap;
        }

        float rx = origin.X + _size.Width - HPad + _overflow - _scrollX;
        for (int i = Trailing.Count - 1; i >= 0; i--)
        {
            var sz = i < _trailSizes.Length ? _trailSizes[i] : Size.Zero;
            rx -= sz.Width;
            float cy = origin.Y + ((_size.Height - sz.Height) / 2f);
            Trailing[i].Layout(new Offset(x: rx, y: cy));
            rx -= Gap;
        }
    }

    public override void Paint(PaintList paint)
    {
        bool glass = Translucent;
        if (!glass)
            paint.AddRect(bounds: Bounds, color: _theme.Surface);

        // 1px hairline along the bottom edge.
        paint.AddRect(
            bounds: new Rect(
                x: Bounds.X,
                y: Bounds.Y + Bounds.Height - 1f,
                width: Bounds.Width,
                height: 1f
            ),
            color: _theme.Separator
        );

        paint.AddClipStart(Bounds);
        foreach (var w in Leading) w.Paint(paint);
        foreach (var w in Trailing) w.Paint(paint);
        paint.AddClipEnd();
    }

    public override bool CanTouchScroll(bool vertical) => !vertical && _overflow > 0f;

    public override void OnTouchScroll(float dx, float dy)
    {
        if (_overflow <= 0f)
        {
            base.OnTouchScroll(dx: dx, dy: dy);
            return;
        }

        _scrollX = Math.Clamp(value: _scrollX - dx, min: 0f, max: _overflow);
        MarkNeedsLayout();
    }

    public override void OnScroll(float dx, float dy)
    {
        if (_overflow <= 0f || MathF.Abs(dx) <= MathF.Abs(dy))
        {
            base.OnScroll(dx: dx, dy: dy);
            return;
        }

        _scrollX = Math.Clamp(value: _scrollX - (dx * Gap * 2f), min: 0f, max: _overflow);
        MarkNeedsLayout();
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;

        for (int i = Trailing.Count - 1; i >= 0; i--)
        {
            var hit = Trailing[i].HitTest(point);
            if (hit != null) return hit;
        }

        for (int i = Leading.Count - 1; i >= 0; i--)
        {
            var hit = Leading[i].HitTest(point);
            if (hit != null) return hit;
        }

        return this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        foreach (var w in Leading) yield return w;
        foreach (var w in Trailing) yield return w;
    }
}
