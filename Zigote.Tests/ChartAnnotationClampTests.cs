using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Charts;
using Zigote.UI.Charts.Marks;

namespace Zigote.Tests;

public class ChartAnnotationClampTests
{
    // Regression: an annotation whose label is wider than a very narrow plot inverted the clamp bounds
    // (min > max) and threw ArgumentException from PaintAnnotations. It must paint without throwing.
    [Theory]
    [InlineData(40f)] // plot far narrower than the label
    [InlineData(70f)]
    [InlineData(140f)]
    public void Annotation_InNarrowPlot_DoesNotThrow(float width)
    {
        var rows = new List<Bar> {
            new(M: "W1", V: 40),
            new(M: "W2", V: 91),
            new(M: "W3", V: 66),
        };
        var chart = new Chart {
            Animated = false,
            Marks = { BarMark.Of(data: rows, x: d => d.M, y: d => d.V) },
        };
        chart.Annotations.Add(
            new ChartAnnotation {
                X = "W2",
                Y = 91,
                Text = "a very wide peak annotation label",
                Placement = ChartAnnotationPlacement.Above,
            }
        );

        chart.Measure(
            new Constraints(
                minWidth: 0,
                maxWidth: width,
                minHeight: 0,
                maxHeight: 80
            )
        );
        chart.Layout(Offset.Zero);
        var paint = new PaintList();
        chart.Paint(paint); // was: System.ArgumentException min > max
        paint.Validate();
    }

    // Regression: hovering a chart whose tooltip card is wider/taller than the (narrow/short) plot
    // inverted the tooltip-card clamp bounds and threw from the hover paint path.
    [Theory]
    [InlineData(60f, 60f)]
    [InlineData(90f, 70f)]
    public void HoverTooltip_InTinyPlot_DoesNotThrow(float width, float height)
    {
        var data = new List<Pt>();
        for (int i = 0; i < 6; i++)
        {
            data.Add(new Pt(X: i, Y: 10 + i, S: "Alpha series"));
            data.Add(new Pt(X: i, Y: 20 + i, S: "Beta series"));
        }

        var chart = new Chart {
            Animated = false,
            Marks = {
                LineMark.Of(
                    data: data,
                    x: d => d.X,
                    y: d => d.Y,
                    series: d => d.S
                ),
            },
        };
        chart.Measure(
            new Constraints(
                minWidth: 0,
                maxWidth: width,
                minHeight: 0,
                maxHeight: height
            )
        );
        chart.Layout(Offset.Zero);

        var plot = chart.PlotRect;
        chart.OnPointerMove(
            new Offset(x: plot.X + (plot.Width * 0.5f), y: plot.Y + (plot.Height * 0.5f))
        );

        var paint = new PaintList();
        chart.Paint(paint); // was: System.ArgumentException min > max in the tooltip card clamp
        paint.Validate();
    }

    private static void PaintOnce(Chart chart, float w = 220f, float h = 160f)
    {
        chart.Measure(
            new Constraints(
                minWidth: 0,
                maxWidth: w,
                minHeight: 0,
                maxHeight: h
            )
        );
        chart.Layout(Offset.Zero);
        var paint = new PaintList();
        chart.Paint(paint); // must not throw
        paint.Validate();
    }

    // Regression: a NaN slice value made the pie total NaN, defeated the `total <= 0` guard, and produced
    // NaN wedge angles that threw "NaN in polygon point" from the fill.
    [Fact]
    public void Donut_WithNaNValue_DoesNotThrow()
    {
        var data = new List<(string Name, double V)> {
            ("A", 50),
            ("B", double.NaN),
            ("C", 20),
        };
        var donut = SectorMark.Of(data: data, value: d => d.V, category: d => d.Name);
        donut.InnerRadiusFraction = 0.5f;
        PaintOnce(
            chart: new Chart {
                Animated = false,
                Marks = { donut },
            },
            w: 200f,
            h: 150f
        );
    }

    // Regression: a NaN heatmap magnitude produced a NaN colour that threw "NaN in color".
    [Fact]
    public void Heatmap_WithNaNFill_DoesNotThrow()
    {
        var cells = new List<(string X, string Y, double M)> {
            ("A", "R", 2),
            ("B", "R", double.NaN),
            ("C", "R", 8),
        };
        var heat = RectangleMark.Of(data: cells, x: c => c.X, y: c => c.Y);
        heat.FillBy = c => c.M;
        PaintOnce(
            new Chart {
                Animated = false,
                Marks = { heat },
                LegendPosition = ChartLegendPosition.Hidden,
            }
        );
    }

    // Regression: a DateTime at/near DateTime.MaxValue rounded the tick count to MaxTicks+1 and threw
    // ArgumentOutOfRangeException from `new DateTime(long)` in the time-scale tick builder.
    [Fact]
    public void TimeScale_AtMaxValue_DoesNotThrow()
    {
        var data = new List<(DateTime T, double V)> {
            (DateTime.MaxValue, 10), // single point at the very top of the representable range
        };
        PaintOnce(
            new Chart {
                Animated = false,
                Marks = { LineMark.Of(data: data, x: d => d.T, y: d => d.V) },
            }
        );
    }

    private readonly record struct Bar(string M, double V);

    private readonly record struct Pt(double X, double Y, string S);
}
