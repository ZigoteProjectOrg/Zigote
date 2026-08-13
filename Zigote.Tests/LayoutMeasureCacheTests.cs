using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Headless coverage for the Stack single-measure paths, Container constraint compliance and
///     the Label wrap-cache key (deterministic heuristic measurer: width = chars × fontSize × 0.55).
/// </summary>
public class LayoutMeasureCacheTests
{
    // ── Stack: no double-measure under tight constraints ──

    [Fact]
    public void Stack_TightConstraints_MeasuresEachChildOnce()
    {
        var small = new MeasureCountingBox(width: 10f, height: 10f);
        var fill = new FillBox();
        var stack = new Stack([small, fill]);

        var size = stack.Measure(Constraints.Tight(width: 100f, height: 50f));

        Assert.Equal(expected: new Size(width: 100f, height: 50f), actual: size);
        Assert.Equal(expected: 1, actual: small.Measures);
        Assert.Equal(expected: 1, actual: fill.Measures);
    }

    [Fact]
    public void Stack_LooseConstraints_SkipsRefillWhenProbeMatchesStackSize()
    {
        var small = new MeasureCountingBox(width: 10f, height: 10f);
        var big = new MeasureCountingBox(width: 30f, height: 30f);
        var stack = new Stack([small, big]);

        var size = stack.Measure(new Constraints(maxWidth: 100f, maxHeight: 100f));

        // The biggest child defines the stack size, so its probe result is final; the smaller
        // child must be re-measured with the fill constraints.
        Assert.Equal(expected: new Size(width: 30f, height: 30f), actual: size);
        Assert.Equal(expected: 1, actual: big.Measures);
        Assert.Equal(expected: 2, actual: small.Measures);
    }

    // ── Container: constraint compliance ──

    [Fact]
    public void Container_TightCell_ForcesCellSize()
    {
        var container = new Container(new Container(width: 10, height: 10));

        var size = container.Measure(Constraints.Tight(width: 200f, height: 100f));

        Assert.Equal(expected: new Size(width: 200f, height: 100f), actual: size);
    }

    [Fact]
    public void Container_Childless_UnboundedSpace_SizesToConstraintMinimum()
    {
        var size = new Container().Measure(Constraints.Unbounded);
        Assert.Equal(expected: Size.Zero, actual: size);

        var withMin = new Container().Measure(new Constraints(minWidth: 50f, minHeight: 20f));
        Assert.Equal(expected: new Size(width: 50f, height: 20f), actual: withMin);
    }

    [Fact]
    public void Container_WithChild_RespectsMinConstraints()
    {
        var container = new Container(new Container(width: 10, height: 10));

        var size = container.Measure(
            new Constraints(
                minWidth: 120f,
                maxWidth: 300f,
                minHeight: 40f,
                maxHeight: 300f
            )
        );

        Assert.Equal(expected: new Size(width: 120f, height: 40f), actual: size);
    }

    // ── Label: wrap-cache key covers MaxLines / Overflow ──

    [Fact]
    public void Label_Rewraps_WhenMaxLinesChanges()
    {
        var label = new Label("aaaa bbbb cccc dddd eeee") {
            FontSize = 10f,
            LineHeight = 1.2f,
        };
        var c = new Constraints(maxWidth: 60f, maxHeight: 600f);

        // Wraps to "aaaa bbbb" / "cccc dddd" / "eeee" → 3 lines of 12 px.
        var unbounded = label.Measure(c);
        Assert.Equal(expected: 36f, actual: unbounded.Height, precision: 2);

        label.MaxLines = 2;
        var capped = label.Measure(c);
        Assert.Equal(expected: 24f, actual: capped.Height, precision: 2);
    }

    [Fact]
    public void Label_Rewraps_WhenOverflowChanges()
    {
        var label = new Label("aaaa bbbb cccc dddd eeee") {
            FontSize = 10f,
            LineHeight = 1.2f,
            MaxLines = 2,
        };
        var c = new Constraints(maxWidth: 60f, maxHeight: 600f);

        // Clip keeps the raw joined remainder (width capped by the constraints).
        var clipped = label.Measure(c);
        Assert.Equal(expected: 60f, actual: clipped.Width, precision: 2);

        // Ellipsis re-fits the last line to "cccc dddd…" (10 chars × 5.5 px).
        label.Overflow = TextOverflow.Ellipsis;
        var ellipsized = label.Measure(c);
        Assert.Equal(expected: 55f, actual: ellipsized.Width, precision: 2);
    }

    private sealed class MeasureCountingBox(float width, float height) : LeafWidget
    {
        public int Measures;

        public override Size Measure(Constraints c)
        {
            Measures++;
            return c.Constrain(new Size(width: width, height: height));
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: width,
                height: height
            );
        }

        public override void Paint(PaintList paint) { }
    }

    private sealed class FillBox : LeafWidget
    {
        public int Measures;

        public override Size Measure(Constraints c)
        {
            Measures++;
            return new Size(
                width: float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth,
                height: float.IsFinite(c.MaxHeight) ? c.MaxHeight : c.MinHeight
            );
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: MeasuredSize.Width,
                height: MeasuredSize.Height
            );
        }

        public override void Paint(PaintList paint) { }
    }
}
