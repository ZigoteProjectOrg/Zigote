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
        for (int i = 0; i < 100; i++) rows.Add(new FakeFocusable());
        var list = new ListView(itemExtent: 20) { Smooth = false };
        list.SetItems(rows);
        list.Measure(Constraints.Tight(width: 200f, height: 100f));
        list.Layout(Offset.Zero);
        return list;
    }

    [Fact]
    public void OffWindowRows_AreNotFocusReachable()
    {
        var list = LaidOutList(out var rows);

        var focusables = FocusTraversal.Focusables(list);
        Assert.Contains(expected: rows[0], collection: focusables);
        Assert.DoesNotContain(expected: rows[50], collection: focusables);
        Assert.DoesNotContain(expected: rows[99], collection: focusables);
    }

    [Fact]
    public void ScrolledOutRows_KeepStaleBounds_ButLeaveTheVisibleSet()
    {
        var list = LaidOutList(out var rows);

        // Scroll 1000 px down (25 wheel ticks × ScrollSpeed 40, instant) and lay out again.
        list.OnScroll(dx: 0f, dy: -25f);
        list.Measure(Constraints.Tight(width: 200f, height: 100f));
        list.Layout(Offset.Zero);

        // Row 0 still has its non-zero bounds from the first layout — visibility must come from
        // the container's window, not the widget's own rect.
        Assert.True(rows[0].Bounds.Height > 0f);

        var focusables = FocusTraversal.Focusables(list);
        Assert.DoesNotContain(expected: rows[0], collection: focusables);
        Assert.Contains(expected: rows[50], collection: focusables);
    }

    private sealed class FakeFocusable : Widget
    {
        public override bool Focusable => true;

        public override Size Measure(Constraints c) => new(width: 50f, height: 20f);

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: 50f,
                height: 20f
            );
        }

        public override void Paint(PaintList paint) { }
    }
}
