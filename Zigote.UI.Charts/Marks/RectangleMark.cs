using Zigote.Core;
using Zigote.UI.Charts.Rendering;
using Zigote.UI.Charts.Scales;

namespace Zigote.UI.Charts.Marks;

public static class RectangleMark
{
    public static RectangleMark<T> Of<T>(IReadOnlyList<T> data, Func<T, ChartValue> x,
        Func<T, ChartValue> y) =>
        new(data: data, x: x, y: y);
}

/// <summary>
///     One cell per datum at (x, y) — heatmaps when both axes are categorical and
///     <see cref="FillBy" /> maps a magnitude onto the low→high color ramp.
/// </summary>
public class RectangleMark<T>(IReadOnlyList<T> data, Func<T, ChartValue> x, Func<T, ChartValue> y)
    : SeriesMark<T>(data: data, x: x, y: y)
{
    private double _fillMax = 1;
    private double _fillMin;

    /// <summary>Per-datum magnitude mapped onto <see cref="LowColor" />→<see cref="HighColor" />.</summary>
    public Func<T, double>? FillBy { get; set; }

    public Color? LowColor { get; set; }
    public Color? HighColor { get; set; }

    /// <summary>Gap between adjacent cells in px.</summary>
    public float Inset { get; set; } = 1f;

    public float CornerRadius { get; set; } = 2f;

    /// <summary>Cell size in px along a continuous axis (band axes size cells from the band).</summary>
    public float FixedCellSize { get; set; } = 16f;

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

        _fillMin = double.PositiveInfinity;
        _fillMax = double.NegativeInfinity;
        if (FillBy is not null)
        {
            foreach (var d in Data)
            {
                double v = FillBy(d);
                if (v < _fillMin) _fillMin = v;
                if (v > _fillMax) _fillMax = v;
            }
        }

        if (double.IsInfinity(_fillMin)) (_fillMin, _fillMax) = (0, 1);
        if (_fillMax <= _fillMin) _fillMax = _fillMin + 1;
    }

    public override void CollectInteractive(ChartRenderContext ctx)
    {
        for (int i = 0; i < Resolved.Count; i++)
        {
            var p = Resolved[i];
            string label = FillBy is not null
                ? NiceScale.FormatNumber(FillBy(Data[i]))
                : FormatValue(p.Y);
            ctx.HoverPoints.Add(
                new ChartDataPoint(
                    screenX: ctx.MapX(p.X),
                    screenY: ctx.MapY(p.Y),
                    x: p.X,
                    y: p.Y,
                    series: p.Series,
                    valueLabel: label,
                    color: CellColor(ctx: ctx, index: i)
                )
            );
        }
    }

    public override void Paint(ChartRenderContext ctx)
    {
        var paint = ctx.Paint;
        if (paint is null) return;

        float alpha = ctx.Progress;
        float cellW = ctx.XScale.IsBand
            ? ctx.XScale.NormalizedBandWidth * ctx.PlotRect.Width
            : FixedCellSize;
        float cellH = ctx.YScale.IsBand
            ? ctx.YScale.NormalizedBandWidth * ctx.PlotRect.Height
            : FixedCellSize;

        for (int i = 0; i < Resolved.Count; i++)
        {
            var p = Resolved[i];
            float cx = ctx.MapX(p.X);
            float cy = ctx.MapY(p.Y);
            var rect = new Rect(
                x: cx - (cellW / 2f) + Inset,
                y: cy - (cellH / 2f) + Inset,
                width: MathF.Max(x: 0.5f, y: cellW - (Inset * 2)),
                height: MathF.Max(x: 0.5f, y: cellH - (Inset * 2))
            );
            var color = CellColor(ctx: ctx, index: i);
            paint.AddRect(
                bounds: rect,
                color: color.WithAlpha(color.A * alpha),
                radius: CornerRadius
            );
        }
    }

    private Color CellColor(ChartRenderContext ctx, int index)
    {
        var baseColor = ctx.ColorFor(
            series: Resolved[index].Series,
            markOverride: Color,
            markIndex: MarkIndex
        );
        if (FillBy is null) return baseColor;

        float t = (float)((FillBy(Data[index]) - _fillMin) / (_fillMax - _fillMin));
        if (!float.IsFinite(t))
            t = 0f; // NaN magnitude or a zero-span ramp → fall back to the low colour
        t = Math.Clamp(value: t, min: 0f, max: 1f);
        var low = LowColor ?? baseColor.WithAlpha(0.15f);
        var high = HighColor ?? baseColor;
        return new Color(
            r: low.R + ((high.R - low.R) * t),
            g: low.G + ((high.G - low.G) * t),
            b: low.B + ((high.B - low.B) * t),
            a: low.A + ((high.A - low.A) * t)
        );
    }
}
