using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

public class Column : MultiChildWidget
{
    private FlexLayout.ChildMetrics[] _metrics = [];

    private Size _size;

    /// <summary>
    ///     Named-argument constructor:
    ///     <c>
    ///         new Column(mainAxisAlignment: MainAxisAlignment.Center,
    ///         children: [...])
    ///     </c>
    ///     . Every argument is optional, so the object-initializer form
    ///     (<c>new Column { Children = { … } }</c>) and the positional <c>new Column(list)</c> keep
    ///     working. Defaults: main = Start, cross = Center, size = Max.
    /// </summary>
    public Column(
        IEnumerable<Widget>? children = null,
        MainAxisAlignment mainAxisAlignment = MainAxisAlignment.Start,
        CrossAxisAlignment crossAxisAlignment = CrossAxisAlignment.Center,
        MainAxisSize mainAxisSize = MainAxisSize.Max,
        Key? key = null) : base(children)
    {
        MainAxisAlign = mainAxisAlignment;
        CrossAxisAlign = crossAxisAlignment;
        MainAxisSize = mainAxisSize;
        if (key is not null) Key = key;
    }

    public MainAxisAlignment MainAxisAlign { get; set; } = MainAxisAlignment.Start;
    public CrossAxisAlignment CrossAxisAlign { get; set; } = CrossAxisAlignment.Center;
    public MainAxisSize MainAxisSize { get; set; } = MainAxisSize.Max;

    public override Size Measure(Constraints c)
    {
        _size = FlexLayout.Measure(
            Children,
            c,
            1,
            MainAxisAlign,
            CrossAxisAlign,
            MainAxisSize,
            ref _metrics
        );
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
        FlexLayout.Layout(
            Children,
            _metrics,
            Bounds,
            1,
            MainAxisAlign
        );
    }

    public override void Paint(PaintList paint)
    {
        FlexLayout.Paint(Children, paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return FlexLayout.HitTest(Children, Bounds, point);
    }
}