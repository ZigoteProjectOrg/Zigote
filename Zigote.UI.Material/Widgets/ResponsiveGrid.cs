namespace Zigote.UI.Material;

/// <summary>
///     A responsive masonry grid: it derives a column count from the available width (targeting
///     <see cref="MinColumnWidth" />), measures each child at the resolved column width, and packs
///     children
///     into the currently-shortest column — so tiles of <b>different heights</b> tessellate without
///     gaps
///     (Pinterest-style). Children keep whatever height they report at that width, so wrap image tiles
///     in an
///     <see cref="AspectRatio" /> to give the grid varied, content-driven heights. Reconciles by key
///     via
///     <see cref="MultiChildWidget.SetChildren" />.
/// </summary>
public sealed class ResponsiveGrid(IEnumerable<Widget>? children = null)
    : MultiChildWidget(children)
{
    private Offset[] _positions = [];
    private Size _size;

    /// <summary>The grid targets columns at least this wide; more width → more columns.</summary>
    public float MinColumnWidth { get; set; } = 220f;

    /// <summary>Space between columns and between stacked tiles.</summary>
    public float Gutter { get; set; } = 12f;

    public int MaxColumns { get; set; } = 8;

    public override Size Measure(Constraints c)
    {
        var availW = float.IsFinite(c.MaxWidth) ? c.MaxWidth : MinColumnWidth;
        var cols = Math.Clamp(
            (int)MathF.Floor((availW + Gutter) / (MinColumnWidth + Gutter)),
            1,
            MaxColumns
        );
        var colW = (availW - Gutter * (cols - 1)) / cols;
        if (colW <= 0f || !float.IsFinite(colW)) colW = availW;

        if (_positions.Length < Children.Count)
            _positions = new Offset[Children.Count];

        Span<float> colHeights = stackalloc float[cols];
        colHeights.Clear();

        var childC = new Constraints(colW, colW);
        for (var i = 0; i < Children.Count; i++)
        {
            var sz = Children[i].Measure(childC);
            var h = float.IsFinite(sz.Height) ? sz.Height : colW;

            var shortest = 0;
            for (var k = 1; k < cols; k++)
                if (colHeights[k] < colHeights[shortest])
                    shortest = k;

            _positions[i] = new Offset(shortest * (colW + Gutter), colHeights[shortest]);
            colHeights[shortest] += h + Gutter;
        }

        var maxH = 0f;
        for (var k = 0; k < cols; k++) maxH = MathF.Max(maxH, colHeights[k]);
        if (maxH > 0f) maxH -= Gutter;

        _size = c.Constrain(new Size(availW, maxH));
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
        for (var i = 0; i < Children.Count; i++)
            Children[i].Layout(new Offset(origin.X + _positions[i].X, origin.Y + _positions[i].Y));
    }

    public override void Paint(PaintList paint)
    {
        // Cull off-screen tiles so painting is O(visible), not O(total) — essential for smooth scrolling of
        // a large (infinitely-growing) grid. IsVisible tests each tile's rect against the active clip.
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (paint.IsVisible(child.Bounds))
                child.Paint(paint);
        }
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var hit = Children[i].HitTest(point);
            if (hit != null) return hit;
        }

        return null;
    }
}