using Xunit;
using Zigote.Core;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Exercises Row/Column flex distribution through the public widget API, and specifically guards
///     the
///     metrics-array reuse refactor in <c>FlexLayout.Measure</c>: re-measuring and changing child
///     count
///     must never read stale slots from a reused buffer.
/// </summary>
public class FlexLayoutTests
{
    [Fact]
    public void Row_PlacesFixedChildrenSequentially()
    {
        var a = new SizedBox(40, 10);
        var b = new SizedBox(60, 20);
        var row = new Row([a, b]) {
            MainAxisSize = MainAxisSize.Min,
            CrossAxisAlignment = CrossAxisAlignment.Start,
        };

        var size = row.Measure(new Constraints(maxWidth: 200, maxHeight: 100));
        row.Layout(new Offset(0, 0));

        Assert.Equal(100f, size.Width, 3); // 40 + 60
        Assert.Equal(20f, size.Height, 3); // max(10, 20)
        Assert.Equal(0f, a.Bounds.X, 3);
        Assert.Equal(40f, b.Bounds.X, 3);
    }

    [Fact]
    public void Row_WithExpanded_FillsMainAxis_AndIsStableAcrossReuse()
    {
        // Exercises the flex pass (pass 1 writes no slots, pass 2 writes them) through the reused
        // metrics buffer: the second Measure hits the Array.Clear reuse path and must match the first.
        var e0 = new Expanded(new SizedBox(0, 10));
        var e1 = new Expanded(new SizedBox(0, 10));
        var row = new Row([e0, e1]) { CrossAxisAlignment = CrossAxisAlignment.Start };
        var c = new Constraints(maxWidth: 200, maxHeight: 100);

        var first = row.Measure(c);
        var second = row.Measure(c);
        row.Layout(new Offset(0, 0));

        Assert.Equal(200f, first.Width, 2); // MainAxisSize.Max fills the available width
        Assert.Equal(first.Width, second.Width, 3); // reuse path is stable
        // Two equal flex factors get equal shares, so the second child starts at the midpoint.
        Assert.Equal(e0.Bounds.Width, e1.Bounds.Width, 2);
        Assert.Equal(e0.Bounds.Width, e1.Bounds.X, 2);
    }

    [Fact]
    public void Column_NonFlexMaxSibling_StarvesExpanded_UnlessMinSized()
    {
        // Pins the CodeEditor-demo layout gotcha: a plain Column defaults to MainAxisSize.Max, so a
        // *non-flex* Column used as a header/content block greedily consumes the whole main axis,
        // starving an Expanded sibling below it (the editors collapsed to zero height). Shrinking that
        // block to MainAxisSize.Min frees the remaining space for the Expanded child.
        static (float Header, float Body) LayoutWith(MainAxisSize headerSize)
        {
            var header = new Column([new SizedBox(0, 40)]) { MainAxisSize = headerSize };
            var body = new SizedBox(); // tracked Expanded leaf
            var content = new Column([header, new Expanded(body)]) {
                CrossAxisAlignment = CrossAxisAlignment.Stretch,
            };
            content.Measure(
                new Constraints(
                    700,
                    700,
                    500,
                    500
                )
            ); // tight 700×500
            content.Layout(new Offset(0, 0));
            return (header.Bounds.Height, body.Bounds.Height);
        }

        var bug = LayoutWith(MainAxisSize.Max);
        Assert.Equal(500f, bug.Header, 1); // greedy header eats the full height
        Assert.Equal(0f, bug.Body, 1); // ...starving the Expanded body

        var ok = LayoutWith(MainAxisSize.Min);
        Assert.Equal(40f, ok.Header, 1); // header shrinks to its content
        Assert.Equal(460f, ok.Body, 1); // Expanded body now fills the remainder
    }

    [Fact]
    public void Remeasure_IsStable_AcrossReusedBuffer()
    {
        var a = new SizedBox(30, 10);
        var b = new SizedBox(50, 10);
        var row = new Row([a, b]) { MainAxisSize = MainAxisSize.Min };
        var c = new Constraints(maxWidth: 200, maxHeight: 100);

        var first = row.Measure(c);
        for (var i = 0; i < 5; i++)
        {
            var again = row.Measure(c);
            Assert.Equal(first.Width, again.Width, 3);
            Assert.Equal(first.Height, again.Height, 3);
        }
    }

    [Fact]
    public void ChildCountChange_DoesNotLeakStaleMetrics()
    {
        var row = new Row([new SizedBox(30, 10), new SizedBox(30, 10), new SizedBox(30, 10)]) {
            MainAxisSize = MainAxisSize.Min,
        };
        var c = new Constraints(maxWidth: 200, maxHeight: 100);

        var three = row.Measure(c);
        Assert.Equal(90f, three.Width, 3);

        // Shrink to one child — the reused (larger) buffer must not contribute a phantom third box.
        row.SetChildren([new SizedBox(30, 10)]);
        var one = row.Measure(c);
        Assert.Equal(30f, one.Width, 3);

        // Grow back beyond the original count — buffer must expand and stay correct.
        row.SetChildren(
            [new SizedBox(20, 10), new SizedBox(20, 10), new SizedBox(20, 10), new SizedBox(20, 10)]
        );
        var four = row.Measure(c);
        Assert.Equal(80f, four.Width, 3);
    }
}
