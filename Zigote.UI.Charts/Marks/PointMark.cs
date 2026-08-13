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
        Func<T, ChartValue> y, Func<T, string>? series = null) =>
        new(data: data, x: x, y: y) { SeriesBy = series };

    /// <summary>Vectorized: scatter paired xs/ys arrays directly — no row type needed.</summary>
    public static PointMark<ChartSample> Of(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        return new PointMark<ChartSample>(
            data: ChartSamples.Pair(xs: xs, ys: ys),
            x: ChartSamples.X,
            y: ChartSamples.Y
        );
    }
}

/// <summary>
///     Scatter symbols at each datum. <see cref="SizeBy" /> turns it into a bubble chart: symbol
///     area scales with the selected value (√-scaled so area, not diameter, encodes magnitude).
/// </summary>
public class PointMark<T>(IReadOnlyList<T> data, Func<T, ChartValue> x, Func<T, ChartValue> y)
    : SeriesMark<T>(data: data, x: x, y: y)
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
        var ys = domain.Y(sample: Resolved[0].Y, secondary: UseSecondaryYAxis);
        foreach (var p in Resolved)
        {
            xs.Include(p.X);
            ys.Include(p.Y);
        }

        _maxSizeValue = 0;
        if (SizeBy is not null)
        {
            foreach (var d in Data)
                _maxSizeValue = Math.Max(val1: _maxSizeValue, val2: Math.Abs(SizeBy(d)));
        }
    }

    public override void CollectInteractive(ChartRenderContext ctx)
    {
        foreach (var p in Resolved)
        {
            ctx.HoverPoints.Add(
                new ChartDataPoint(
                    screenX: ctx.MapX(p.X),
                    screenY: ctx.MapY(p.Y),
                    x: p.X,
                    y: p.Y,
                    series: p.Series,
                    valueLabel: FormatValue(p.Y),
                    color: ctx.ColorFor(series: p.Series, markOverride: Color, markIndex: MarkIndex)
                )
            );
        }
    }

    public override void Paint(ChartRenderContext ctx)
    {
        var paint = ctx.Paint;
        if (paint is null) return;

        // Entrance animation: symbols fade + scale in.
        float alpha = Opacity * ctx.Progress;
        if (alpha <= 0.001f) return;

        for (int i = 0; i < Resolved.Count; i++)
        {
            var p = Resolved[i];
            float cx = ctx.MapX(p.X);
            float cy = p.Y.Kind == ChartValueKind.Category
                ? ctx.MapY(p.Y)
                : ctx.MapYNumeric(MorphedY(ctx: ctx, p: p));
            float d = Diameter(i) * ctx.Progress;
            if (d < 0.5f) continue;
            float r = d / 2f;
            var color = ctx.ColorFor(series: p.Series, markOverride: Color, markIndex: MarkIndex)
                .WithAlpha(alpha);
            var rect = new Rect(
                x: cx - r,
                y: cy - r,
                width: d,
                height: d
            );
            switch (Symbol)
            {
                case ChartSymbol.Square:
                    paint.AddRect(bounds: rect, color: color, radius: 1.5f);
                    break;
                case ChartSymbol.Ring:
                    paint.AddBorder(
                        bounds: rect,
                        color: color,
                        radius: r,
                        width: 2f
                    );
                    break;
                case ChartSymbol.Triangle:
                    // Upward equilateral triangle, filled via the native polygon primitive.
                    const float sin60 = 0.8660254f;
                    paint.AddPolygon(
                        points: [
                            new Offset(x: cx, y: cy - r),
                            new Offset(x: cx + (r * sin60), y: cy + (r * 0.5f)),
                            new Offset(x: cx - (r * sin60), y: cy + (r * 0.5f)),
                        ],
                        color: color
                    );
                    break;
                case ChartSymbol.Diamond:
                    paint.AddPolygon(
                        points: [
                            new Offset(x: cx, y: cy - r),
                            new Offset(x: cx + r, y: cy),
                            new Offset(x: cx, y: cy + r),
                            new Offset(x: cx - r, y: cy),
                        ],
                        color: color
                    );
                    break;
                default:
                    paint.AddRect(bounds: rect, color: color, radius: r);
                    break;
            }
        }
    }

    private float Diameter(int index)
    {
        if (SizeBy is null || _maxSizeValue <= 0) return Size;
        double v = Math.Abs(SizeBy(Data[index]));
        return MathF.Max(x: 2f, y: MaxSize * MathF.Sqrt((float)(v / _maxSizeValue)));
    }
}
