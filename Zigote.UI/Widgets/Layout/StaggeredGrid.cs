using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     A grid of fixed-width columns where each child keeps its own height and lands in whichever
///     column is currently shortest — the Pinterest shape. <see cref="GridView" /> is the uniform
///     -cell grid; this one is for photo walls and note boards, where cropping every tile to the
///     same height is the wrong answer.
///     <para>
///         Children are measured against a tight column width, so a child that sizes to its
///         constraint fills the column and one that measures itself (an image with an aspect
///         ratio) keeps its own height.
///     </para>
///     <para>
///         ponytail: shortest-column placement, not a global optimum. Filling the last row evenly
///         needs bin-packing over all children; this places in order, which is also what keeps
///         source order readable down the columns.
///     </para>
/// </summary>
public class StaggeredGrid : MultiChildWidget
{
    private float[] _columnHeights = [];
    private Offset[] _offsets = [];
    private Size _size;

    /// <param name="children">Tiles, placed in order.</param>
    /// <param name="columns">How many columns. Clamped to at least one.</param>
    /// <param name="spacing">Gap between columns.</param>
    /// <param name="runSpacing">Gap between tiles down a column.</param>
    public StaggeredGrid(
        IEnumerable<Widget>? children = null,
        int columns = 2,
        double? spacing = null,
        double? runSpacing = null,
        Key? key = null) : base(children)
    {
        Columns = columns;
        if (spacing is { } s) Spacing = (float)s;
        if (runSpacing is { } r) RunSpacing = (float)r;
        if (key is not null) Key = key;
    }

    /// <summary>Column count; at least one.</summary>
    public int Columns
    {
        get;
        set => field = Math.Max(1, value);
    } = 2;

    /// <summary>Gap between columns.</summary>
    public float Spacing { get; set; } = UI.Theme.Spacing.Sm;

    /// <summary>Gap between tiles within a column.</summary>
    public float RunSpacing { get; set; } = UI.Theme.Spacing.Sm;

    /// <summary>Width one column gets under these constraints — useful for sizing images ahead of layout.</summary>
    public float ColumnWidth(float availableWidth)
        => MathF.Max(0f, (availableWidth - (Spacing * (Columns - 1))) / Columns);

    public override Size Measure(Constraints c)
    {
        // Grow-only scratch buffers, the Wrap/Column pattern: no reallocation while the child
        // count is stable.
        if (_offsets.Length < Children.Count) _offsets = new Offset[Children.Count];
        if (_columnHeights.Length < Columns) _columnHeights = new float[Columns];
        Array.Clear(_columnHeights, 0, Columns);

        // Unbounded width has no columns to speak of; fall back to something laid out rather than
        // infinitely wide, the way a scroll view's cross axis behaves.
        float available = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 600f;
        float columnWidth = ColumnWidth(available);
        var childConstraints = new Constraints(
            minWidth: columnWidth,
            maxWidth: columnWidth,
            minHeight: 0,
            maxHeight: float.PositiveInfinity);

        for (int i = 0; i < Children.Count; i++)
        {
            var size = Children[i].Measure(childConstraints);
            int column = ShortestColumn();
            float y = _columnHeights[column];
            _offsets[i] = new Offset(x: column * (columnWidth + Spacing), y: y > 0 ? y + RunSpacing : 0f);
            _columnHeights[column] = _offsets[i].Y + size.Height;
        }

        float tallest = 0f;
        for (int i = 0; i < Columns; i++) tallest = MathF.Max(tallest, _columnHeights[i]);
        _size = c.Constrain(new Size(width: available, height: tallest));
        return _size;
    }

    /// <summary>Where the next tile goes: the column with the least in it, ties to the left.</summary>
    private int ShortestColumn()
    {
        int shortest = 0;
        for (int i = 1; i < Columns; i++)
            if (_columnHeights[i] < _columnHeights[shortest])
                shortest = i;
        return shortest;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(x: origin.X, y: origin.Y, width: _size.Width, height: _size.Height);

        // The same guard Wrap carries: the child list can grow between Measure and Layout, so lay
        // out the measured prefix and ask for another pass rather than indexing past the table.
        int count = Math.Min(Children.Count, _offsets.Length);
        for (int i = 0; i < count; i++)
            Children[i].Layout(new Offset(x: origin.X + _offsets[i].X, y: origin.Y + _offsets[i].Y));

        if (count < Children.Count) MarkNeedsLayout();
    }

    public override void Paint(PaintList paint)
    {
        foreach (var child in Children) child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        for (int i = Children.Count - 1; i >= 0; i--)
            if (Children[i].HitTest(point) is { } hit)
                return hit;
        return null;
    }
}
