using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Builds its subtree from the incoming
///     <see cref="BoxConstraints" />. The <c>builder</c> runs during Measure (and re-runs when the
///     constraints change), so it can size children against the space actually available.
/// </summary>
public sealed class LayoutBuilder : RenderWidget
{
    private readonly Func<BuildContext, BoxConstraints, Widget> _builder;
    private Widget? _child;
    private Constraints? _lastConstraints;
    private Size _size;

    public LayoutBuilder(Func<BuildContext, BoxConstraints, Widget> builder)
    {
        _builder = builder;
    }

    public override Size Measure(Constraints c)
    {
        if (_child is null || _lastConstraints is not { } last || c != last)
        {
            _child?.Detach();
            var bc = new BoxConstraints(
                c.MinWidth,
                c.MaxWidth,
                c.MinHeight,
                c.MaxHeight
            );
            _child = _builder(BuildContext.Current, bc);
            if (Owner is not null) _child.Attach(Owner, this);
            _lastConstraints = c;
        }

        _size = _child.Measure(c);
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
        _child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_child);
    }
}