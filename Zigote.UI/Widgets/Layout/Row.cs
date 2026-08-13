using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

public class Row : MultiChildWidget
{
    private FlexLayout.ChildMetrics[] _metrics = [];

    // Resolved during Measure (BuildContext is valid there) and reused by Layout.
    private bool _rtl;

    private Size _size;

    /// <summary>
    ///     Named-argument constructor:
    ///     <c>
    ///         new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween,
    ///         children: [...])
    ///     </c>
    ///     . All arguments optional — the object-initializer and positional forms
    ///     keep working. Defaults: main = Start, cross = Center, size = Max.
    /// </summary>
    public Row(
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

    /// <summary>
    ///     Horizontal flow of the children. <c>null</c> (the default) follows the ambient
    ///     <see cref="Directionality" />; under <see cref="TextDirection.Rtl" /> the row mirrors —
    ///     the first child sits at the right edge and alignment slack flips with it.
    /// </summary>
    public TextDirection? LayoutDirection { get; set; }

    public override Size Measure(Constraints c)
    {
        _rtl = (LayoutDirection ?? Directionality.Of(BuildContext.Current)) == TextDirection.Rtl;
        _size = FlexLayout.Measure(
            children: Children,
            c: c,
            axis: 0,
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
            axis: 0,
            mainAlign: MainAxisAlignment,
            spacing: Spacing,
            rtl: _rtl
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
