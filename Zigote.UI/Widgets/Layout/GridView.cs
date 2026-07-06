using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     A fixed-cell grid with a fixed cross-axis count. Lays children in <see cref="CrossAxisCount" />
///     equal columns; cell height follows <see cref="ChildAspectRatio" />.
///     <para>
///         This grid <b>sizes to its content</b> (it does not scroll on its own). Wrap it
///         in a <see cref="SingleChildScrollView" /> for scrolling, or use it inside a bounded region.
///     </para>
/// </summary>
public sealed class GridView : MultiChildWidget
{
    private float _cellH;
    private float _cellW;
    private int _cols = 1;
    private Size _size;

    private GridView(IEnumerable<Widget>? children) : base(children)
    {
    }

    public int CrossAxisCount { get; set; } = 2;
    public float MainAxisSpacing { get; set; }
    public float CrossAxisSpacing { get; set; }
    public float ChildAspectRatio { get; set; } = 1f;

    /// <summary>Constructs a grid from a fixed cross-axis count and children.</summary>
    public static GridView Count(
        int crossAxisCount,
        IEnumerable<Widget>? children = null,
        double mainAxisSpacing = 0,
        double crossAxisSpacing = 0,
        double childAspectRatio = 1)
    {
        return new GridView(children) {
            CrossAxisCount = Math.Max(1, crossAxisCount),
            MainAxisSpacing = (float)mainAxisSpacing,
            CrossAxisSpacing = (float)crossAxisSpacing,
            ChildAspectRatio = (float)childAspectRatio,
        };
    }

    /// <summary>Builds a grid with a fixed cross-axis count from an item builder (materialized eagerly).</summary>
    public static GridView Builder(
        int crossAxisCount,
        int itemCount,
        Func<int, Widget> itemBuilder,
        double mainAxisSpacing = 0,
        double crossAxisSpacing = 0,
        double childAspectRatio = 1)
    {
        var items = new List<Widget>(Math.Max(0, itemCount));
        for (var i = 0; i < itemCount; i++) items.Add(itemBuilder(i));
        return Count(
            crossAxisCount,
            items,
            mainAxisSpacing,
            crossAxisSpacing,
            childAspectRatio
        );
    }

    public override Size Measure(Constraints c)
    {
        _cols = Math.Max(1, CrossAxisCount);
        var availW = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 0f;

        _cellW = MathF.Max(0f, (availW - (_cols - 1) * CrossAxisSpacing) / _cols);
        _cellH = ChildAspectRatio > 0f ? _cellW / ChildAspectRatio : _cellW;

        var cell = Constraints.Tight(_cellW, _cellH);
        for (var i = 0; i < Children.Count; i++) Children[i].Measure(cell);

        var rows = (Children.Count + _cols - 1) / _cols;
        var h = rows * _cellH + Math.Max(0, rows - 1) * MainAxisSpacing;
        _size = c.Constrain(new Size(availW, h));
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
        {
            var col = i % _cols;
            var row = i / _cols;
            var x = origin.X + col * (_cellW + CrossAxisSpacing);
            var y = origin.Y + row * (_cellH + MainAxisSpacing);
            Children[i].Layout(new Offset(x, y));
        }
    }

    public override void Paint(PaintList paint)
    {
        for (var i = 0; i < Children.Count; i++) Children[i].Paint(paint);
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