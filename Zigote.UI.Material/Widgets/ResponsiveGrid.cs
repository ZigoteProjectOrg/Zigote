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
        float availW = float.IsFinite(c.MaxWidth) ? c.MaxWidth : MinColumnWidth;
        int cols = Math.Clamp(
            value: (int)MathF.Floor((availW + Gutter) / (MinColumnWidth + Gutter)),
            min: 1,
            max: MaxColumns
        );
        float colW = (availW - (Gutter * (cols - 1))) / cols;
        if (colW <= 0f || !float.IsFinite(colW)) colW = availW;

        if (_positions.Length < Children.Count)
            _positions = new Offset[Children.Count];

        Span<float> colHeights = stackalloc float[cols];
        colHeights.Clear();

        // `i < _positions.Length` tolerates Children growing under the loop: a tile's Measure can
        // run app code (a load-more signal, a deferred Watch apply) that adds tiles. The extras are
        // skipped this pass and picked up next frame — the mutation always marks layout.
        var childC = new Constraints(minWidth: colW, maxWidth: colW);
        for (int i = 0; i < Children.Count && i < _positions.Length; i++)
        {
            var sz = Children[i].Measure(childC);
            float h = float.IsFinite(sz.Height) ? sz.Height : colW;

            int shortest = 0;
            for (int k = 1; k < cols; k++)
            {
                if (colHeights[k] < colHeights[shortest])
                    shortest = k;
            }

            _positions[i] = new Offset(x: shortest * (colW + Gutter), y: colHeights[shortest]);
            colHeights[shortest] += h + Gutter;
        }

        float maxH = 0f;
        for (int k = 0; k < cols; k++) maxH = MathF.Max(x: maxH, y: colHeights[k]);
        if (maxH > 0f) maxH -= Gutter;

        _size = c.Constrain(new Size(width: availW, height: maxH));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        for (int i = 0; i < Children.Count && i < _positions.Length; i++)
        {
            Children[i].Layout(
                new Offset(x: origin.X + _positions[i].X, y: origin.Y + _positions[i].Y)
            );
        }
    }

    public override void Paint(PaintList paint)
    {
        // Cull off-screen tiles so painting is O(visible), not O(total) — essential for smooth scrolling of
        // a large (infinitely-growing) grid. IsVisible tests each tile's rect against the active clip.
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (paint.IsVisible(child.Bounds))
                child.Paint(paint);
        }
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var hit = Children[i].HitTest(point);
            if (hit != null) return hit;
        }

        return null;
    }
}
