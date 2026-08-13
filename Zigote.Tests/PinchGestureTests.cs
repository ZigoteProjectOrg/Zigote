using Xunit;
using Zigote.Core;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;
using Zigote.UI.Charts.Scales;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     The pinch-to-zoom seam (<see cref="Widget.CanTouchScale" /> /
///     <see cref="Widget.OnTouchScale" />).
///     Like the rest of the touch layer, the App-side finger routing that drives it needs a live
///     window (see <see cref="TouchInputTests" />); what is pinned here is the contract that routing
///     targets — who opts in, who declines so the gesture falls through to an ancestor, and that a
///     consumer scales about the focal point rather than its own centre.
/// </summary>
public class PinchGestureTests
{
    private static Chart LaidOut(Chart chart, float w = 600, float h = 300)
    {
        chart.Measure(Constraints.Tight(width: w, height: h));
        chart.Layout(Offset.Zero);
        return chart;
    }

    private static Chart ZoomableChart()
    {
        var data = Enumerable.Range(start: 0, count: 100).Select(i => ((double)i, (double)i))
            .ToList();
        return new Chart {
            Animated = false,
            ZoomableX = true,
            Marks = { LineMark.Of(data: data, x: d => d.Item1, y: d => d.Item2) },
        };
    }

    [Fact]
    public void PlainWidget_DeclinesTheGesture()
    {
        // The default must be "no": a widget that has not opted in would otherwise swallow pinches
        // meant for a zoomable ancestor.
        var box = new SizedBox(width: 100, height: 100);
        Assert.False(box.CanTouchScale());

        box.OnTouchScale(scale: 2f, focus: new Offset(x: 50, y: 50)); // no-op, must not throw
    }

    [Fact]
    public void Chart_OptsIn_OnlyWhenZoomable()
    {
        var plain = LaidOut(
            new Chart {
                Animated = false,
                Marks = {
                    LineMark.Of(
                        data: new[] {
                            (0.0, 0.0),
                            (1.0, 1.0),
                        },
                        x: d => d.Item1,
                        y: d => d.Item2
                    ),
                },
            }
        );
        Assert.False(plain.CanTouchScale());

        var zoomable = LaidOut(ZoomableChart());
        Assert.True(zoomable.CanTouchScale());

        zoomable.ZoomableX = false;
        Assert.False(zoomable.CanTouchScale());
    }

    [Fact]
    public void Chart_OnTouchScale_ZoomsAboutTheFocalPoint()
    {
        var chart = LaidOut(ZoomableChart());

        // Fingers spreading 2× centred on the plot: the window halves around the centre domain
        // value, so it stays under the fingers instead of drifting.
        var centre = new Offset(
            x: chart.PlotRect.X + (chart.PlotRect.Width / 2f),
            y: chart.PlotRect.Y + (chart.PlotRect.Height / 2f)
        );
        chart.OnTouchScale(scale: 2f, focus: centre);
        LaidOut(chart);

        var x = Assert.IsType<LinearScale>(chart.ResolvedXScale);
        Assert.Equal(
            expected: 0.5f,
            actual: x.NormalizeNumeric(50),
            precision: 2
        ); // focal domain value still centred
        Assert.True(x.NormalizeNumeric(24) < 0f); // window really did shrink
    }

    [Fact]
    public void Chart_OnTouchScale_Squeeze_UndoesSpread()
    {
        var chart = LaidOut(ZoomableChart());
        var centre = new Offset(
            x: chart.PlotRect.X + (chart.PlotRect.Width / 2f),
            y: chart.PlotRect.Y + (chart.PlotRect.Height / 2f)
        );

        // Scale arrives as a per-event multiplier, so a squeeze by the reciprocal must land back
        // where it started — otherwise a pinch in-and-out drifts the view.
        chart.OnTouchScale(scale: 2f, focus: centre);
        chart.OnTouchScale(scale: 0.5f, focus: centre);
        LaidOut(chart);

        var x = Assert.IsType<LinearScale>(chart.ResolvedXScale);
        Assert.True(x.NormalizeNumeric(10) is > 0f and < 0.2f); // full extent visible again
    }
}
