using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Focus;

namespace Zigote.Tests;

/// <summary>
///     Focus traversal must only reach controls that are actually shown: a hidden TabView page
///     keeps its last laid-out (non-zero) bounds, so without the GetVisibleChildren seam, Tab
///     would cycle into invisible widgets after a keyboard tab switch.
/// </summary>
public class FocusVisibilityTests
{
    private static TabView LaidOutTabView(out FakeFocusable inTab0, out FakeFocusable inTab1)
    {
        inTab0 = new FakeFocusable();
        inTab1 = new FakeFocusable();
        var tabs = new TabView();
        tabs.Children.Add(inTab0);
        tabs.Children.Add(inTab1);

        // Lay out with tab 0 selected, then switch to tab 1 and lay out again — tab 0's
        // focusable keeps its stale non-zero bounds, exactly the post-switch state.
        tabs.Measure(Constraints.Tight(width: 200f, height: 100f));
        tabs.Layout(Offset.Zero);
        return tabs;
    }

    [Fact]
    public void HiddenTabPage_IsNotFocusReachable()
    {
        var tabs = LaidOutTabView(inTab0: out var inTab0, inTab1: out var inTab1);

        var focusables = FocusTraversal.Focusables(tabs);
        Assert.Contains(expected: inTab0, collection: focusables);
        Assert.DoesNotContain(expected: inTab1, collection: focusables);

        tabs.SelectedIndex = 1;
        tabs.Measure(Constraints.Tight(width: 200f, height: 100f));
        tabs.Layout(Offset.Zero);

        // Tab 0's focusable still has non-zero bounds from its last layout — visibility must come
        // from the container, not the widget's own rect.
        Assert.True(inTab0.Bounds.Width > 0f);

        focusables = FocusTraversal.Focusables(tabs);
        Assert.DoesNotContain(expected: inTab0, collection: focusables);
        Assert.Contains(expected: inTab1, collection: focusables);
    }

    [Fact]
    public void TabTraversal_AfterSwitch_MovesWithinActivePage()
    {
        var tabs = LaidOutTabView(inTab0: out _, inTab1: out var inTab1);
        tabs.SelectedIndex = 1;
        tabs.Measure(Constraints.Tight(width: 200f, height: 100f));
        tabs.Layout(Offset.Zero);

        var order = FocusTraversal.Focusables(tabs);
        // With no current focus, Tab lands on the active page's control — never a hidden one.
        Assert.Same(
            expected: inTab1,
            actual: FocusTraversal.NextInTab(order: order, current: null, backwards: false)
        );
        // And traversal from it wraps within the visible set instead of resetting into tab 0.
        Assert.Same(
            expected: inTab1,
            actual: FocusTraversal.NextInTab(order: order, current: inTab1, backwards: false)
        );
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
