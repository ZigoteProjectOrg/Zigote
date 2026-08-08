using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

/// <summary>
///     A subtree that is detached and re-attached — a wrapper swapped around retained content (the
///     Adwaita bottom sheet returning <c>content</c> when closed and <c>Stack { content, sheet }</c>
///     when open), a route re-entered, a tab re-shown. Detach disposes every
///     <see cref="StatefulWidget" /> state below it and clears its child cache, so the subtree only
///     comes back if something re-measures it. Nothing in the re-attach path invalidates measure
///     caches, so a <see cref="StatelessWidget" /> ancestor at unchanged constraints early-returned
///     its cached size, the rebuild never ran, and the subtree rendered blank. <see cref="Widget.Detach" />
///     flags the widget for layout to close that hole.
/// </summary>
public class DetachReattachMeasureTests
{
    private static readonly Constraints Room = new(
        0f,
        200f,
        0f,
        200f
    );

    private sealed class Probe : Widget
    {
        public bool Measured;

        public override Size Measure(Constraints constraints)
        {
            Measured = true;
            return new Size(10f, 10f);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                10f,
                10f
            );
        }

        public override void Paint(PaintList paint)
        {
        }
    }

    private sealed class Leaf : StatefulWidget
    {
        public readonly List<Probe> Built = [];

        protected override WidgetState CreateState()
        {
            return new LeafState();
        }

        private sealed class LeafState : WidgetState<Leaf>
        {
            public override Widget Build(BuildContext context)
            {
                var probe = new Probe();
                Widget.Built.Add(probe);
                return probe;
            }
        }
    }

    private sealed class Wrapper(Widget child) : StatelessWidget
    {
        protected override Widget Build(BuildContext context)
        {
            return child;
        }
    }

    [Fact]
    public void ReattachedSubtreeIsRebuiltNotLeftBlank()
    {
        var leaf = new Leaf();
        var wrapper = new Wrapper(leaf);

        wrapper.Measure(Room);
        wrapper.Layout(Offset.Zero);
        Assert.Single(leaf.Built);
        Assert.True(leaf.Built[0].Measured);

        // Stand in for the parent link Attach would have set: App owns a native window, so a live
        // Owner is not constructible headlessly, and Detach only cascades into children that still
        // point at it.
        leaf.Parent = wrapper;

        // The transient re-parent: the whole subtree goes away and comes straight back. Detach
        // disposed the leaf's state, so it MUST rebuild before it is laid out again.
        wrapper.Detach();

        wrapper.Measure(Room); // same constraints, same generation — the stale cache must not win
        wrapper.Layout(Offset.Zero);

        Assert.Equal(2, leaf.Built.Count);
        Assert.True(
            leaf.Built[1].Measured,
            "the re-attached subtree was never re-measured — it renders blank"
        );
    }
}