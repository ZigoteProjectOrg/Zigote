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
        var a = new SizedBox(width: 40, height: 10);
        var b = new SizedBox(width: 60, height: 20);
        var row = new Row([a, b]) {
            MainAxisSize = MainAxisSize.Min,
            CrossAxisAlignment = CrossAxisAlignment.Start,
        };

        var size = row.Measure(new Constraints(maxWidth: 200, maxHeight: 100));
        row.Layout(new Offset(x: 0, y: 0));

        Assert.Equal(expected: 100f, actual: size.Width, precision: 3); // 40 + 60
        Assert.Equal(expected: 20f, actual: size.Height, precision: 3); // max(10, 20)
        Assert.Equal(expected: 0f, actual: a.Bounds.X, precision: 3);
        Assert.Equal(expected: 40f, actual: b.Bounds.X, precision: 3);
    }

    [Fact]
    public void Row_WithExpanded_FillsMainAxis_AndIsStableAcrossReuse()
    {
        // Exercises the flex pass (pass 1 writes no slots, pass 2 writes them) through the reused
        // metrics buffer: the second Measure hits the Array.Clear reuse path and must match the first.
        var e0 = new Expanded(new SizedBox(width: 0, height: 10));
        var e1 = new Expanded(new SizedBox(width: 0, height: 10));
        var row = new Row([e0, e1]) { CrossAxisAlignment = CrossAxisAlignment.Start };
        var c = new Constraints(maxWidth: 200, maxHeight: 100);

        var first = row.Measure(c);
        var second = row.Measure(c);
        row.Layout(new Offset(x: 0, y: 0));

        Assert.Equal(
            expected: 200f,
            actual: first.Width,
            precision: 2
        ); // MainAxisSize.Max fills the available width
        Assert.Equal(
            expected: first.Width,
            actual: second.Width,
            precision: 3
        ); // reuse path is stable
        // Two equal flex factors get equal shares, so the second child starts at the midpoint.
        Assert.Equal(expected: e0.Bounds.Width, actual: e1.Bounds.Width, precision: 2);
        Assert.Equal(expected: e0.Bounds.Width, actual: e1.Bounds.X, precision: 2);
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
            var header =
                new Column([new SizedBox(width: 0, height: 40)]) { MainAxisSize = headerSize };
            var body = new SizedBox(); // tracked Expanded leaf
            var content = new Column([header, new Expanded(body)]) {
                CrossAxisAlignment = CrossAxisAlignment.Stretch,
            };
            content.Measure(
                new Constraints(
                    minWidth: 700,
                    maxWidth: 700,
                    minHeight: 500,
                    maxHeight: 500
                )
            ); // tight 700×500
            content.Layout(new Offset(x: 0, y: 0));
            return (header.Bounds.Height, body.Bounds.Height);
        }

        var bug = LayoutWith(MainAxisSize.Max);
        Assert.Equal(
            expected: 500f,
            actual: bug.Header,
            precision: 1
        ); // greedy header eats the full height
        Assert.Equal(expected: 0f, actual: bug.Body, precision: 1); // ...starving the Expanded body

        var ok = LayoutWith(MainAxisSize.Min);
        Assert.Equal(
            expected: 40f,
            actual: ok.Header,
            precision: 1
        ); // header shrinks to its content
        Assert.Equal(
            expected: 460f,
            actual: ok.Body,
            precision: 1
        ); // Expanded body now fills the remainder
    }

    [Fact]
    public void Remeasure_IsStable_AcrossReusedBuffer()
    {
        var a = new SizedBox(width: 30, height: 10);
        var b = new SizedBox(width: 50, height: 10);
        var row = new Row([a, b]) { MainAxisSize = MainAxisSize.Min };
        var c = new Constraints(maxWidth: 200, maxHeight: 100);

        var first = row.Measure(c);
        for (int i = 0; i < 5; i++)
        {
            var again = row.Measure(c);
            Assert.Equal(expected: first.Width, actual: again.Width, precision: 3);
            Assert.Equal(expected: first.Height, actual: again.Height, precision: 3);
        }
    }

    [Fact]
    public void ChildCountChange_DoesNotLeakStaleMetrics()
    {
        var row = new Row(
            [
                new SizedBox(width: 30, height: 10), new SizedBox(width: 30, height: 10),
                new SizedBox(width: 30, height: 10),
            ]
        ) {
            MainAxisSize = MainAxisSize.Min,
        };
        var c = new Constraints(maxWidth: 200, maxHeight: 100);

        var three = row.Measure(c);
        Assert.Equal(expected: 90f, actual: three.Width, precision: 3);

        // Shrink to one child — the reused (larger) buffer must not contribute a phantom third box.
        row.SetChildren([new SizedBox(width: 30, height: 10)]);
        var one = row.Measure(c);
        Assert.Equal(expected: 30f, actual: one.Width, precision: 3);

        // Grow back beyond the original count — buffer must expand and stay correct.
        row.SetChildren(
            [
                new SizedBox(width: 20, height: 10), new SizedBox(width: 20, height: 10),
                new SizedBox(width: 20, height: 10), new SizedBox(width: 20, height: 10),
            ]
        );
        var four = row.Measure(c);
        Assert.Equal(expected: 80f, actual: four.Width, precision: 3);
    }
}
