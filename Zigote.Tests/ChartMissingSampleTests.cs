using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Charts.Scales;

namespace Zigote.Tests;

/// <summary>
///     A missing sample is normally encoded as NaN. The scales keep it out of the domain, and every
///     data-driven mark has to keep it out of the paint list too: a non-finite coordinate in a paint
///     command is a hard tripwire, so one bad value used to take the whole chart down.
/// </summary>
public class ChartMissingSampleTests
{
    private static PaintList PaintOnce(Chart chart)
    {
        chart.Measure(Constraints.Tight(width: 220f, height: 160f));
        chart.Layout(Offset.Zero);
        var paint = new PaintList();
        chart.Paint(paint);
        paint.Validate();
        return paint;
    }

    [Theory]
    [InlineData(ChartInterpolation.Monotone)]
    [InlineData(ChartInterpolation.Linear)]
    [InlineData(ChartInterpolation.Step)]
    public void Line_WithAMissingSample_PaintsTheRest(ChartInterpolation interpolation)
    {
        var line = LineMark.Of([1.0, double.NaN, 3.0, 4.0]);
        line.Interpolation = interpolation;
        line.ShowSymbols = true;
        var paint = PaintOnce(new Chart { Animated = false, Marks = { line } });
        Assert.NotEmpty(paint.DebugCommands);
    }

    [Fact]
    public void Line_OfOnlyMissingSamples_PaintsNoStroke()
    {
        PaintOnce(new Chart { Animated = false, Marks = { LineMark.Of([double.NaN, double.NaN]) } });
    }

    [Fact]
    public void Point_WithAMissingSample_PaintsTheRest()
    {
        var points = PointMark.Of(xs: [0.0, 1.0, 2.0], ys: [1.0, double.NaN, 3.0]);
        PaintOnce(new Chart { Animated = false, Marks = { points } });
    }

    [Theory]
    [InlineData(ChartInterpolation.Monotone)]
    [InlineData(ChartInterpolation.Linear)]
    public void Area_WithAMissingSample_PaintsTheRest(ChartInterpolation interpolation)
    {
        var area = AreaMark.Of([1.0, double.NaN, 3.0, 2.0]);
        area.Interpolation = interpolation;
        PaintOnce(new Chart { Animated = false, Marks = { area } });
    }

    [Fact]
    public void Bar_WithAMissingSample_PaintsTheRest()
    {
        var data = new List<(string Key, double Value)> { ("a", 1.0), ("b", double.NaN), ("c", 3.0) };
        var bars = BarMark.Of(data: data, x: d => d.Key, y: d => d.Value);
        PaintOnce(new Chart { Animated = false, Marks = { bars } });
    }

    [Fact]
    public void Stacking_LeavesAMissingSampleOutOfItsColumn()
    {
        // One NaN used to poison the running sum and blank every series stacked with it.
        var x = ChartValue.Category("q1");
        var result = new Dictionary<(string Series, ChartValue X), StackedSpan>();
        StackCompute.Compute(
            points: [("a", x, 2.0), ("b", x, double.NaN), ("c", x, 3.0)],
            seriesOrder: ["a", "b", "c"],
            mode: ChartStacking.Standard,
            result: result
        );

        Assert.Equal(expected: new StackedSpan(Bottom: 0, Top: 2), actual: result[("a", x)]);
        Assert.False(result.ContainsKey(("b", x)));
        Assert.Equal(expected: new StackedSpan(Bottom: 2, Top: 5), actual: result[("c", x)]);
    }

    [Fact]
    public void Ticks_TerminateWhenTheStepIsBelowDoublePrecision()
    {
        // At 2^53 adding 1 is a no-op, so the accumulating loop never advanced and grew the list
        // until memory ran out.
        const double big = 9007199254740992d; // 2^53
        var ticks = NiceScale.Ticks(min: big, max: big + 2, step: 1);

        Assert.InRange(actual: ticks.Count, low: 1, high: 3);
        Assert.Equal(expected: ticks.Distinct().Order(), actual: ticks);
    }

    [Fact]
    public void Chart_WithValuesBeyondDoublePrecision_LaysOutAndPaints()
    {
        PaintOnce(
            new Chart {
                Animated = false,
                Marks = { LineMark.Of([9007199254740992d, 9007199254740994d]) },
            }
        );
    }

    [Fact]
    public void Ticks_AreUnchangedForOrdinaryRanges()
    {
        Assert.Equal(expected: [0, 0.5, 1, 1.5, 2], actual: NiceScale.Ticks(min: 0, max: 2, step: 0.5));
        Assert.Equal(expected: [-1, 0, 1], actual: NiceScale.Ticks(min: -1, max: 1, step: 1));
        Assert.Empty(NiceScale.Ticks(min: 0, max: 1, step: 0));
        Assert.Empty(NiceScale.Ticks(min: double.NaN, max: 1, step: 1));
    }
}
