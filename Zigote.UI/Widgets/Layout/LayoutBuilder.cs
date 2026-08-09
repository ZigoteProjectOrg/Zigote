using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Builds its subtree from the incoming
///     <see cref="BoxConstraints" />. The <c>builder</c> runs during Measure (and re-runs when the
///     constraints change), so it can size children against the space actually available.
/// </summary>
public sealed class LayoutBuilder : Widget
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
            var bc = new BoxConstraints(
                c.MinWidth,
                c.MaxWidth,
                c.MinHeight,
                c.MaxHeight
            );
            var next = _builder(BuildContext.Current, bc);
            // A builder that hands back the SAME widget is asking to be re-laid-out, not rebuilt.
            // Detaching and re-attaching it would restart its animations and re-defer the build of
            // any Watch inside it (Watch postpones swaps while the tree walk is running) — once per
            // frame for the whole of a window-resize drag, which is what made resizing flicker.
            if (!ReferenceEquals(next, _child))
            {
                // Attach-then-detach (Widget.SwapChild): a builder can return a DIFFERENT wrapper
                // around the SAME retained subtree, and detaching first would tear that shared
                // subtree down for the same reasons the identity check above avoids.
                var previous = _child;
                _child = next;
                SwapChild(previous, _child);
            }

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