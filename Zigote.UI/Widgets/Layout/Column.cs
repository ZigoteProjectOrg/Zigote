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
        float spacing = 0f,
        Key? key = null) : base(children)
    {
        MainAxisAlignment = mainAxisAlignment;
        CrossAxisAlignment = crossAxisAlignment;
        MainAxisSize = mainAxisSize;
        Spacing = spacing;
        if (key is not null) Key = key;
    }

    public MainAxisAlignment MainAxisAlignment { get; set; } = MainAxisAlignment.Start;
    public CrossAxisAlignment CrossAxisAlignment { get; set; } = CrossAxisAlignment.Center;
    public MainAxisSize MainAxisSize { get; set; } = MainAxisSize.Max;

    /// <summary>
    ///     Fixed main-axis gap between adjacent children. Composes with
    ///     <see cref="MainAxisAlignment" />: the gaps are reserved first, alignment distributes the
    ///     remaining free space.
    /// </summary>
    public float Spacing { get; set; }

    [Obsolete("Renamed — use MainAxisAlignment.")]
    public MainAxisAlignment MainAxisAlign
    {
        get => MainAxisAlignment;
        set => MainAxisAlignment = value;
    }

    [Obsolete("Renamed — use CrossAxisAlignment.")]
    public CrossAxisAlignment CrossAxisAlign
    {
        get => CrossAxisAlignment;
        set => CrossAxisAlignment = value;
    }

    public override Size Measure(Constraints c)
    {
        _size = FlexLayout.Measure(
            children: Children,
            c: c,
            axis: 1,
            mainAlign: MainAxisAlignment,
            crossAlign: CrossAxisAlignment,
            mainSize: MainAxisSize,
            spacing: Spacing,
            metrics: ref _metrics
        );
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
        FlexLayout.Layout(
            children: Children,
            metrics: _metrics,
            bounds: Bounds,
            axis: 1,
            mainAlign: MainAxisAlignment,
            spacing: Spacing
        );
    }

    public override void Paint(PaintList paint) =>
        FlexLayout.Paint(children: Children, paint: paint);

    public override Widget? HitTest(Offset point) => FlexLayout.HitTest(
        children: Children,
        bounds: Bounds,
        point: point
    );
}
