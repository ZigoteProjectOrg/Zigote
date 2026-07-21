using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Focus;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     A virtualized ListView only lays out the rows inside its viewport window, so rows scrolled
///     out of the window keep their last (non-zero) bounds. Without the GetVisibleChildren seam,
///     focus traversal and the semantics walk would reach those stale off-window rows.
/// </summary>
public class ListViewVisibilityTests
{
    private static ListView LaidOutList(out List<Widget> rows)
    {
        rows = [];
        for (var i = 0; i < 100; i++) rows.Add(new FakeFocusable());
        var list = new ListView(itemExtent: 20) { Smooth = false };
        list.SetItems(rows);
        list.Measure(Constraints.Tight(200f, 100f));
        list.Layout(Offset.Zero);
        return list;
    }

    [Fact]
    public void OffWindowRows_AreNotFocusReachable()
    {
        var list = LaidOutList(out var rows);

        var focusables = FocusTraversal.Focusables(list);
        Assert.Contains(rows[0], focusables);
        Assert.DoesNotContain(rows[50], focusables);
        Assert.DoesNotContain(rows[99], focusables);
    }

    [Fact]
    public void ScrolledOutRows_KeepStaleBounds_ButLeaveTheVisibleSet()
    {
        var list = LaidOutList(out var rows);

        // Scroll 1000 px down (25 wheel ticks × ScrollSpeed 40, instant) and lay out again.
        list.OnScroll(0f, -25f);
        list.Measure(Constraints.Tight(200f, 100f));
        list.Layout(Offset.Zero);

        // Row 0 still has its non-zero bounds from the first layout — visibility must come from
        // the container's window, not the widget's own rect.
        Assert.True(rows[0].Bounds.Height > 0f);

        var focusables = FocusTraversal.Focusables(list);
        Assert.DoesNotContain(rows[0], focusables);
        Assert.Contains(rows[50], focusables);
    }

    private sealed class FakeFocusable : RenderWidget
    {
        public override bool Focusable => true;

        public override Size Measure(Constraints c)
        {
            return new Size(50f, 20f);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                50f,
                20f
            );
        }

        public override void Paint(PaintList paint)
        {
        }
    }
}
