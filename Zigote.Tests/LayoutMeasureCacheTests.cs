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
        var small = new MeasureCountingBox(10f, 10f);
        var fill = new FillBox();
        var stack = new Stack([small, fill]);

        var size = stack.Measure(Constraints.Tight(100f, 50f));

        Assert.Equal(new Size(100f, 50f), size);
        Assert.Equal(1, small.Measures);
        Assert.Equal(1, fill.Measures);
    }

    [Fact]
    public void Stack_LooseConstraints_SkipsRefillWhenProbeMatchesStackSize()
    {
        var small = new MeasureCountingBox(10f, 10f);
        var big = new MeasureCountingBox(30f, 30f);
        var stack = new Stack([small, big]);

        var size = stack.Measure(new Constraints(maxWidth: 100f, maxHeight: 100f));

        // The biggest child defines the stack size, so its probe result is final; the smaller
        // child must be re-measured with the fill constraints.
        Assert.Equal(new Size(30f, 30f), size);
        Assert.Equal(1, big.Measures);
        Assert.Equal(2, small.Measures);
    }

    // ── Container: constraint compliance ──

    [Fact]
    public void Container_TightCell_ForcesCellSize()
    {
        var container = new Container(new Container(width: 10, height: 10));

        var size = container.Measure(Constraints.Tight(200f, 100f));

        Assert.Equal(new Size(200f, 100f), size);
    }

    [Fact]
    public void Container_Childless_UnboundedSpace_SizesToConstraintMinimum()
    {
        var size = new Container().Measure(Constraints.Unbounded);
        Assert.Equal(Size.Zero, size);

        var withMin = new Container().Measure(new Constraints(50f, minHeight: 20f));
        Assert.Equal(new Size(50f, 20f), withMin);
    }

    [Fact]
    public void Container_WithChild_RespectsMinConstraints()
    {
        var container = new Container(new Container(width: 10, height: 10));

        var size = container.Measure(
            new Constraints(
                120f,
                300f,
                40f,
                300f
            )
        );

        Assert.Equal(new Size(120f, 40f), size);
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
        Assert.Equal(36f, unbounded.Height, 2);

        label.MaxLines = 2;
        var capped = label.Measure(c);
        Assert.Equal(24f, capped.Height, 2);
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
        Assert.Equal(60f, clipped.Width, 2);

        // Ellipsis re-fits the last line to "cccc dddd…" (10 chars × 5.5 px).
        label.Overflow = TextOverflow.Ellipsis;
        var ellipsized = label.Measure(c);
        Assert.Equal(55f, ellipsized.Width, 2);
    }

    private sealed class MeasureCountingBox(float width, float height) : LeafWidget
    {
        public int Measures;

        public override Size Measure(Constraints c)
        {
            Measures++;
            return c.Constrain(new Size(width, height));
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                width,
                height
            );
        }

        public override void Paint(PaintList paint)
        {
        }
    }

    private sealed class FillBox : LeafWidget
    {
        public int Measures;

        public override Size Measure(Constraints c)
        {
            Measures++;
            return new Size(
                float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth,
                float.IsFinite(c.MaxHeight) ? c.MaxHeight : c.MinHeight
            );
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                MeasuredSize.Width,
                MeasuredSize.Height
            );
        }

        public override void Paint(PaintList paint)
        {
        }
    }
}
