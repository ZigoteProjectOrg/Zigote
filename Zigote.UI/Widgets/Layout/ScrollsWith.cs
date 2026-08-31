using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Hands the wheel, and a touch drag, that lands on its child to a scroll view the child is
///     not inside — chrome floating over a viewport.
///     <para>
///         A floating toolbar sits <i>beside</i> the scroll view it rides on, as a later child of
///         the same <see cref="Stack" />, never in it. A wheel over the toolbar therefore reaches
///         whatever scroll view encloses them both, and the content under the toolbar stays put —
///         the one place on the screen where scrolling does the wrong thing is the one the chrome
///         draws the eye to. Wrapping the toolbar in this names <see cref="Target" /> as the scroll
///         parent for every hit inside it, exactly as the target's own hit test would have; past
///         its edge the scroll bubbles on to whoever was going to get it, as usual.
///     </para>
/// </summary>
public sealed class ScrollsWith : Widget
{
    private Size _size;

    public ScrollsWith(Widget target, Widget? child = null)
    {
        Target = target;
        Child = child;
    }

    /// <summary>The scroll view that scrolls for the child — usually a <see cref="ScrollView" />.</summary>
    public Widget Target { get; set; }

    public Widget? Child { get; set; }

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? c.Constrain(Size.Zero);
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
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint) => Child?.Paint(paint);

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        var hit = Child?.HitTest(point) ?? this;
        // Mirrors ScrollView.HitTest: the target takes the scroll for this hit, and chains to the
        // scroller that was going to get it for anything past its own edge. Not when that is the
        // target itself — the child is inside it then, and the chain would loop.
        if (!ReferenceEquals(objA: CurrentScrollParent, objB: Target))
            Target.ScrollParent = CurrentScrollParent;
        CurrentScrollParent = Target;
        return hit;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
