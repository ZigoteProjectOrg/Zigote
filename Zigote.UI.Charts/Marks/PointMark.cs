using Zigote.Core;
using Zigote.UI.Charts.Rendering;

namespace Zigote.UI.Charts.Marks;

public enum ChartSymbol : byte
{
    Circle,
    Square,

    /// <summary>Hollow circle (2px ring of the series color).</summary>
    Ring,

    /// <summary>Filled upward triangle (native polygon fill).</summary>
    Triangle,

    /// <summary>Filled diamond (square rotated 45°, native polygon fill).</summary>
    Diamond,
}

public static class PointMark
{
    public static PointMark<T> Of<T>(IReadOnlyList<T> data, Func<T, ChartValue> x,
        Func<T, ChartValue> y,
        Func<T, string>? series = null)
    {
        return new PointMark<T>(data, x, y) { SeriesBy = series };
    }

    /// <summary>Vectorized: scatter paired xs/ys arrays directly — no row type needed.</summary>
    public static PointMark<ChartSample> Of(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        return new PointMark<ChartSample>(
            ChartSamples.Pair(xs, ys),
            ChartSamples.X,
            ChartSamples.Y
        );
    }
}

/// <summary>
///     Scatter symbols at each datum. <see cref="SizeBy" /> turns it into a bubble chart: symbol
///     area scales with the selected value (√-scaled so area, not diameter, encodes magnitude).
/// </summary>
public class PointMark<T>(IReadOnlyList<T> data, Func<T, ChartValue> x, Func<T, ChartValue> y)
    : SeriesMark<T>(data, x, y)
{
    private double _maxSizeValue;

    public ChartSymbol Symbol { get; set; } = ChartSymbol.Circle;
    public float Size { get; set; } = 8f;
    public float Opacity { get; set; } = 1f;

    /// <summary>Optional per-datum magnitude mapped to symbol area (bubble charts).</summary>
    public Func<T, double>? SizeBy { get; set; }

    /// <summary>Symbol diameter for the largest <see cref="SizeBy" /> value.</summary>
    public float MaxSize { get; set; } = 28f;

    public override void IncludeDomain(ChartDomain domain)
    {
        ResolveData(EpochChanged(domain));
        if (Resolved.Count == 0) return;
        var xs = domain.X(Resolved[0].X);
        var ys = domain.Y(Resolved[0].Y, UseSecondaryYAxis);
        foreach (var p in Resolved)
        {
            xs.Include(p.X);
            ys.Include(p.Y);
        }

        _maxSizeValue = 0;
        if (SizeBy is not null)
            foreach (var d in Data)
                _maxSizeValue = Math.Max(_maxSizeValue, Math.Abs(SizeBy(d)));
    }

    public override void CollectInteractive(ChartRenderContext ctx)
    {
        foreach (var p in Resolved)
            ctx.HoverPoints.Add(
                new ChartDataPoint(
                    ctx.MapX(p.X),
                    ctx.MapY(p.Y),
                    p.X,
                    p.Y,
                    p.Series,
                    FormatValue(p.Y),
                    ctx.ColorFor(p.Series, Color, MarkIndex)
                )
            );
    }

    public override void Paint(ChartRenderContext ctx)
    {
        var paint = ctx.Paint;
        if (paint is null) return;

        // Entrance animation: symbols fade + scale in.
        var alpha = Opacity * ctx.Progress;
        if (alpha <= 0.001f) return;

        for (var i = 0; i < Resolved.Count; i++)
        {
            var p = Resolved[i];
            var cx = ctx.MapX(p.X);
            var cy = p.Y.Kind == ChartValueKind.Category
                ? ctx.MapY(p.Y)
                : ctx.MapYNumeric(MorphedY(ctx, p));
            var d = Diameter(i) * ctx.Progress;
            if (d < 0.5f) continue;
            var r = d / 2f;
            var color = ctx.ColorFor(p.Series, Color, MarkIndex).WithAlpha(alpha);
            var rect = new Rect(
                cx - r,
                cy - r,
                d,
                d
            );
            switch (Symbol)
            {
                case ChartSymbol.Square:
                    paint.AddRect(rect, color, 1.5f);
                    break;
                case ChartSymbol.Ring:
                    paint.AddBorder(
                        rect,
                        color,
                        r,
                        2f
                    );
                    break;
                case ChartSymbol.Triangle:
                    // Upward equilateral triangle, filled via the native polygon primitive.
                    const float sin60 = 0.8660254f;
                    paint.AddPolygon(
                        [
                            new Offset(cx, cy - r),
                            new Offset(cx + r * sin60, cy + r * 0.5f),
                            new Offset(cx - r * sin60, cy + r * 0.5f),
                        ],
                        color
                    );
                    break;
                case ChartSymbol.Diamond:
                    paint.AddPolygon(
                        [
                            new Offset(cx, cy - r),
                            new Offset(cx + r, cy),
                            new Offset(cx, cy + r),
                            new Offset(cx - r, cy),
                        ],
                        color
                    );
                    break;
                default:
                    paint.AddRect(rect, color, r);
                    break;
            }
        }
    }

    private float Diameter(int index)
    {
        if (SizeBy is null || _maxSizeValue <= 0) return Size;
        var v = Math.Abs(SizeBy(Data[index]));
        return MathF.Max(2f, MaxSize * MathF.Sqrt((float)(v / _maxSizeValue)));
    }
}
