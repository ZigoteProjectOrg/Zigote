using Xunit;
using Zigote.Core;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Focus;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Covers the pure focus-traversal policy (<see cref="FocusTraversal" />): reading-order
///     collection of
///     focusables (skipping collapsed/disabled ones), Tab/Shift-Tab wrapping, and geometric arrow
///     navigation. Headless — lay out a tree, call the policy, assert. The app-level scoping (modal
///     trap,
///     overlay auto-focus, Esc) is integration behaviour exercised at runtime.
/// </summary>
public class FocusTraversalTests
{
    private static Pressable Btn(float w = 40f, float h = 20f, bool enabled = true)
    {
        return new Pressable {
            Child = new SizedBox(width: w, height: h),
            Enabled = enabled,
        };
    }

    private static void LayOut(Widget w, float width = 400f, float height = 200f)
    {
        w.Measure(Constraints.Loose(width: width, height: height));
        w.Layout(Offset.Zero);
    }

    [Fact]
    public void Focusables_AreCollectedInReadingOrder()
    {
        var a = Btn();
        var b = Btn();
        var c = Btn();
        var col = new Column {
            Children = {
                a,
                b,
                c,
            },
        };
        LayOut(col);

        var order = FocusTraversal.Focusables(col);
        Assert.Equal(
            expected: new Widget[] {
                a,
                b,
                c,
            },
            actual: order
        );
    }

    [Fact]
    public void Focusables_SkipCollapsedAndDisabled()
    {
        var visible = Btn();
        var collapsed = Btn(w: 0f, h: 0f);
        var disabled = Btn(enabled: false);
        var col = new Column {
            Children = {
                visible,
                collapsed,
                disabled,
            },
        };
        LayOut(col);

        var order = FocusTraversal.Focusables(col);
        Assert.Single(order);
        Assert.Same(expected: visible, actual: order[0]);
    }

    [Fact]
    public void NextInTab_WrapsForwardAndBackward()
    {
        var a = Btn();
        var b = Btn();
        var c = Btn();
        var col = new Column {
            Children = {
                a,
                b,
                c,
            },
        };
        LayOut(col);
        var order = FocusTraversal.Focusables(col);

        Assert.Same(
            expected: b,
            actual: FocusTraversal.NextInTab(order: order, current: a, backwards: false)
        );
        Assert.Same(
            expected: a,
            actual: FocusTraversal.NextInTab(order: order, current: c, backwards: false)
        ); // forward wrap
        Assert.Same(
            expected: c,
            actual: FocusTraversal.NextInTab(order: order, current: a, backwards: true)
        ); // backward wrap
        Assert.Same(
            expected: a,
            actual: FocusTraversal.NextInTab(order: order, current: null, backwards: false)
        ); // nothing focused → first
    }

    [Fact]
    public void Directional_PicksNearestNeighbourInPressedDirection()
    {
        var a = Btn();
        var b = Btn();
        var c = Btn();
        var row = new Row {
            Children = {
                a,
                b,
                c,
            },
        };
        LayOut(row);
        var order = FocusTraversal.Focusables(row);

        Assert.Same(
            expected: c,
            actual: FocusTraversal.Directional(
                order: order,
                current: b,
                dx: 1f,
                dy: 0f
            )
        ); // right
        Assert.Same(
            expected: a,
            actual: FocusTraversal.Directional(
                order: order,
                current: b,
                dx: -1f,
                dy: 0f
            )
        ); // left
        Assert.Null(
            FocusTraversal.Directional(
                order: order,
                current: c,
                dx: 1f,
                dy: 0f
            )
        ); // nothing to the right of the last
    }

    [Fact]
    public void Directional_AcrossRows()
    {
        var top = Btn();
        var bottom = Btn();
        var col = new Column {
            Children = {
                top,
                bottom,
            },
        };
        LayOut(col);
        var order = FocusTraversal.Focusables(col);

        Assert.Same(
            expected: bottom,
            actual: FocusTraversal.Directional(
                order: order,
                current: top,
                dx: 0f,
                dy: 1f
            )
        ); // down
        Assert.Same(
            expected: top,
            actual: FocusTraversal.Directional(
                order: order,
                current: bottom,
                dx: 0f,
                dy: -1f
            )
        ); // up
    }
}
