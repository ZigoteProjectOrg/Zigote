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
    private Widget? _scroller;

    public ScrollsWith(Widget target, Widget? child = null)
    {
        Target = target;
        Child = child;
    }

    /// <summary>
    ///     What scrolls for the child: a <see cref="ScrollView" /> or <see cref="ListView" />, or
    ///     the page holding one. Chrome that floats over a *page switcher* rides whatever page is
    ///     showing, and which widget scrolls there changes with the page — and on some pages with
    ///     what the page is showing — so a page is resolved to the first scroller inside it, per
    ///     hit, rather than named once and left to go stale.
    /// </summary>
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
        // Glue, like LiquidGroup: a miss inside these bounds falls through to whatever is stacked
        // beneath. Chrome rarely fills the rect it is positioned in — a capsule clamped narrower
        // than the pane leaves most of the band empty — and answering `this` there would eat every
        // click on the very content it floats above.
        if (Child?.HitTest(point) is not { } hit) return null;

        if (Scroller() is not { } target) return hit;
        // Mirrors ScrollView.HitTest: the target takes the scroll for this hit, and chains to the
        // scroller that was going to get it for anything past its own edge. Not when that is the
        // target itself — the child is inside it then, and the chain would loop.
        if (!ReferenceEquals(objA: CurrentScrollParent, objB: target))
            target.ScrollParent = CurrentScrollParent;
        CurrentScrollParent = target;
        return hit;
    }

    /// <summary>
    ///     <see cref="Target" /> when it scrolls itself, else the first scroller inside it. Cached
    ///     until that one leaves the tree — a page switch detaches it, and <c>Owner</c> going null
    ///     is the cheapest true answer to "is this still the page's scroller".
    /// </summary>
    private Widget? Scroller()
    {
        if (_scroller is { Owner: not null }) return _scroller;
        return _scroller = Find(Target);

        static Widget? Find(Widget widget)
        {
            if (widget is ScrollView or ListView) return widget;
            foreach (var child in widget.GetChildren())
                if (Find(child) is { } found)
                    return found;
            return null;
        }
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
