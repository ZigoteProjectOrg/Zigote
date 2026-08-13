using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

/// <summary>
///     A subtree that is detached and re-attached — a wrapper swapped around retained content (the
///     Adwaita bottom sheet returning <c>content</c> when closed and <c>Stack { content, sheet }</c>
///     when open), a route re-entered, a tab re-shown. Detach unmounts every widget below it
///     (disposing what they own), so the subtree only comes back live if something re-measures it.
///     Nothing else in the re-attach path invalidates measure
///     caches, so a <see cref="ComposedWidget" /> ancestor at unchanged constraints early-returned
///     its cached size, the rebuild never ran, and the subtree rendered blank.
///     <see cref="Widget.Detach" />
///     flags the widget for layout to close that hole.
/// </summary>
public class DetachReattachMeasureTests
{
    private static readonly Constraints Room = new(
        minWidth: 0f,
        maxWidth: 200f,
        minHeight: 0f,
        maxHeight: 200f
    );

    [Fact]
    public void ReattachedSubtreeIsRebuiltNotLeftBlank()
    {
        var leaf = new Leaf();
        var wrapper = new Wrapper(leaf);

        wrapper.Measure(Room);
        wrapper.Layout(Offset.Zero);
        Assert.Single(leaf.Built);
        Assert.True(leaf.Built[0].Measures > 0);

        // Stand in for the parent link Attach would have set: App owns a native window, so a live
        // Owner is not constructible headlessly, and Detach only cascades into children that still
        // point at it.
        leaf.Parent = wrapper;

        // The transient re-parent: the whole subtree goes away and comes straight back.
        wrapper.Detach();
        int measures = leaf.Built[0].Measures;

        wrapper.Measure(Room); // same constraints, same generation — the stale cache must not win
        wrapper.Layout(Offset.Zero);

        // The retained tree survives the round trip — no rebuild, so no lost focus/scroll/animation.
        Assert.Single(leaf.Built);
        Assert.True(
            condition: leaf.Built[0].Measures > measures,
            userMessage: "the re-attached subtree was never re-measured — it renders blank"
        );
    }

    private sealed class Probe : Widget
    {
        public int Measures;

        public override Size Measure(Constraints constraints)
        {
            Measures++;
            return new Size(width: 10f, height: 10f);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: 10f,
                height: 10f
            );
        }

        public override void Paint(PaintList paint) { }
    }

    private sealed class Leaf : ComposedWidget
    {
        public readonly List<Probe> Built = [];

        protected override Widget Build(BuildContext context)
        {
            var probe = new Probe();
            Built.Add(probe);
            return probe;
        }
    }

    private sealed class Wrapper(Widget child) : ComposedWidget
    {
        protected override Widget Build(BuildContext context) => child;
    }
}
