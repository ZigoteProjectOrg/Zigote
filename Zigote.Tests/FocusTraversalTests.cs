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
            Child = new SizedBox(w, h),
            Enabled = enabled,
        };
    }

    private static void LayOut(Widget w, float width = 400f, float height = 200f)
    {
        w.Measure(Constraints.Loose(width, height));
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
            new Widget[] {
                a,
                b,
                c,
            },
            order
        );
    }

    [Fact]
    public void Focusables_SkipCollapsedAndDisabled()
    {
        var visible = Btn();
        var collapsed = Btn(0f, 0f);
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
        Assert.Same(visible, order[0]);
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

        Assert.Same(b, FocusTraversal.NextInTab(order, a, false));
        Assert.Same(a, FocusTraversal.NextInTab(order, c, false)); // forward wrap
        Assert.Same(c, FocusTraversal.NextInTab(order, a, true)); // backward wrap
        Assert.Same(a, FocusTraversal.NextInTab(order, null, false)); // nothing focused → first
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
            c,
            FocusTraversal.Directional(
                order,
                b,
                1f,
                0f
            )
        ); // right
        Assert.Same(
            a,
            FocusTraversal.Directional(
                order,
                b,
                -1f,
                0f
            )
        ); // left
        Assert.Null(
            FocusTraversal.Directional(
                order,
                c,
                1f,
                0f
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
            bottom,
            FocusTraversal.Directional(
                order,
                top,
                0f,
                1f
            )
        ); // down
        Assert.Same(
            top,
            FocusTraversal.Directional(
                order,
                bottom,
                0f,
                -1f
            )
        ); // up
    }
}