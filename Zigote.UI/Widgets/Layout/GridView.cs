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

    /// <summary>
    ///     Flutter's <c>GridView.builder</c>: a virtualized, self-scrolling grid. Cells are built on
    ///     demand one grid row at a time, so only the rows in the viewport exist — construction,
    ///     measure, layout and paint are all O(viewport). Returns a <see cref="ListView" /> of rows
    ///     (that is what virtualization needs); the same caveat applies as for
    ///     <see cref="ListView.Builder" /> — a cell scrolled out is destroyed, so keep cell state in
    ///     your model.
    /// </summary>
    public static ListView Builder(
        int crossAxisCount,
        int itemCount,
        Func<int, Widget> itemBuilder,
        double mainAxisSpacing = 0,
        double crossAxisSpacing = 0,
        double childAspectRatio = 1)
    {
        var list = new ListView();
        Rebind(
            list,
            crossAxisCount,
            itemCount,
            itemBuilder,
            mainAxisSpacing,
            crossAxisSpacing,
            childAspectRatio,
            false
        );
        return list;
    }

    /// <summary>
    ///     Re-point a grid from <see cref="Builder" /> at a new item count — the append step of a
    ///     paged or infinite grid, which is otherwise impossible: rebuilding the grid widget makes a
    ///     new <see cref="ListView" /> and drops the reader back to the top.
    ///     <para>
    ///         <paramref name="keepScroll" /> is the point (leave it on); pass false to re-bind and
    ///         jump to the top, as a filter or a sort change would.
    ///     </para>
    /// </summary>
    public static void Rebind(
        ListView list,
        int crossAxisCount,
        int itemCount,
        Func<int, Widget> itemBuilder,
        double mainAxisSpacing = 0,
        double crossAxisSpacing = 0,
        double childAspectRatio = 1,
        bool keepScroll = true)
    {
        var cols = Math.Max(1, crossAxisCount);
        var count = Math.Max(0, itemCount);
        var rows = (count + cols - 1) / cols;
        var crossGap = (float)crossAxisSpacing;
        var mainGap = (float)mainAxisSpacing;
        var ratio = childAspectRatio > 0 ? (float)childAspectRatio : 1f;

        // Cell height follows the width the list actually got, so a resize re-derives it (the list
        // rebuilds its offset table when its width changes). Cells themselves are Expanded, so they
        // stay correct without rebuilding.
        list.HeightOf = r =>
            MathF.Max(0f, (list.ViewportWidth - (cols - 1) * crossGap) / cols) / ratio
            + (r < rows - 1 ? mainGap : 0f);
        list.SetBuilder(
            rows,
            r =>
            {
                var cells = new List<Widget>(cols);
                for (var c = 0; c < cols; c++)
                {
                    var i = r * cols + c;
                    cells.Add(new Expanded(i < count ? itemBuilder(i) : new SizedBox()));
                }

                var row = new Row(
                    cells,
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    spacing: crossGap
                );
                // The trailing gap lives in the row height (see HeightOf), so pad it off the cells.
                return mainGap > 0f && r < rows - 1
                    ? new Padding(EdgeInsets.Only(bottom: mainGap), row)
                    : row;
            },
            keepScroll
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