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
            new("W1", 40),
            new("W2", 91),
            new("W3", 66),
        };
        var chart = new Chart {
            Animated = false,
            Marks = { BarMark.Of(rows, d => d.M, d => d.V) },
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
                0,
                width,
                0,
                80
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
        for (var i = 0; i < 6; i++)
        {
            data.Add(new Pt(i, 10 + i, "Alpha series"));
            data.Add(new Pt(i, 20 + i, "Beta series"));
        }

        var chart = new Chart {
            Animated = false,
            Marks = {
                LineMark.Of(
                    data,
                    d => d.X,
                    d => d.Y,
                    d => d.S
                ),
            },
        };
        chart.Measure(
            new Constraints(
                0,
                width,
                0,
                height
            )
        );
        chart.Layout(Offset.Zero);

        var plot = chart.PlotRect;
        chart.OnPointerMove(new Offset(plot.X + plot.Width * 0.5f, plot.Y + plot.Height * 0.5f));

        var paint = new PaintList();
        chart.Paint(paint); // was: System.ArgumentException min > max in the tooltip card clamp
        paint.Validate();
    }

    private static void PaintOnce(Chart chart, float w = 220f, float h = 160f)
    {
        chart.Measure(
            new Constraints(
                0,
                w,
                0,
                h
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
        var donut = SectorMark.Of(data, d => d.V, d => d.Name);
        donut.InnerRadiusFraction = 0.5f;
        PaintOnce(
            new Chart {
                Animated = false,
                Marks = { donut },
            },
            200f,
            150f
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
        var heat = RectangleMark.Of(cells, c => c.X, c => c.Y);
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
                Marks = { LineMark.Of(data, d => d.T, d => d.V) },
            }
        );
    }

    private readonly record struct Bar(string M, double V);

    private readonly record struct Pt(double X, double Y, string S);
}