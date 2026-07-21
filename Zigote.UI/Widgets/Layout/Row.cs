using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

public class Row : MultiChildWidget
{
    private FlexLayout.ChildMetrics[] _metrics = [];

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

    // Resolved during Measure (BuildContext is valid there) and reused by Layout.
    private bool _rtl;

    public override Size Measure(Constraints c)
    {
        _rtl = (LayoutDirection ?? Directionality.Of(BuildContext.Current)) == TextDirection.Rtl;
        _size = FlexLayout.Measure(
            Children,
            c,
            0,
            MainAxisAlignment,
            CrossAxisAlignment,
            MainAxisSize,
            Spacing,
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
            0,
            MainAxisAlignment,
            Spacing,
            _rtl
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