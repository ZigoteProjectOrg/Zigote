using Zigote.Core;
using Zigote.UI.Charts.Rendering;

namespace Zigote.UI.Charts.Marks;

/// <summary>How consecutive samples connect.</summary>
public enum ChartInterpolation : byte
{
    Linear,

    /// <summary>Monotone cubic (Fritsch–Carlson): smooth, never overshoots the data.</summary>
    Monotone,

    /// <summary>Staircase: hold each value until the next sample.</summary>
    Step,
}

public static class LineMark
{
    public static LineMark<T> Of<T>(IReadOnlyList<T> data, Func<T, ChartValue> x,
        Func<T, ChartValue> y, Func<T, string>? series = null) =>
        new(data: data, x: x, y: y) { SeriesBy = series };

    /// <summary>Vectorized: plot ys against their indices (0, 1, 2, …) — no row type needed.</summary>
    public static LineMark<ChartSample> Of(ReadOnlySpan<double> ys)
    {
        return new LineMark<ChartSample>(
            data: ChartSamples.Pair(xs: default, ys: ys),
            x: ChartSamples.X,
            y: ChartSamples.Y
        );
    }

    /// <summary>Vectorized: plot paired xs/ys arrays directly.</summary>
    public static LineMark<ChartSample> Of(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        return new LineMark<ChartSample>(
            data: ChartSamples.Pair(xs: xs, ys: ys),
            x: ChartSamples.X,
            y: ChartSamples.Y
        );
    }
}

/// <summary>
///     A polyline/curve per series. Compose with <see cref="PointMark{T}" /> for symbols or set
///     <see cref="ShowSymbols" /> for the built-in dots.
/// </summary>
public class LineMark<T>(IReadOnlyList<T> data, Func<T, ChartValue> x, Func<T, ChartValue> y)
    : SeriesMark<T>(data: data, x: x, y: y)
{
    // Per-series x-sorted (and LTTB-decimated) index arrays into the series point list, rebuilt only
    // when the data resolve version or the cap changes — never per paint.
    private readonly Dictionary<string, int[]> _renderOrder = new();
    private int _renderMax = -1;
    private int _renderVersion = -1;

    // Reusable projection scratch, grown on demand — the per-paint path allocates nothing steady-state.
    private float[] _sx = [];
    private float[] _sy = [];
    public ChartInterpolation Interpolation { get; set; } = ChartInterpolation.Monotone;
    public float StrokeWidth { get; set; } = 2f;

    /// <summary>Dash length in px; 0 = solid.</summary>
    public float Dash { get; set; }

    public float DashGap { get; set; } = 4f;

    /// <summary>Draw a small filled circle at every sample.</summary>
    public bool ShowSymbols { get; set; }

    public float SymbolSize { get; set; } = 7f;

    /// <summary>Sort each series by x before stroking (off = connect in data order).</summary>
    public bool SortByX { get; set; } = true;

    /// <summary>
    ///     Cap the rendered vertices per series via Largest-Triangle-Three-Buckets decimation, which
    ///     preserves the visual shape while cutting the paint/hover cost of huge series. 0 = no cap.
    ///     Decimation runs once per data resolve (in data space, cached), so a capped series projects
    ///     only its survivors each frame — the hover registry uses the same reduced set.
    /// </summary>
    public int MaxRenderPoints { get; set; }

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
    }

    public override void CollectInteractive(ChartRenderContext ctx)
    {
        // Register the drawn (decimated) points only — hover resolution matches the stroke and
        // ResolveHover stays O(rendered), not O(raw), for million-point series.
        foreach ((string series, var points) in GroupBySeries())
        {
            if (points.Count == 0) continue;
            int[] order = RenderOrder(series: series, pts: points);
            var color = ctx.ColorFor(series: series, markOverride: Color, markIndex: MarkIndex);
            foreach (int i in order)
            {
                var p = points[i];
                ctx.HoverPoints.Add(
                    new ChartDataPoint(
                        screenX: ctx.MapX(p.X),
                        screenY: ctx.MapY(p.Y),
                        x: p.X,
                        y: p.Y,
                        series: p.Series,
                        valueLabel: FormatValue(p.Y),
                        color: color
                    )
                );
            }
        }
    }

    /// <summary>x-sorted (and, past the cap, LTTB-decimated in data space) point indices for a series.</summary>
    private int[] RenderOrder(string series, List<ResolvedPoint> pts)
    {
        if (_renderVersion != ResolveVersion || _renderMax != MaxRenderPoints)
        {
            _renderOrder.Clear();
            _renderVersion = ResolveVersion;
            _renderMax = MaxRenderPoints;
        }

        // The build lives in its own method: its pts-capturing sort lambda would otherwise hoist a
        // closure allocation into THIS method's entry — paid on every cache-hit paint.
        return _renderOrder.TryGetValue(key: series, value: out int[]? cached)
            ? cached
            : BuildRenderOrder(series: series, pts: pts);
    }

    private int[] BuildRenderOrder(string series, List<ResolvedPoint> pts)
    {
        int n = pts.Count;
        int[] idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        if (SortByX)
        {
            Array.Sort(
                array: idx,
                comparison: (a, b) => pts[a].X.Numeric.CompareTo(pts[b].X.Numeric)
            );
        }

        if (MaxRenderPoints > 2 && n > MaxRenderPoints)
        {
            // Decimate on the data values (x normalized so a large time-axis base keeps float precision).
            float[] xs = new float[n];
            float[] ys = new float[n];
            double x0 = pts[idx[0]].X.Numeric;
            for (int i = 0; i < n; i++)
            {
                var p = pts[idx[i]];
                xs[i] = (float)(p.X.Numeric - x0);
                ys[i] = (float)p.Y.Numeric;
            }

            int[]? keep = ChartGeometry.LttbIndices(xs: xs, ys: ys, threshold: MaxRenderPoints);
            if (keep is not null)
            {
                int[] survivors = new int[keep.Length];
                for (int k = 0; k < keep.Length; k++) survivors[k] = idx[keep[k]];
                idx = survivors;
            }
        }

        _renderOrder[series] = idx;
        return idx;
    }

    public override void Paint(ChartRenderContext ctx)
    {
        var paint = ctx.Paint;
        if (paint is null) return;

        // Entrance animation: reveal the plot left→right.
        bool reveal = ctx.Progress < 1f;
        if (reveal)
        {
            paint.AddClipStart(
                new Rect(
                    x: ctx.PlotRect.X,
                    y: ctx.PlotRect.Y - StrokeWidth,
                    width: MathF.Max(x: 0.01f, y: ctx.PlotRect.Width * ctx.Progress),
                    height: ctx.PlotRect.Height + (StrokeWidth * 2)
                )
            );
        }

        foreach ((string series, var points) in GroupBySeries())
        {
            if (points.Count == 0) continue;
            var color = ctx.ColorFor(series: series, markOverride: Color, markIndex: MarkIndex);
            int count = ProjectOrder(
                ctx: ctx,
                points: points,
                order: RenderOrder(series: series, pts: points)
            );
            var sx = _sx.AsSpan(start: 0, length: count);
            var sy = _sy.AsSpan(start: 0, length: count);
            StrokePolyline(
                ctx: ctx,
                sx: sx,
                sy: sy,
                color: color,
                width: StrokeWidth,
                interpolation: Interpolation,
                dash: Dash,
                dashGap: DashGap
            );

            if (ShowSymbols)
            {
                float r = SymbolSize / 2f;
                for (int i = 0; i < count; i++)
                {
                    paint.AddRect(
                        bounds: new Rect(
                            x: sx[i] - r,
                            y: sy[i] - r,
                            width: SymbolSize,
                            height: SymbolSize
                        ),
                        color: color,
                        radius: r
                    );
                }
            }
        }

        if (reveal) paint.AddClipEnd();
    }

    /// <summary>Project a series' render-order points into the reused scratch; returns the count.</summary>
    private int ProjectOrder(ChartRenderContext ctx, List<ResolvedPoint> points, int[] order)
    {
        int count = order.Length;
        if (_sx.Length < count)
        {
            _sx = new float[count];
            _sy = new float[count];
        }

        for (int k = 0; k < count; k++)
        {
            var p = points[order[k]];
            _sx[k] = ctx.MapX(p.X);
            _sy[k] = p.Y.Kind == ChartValueKind.Category
                ? ctx.MapY(p.Y)
                : ctx.MapYNumeric(MorphedY(ctx: ctx, p: p));
        }

        return count;
    }

    /// <summary>Stroke a projected polyline with the chosen interpolation. Shared with AreaMark.</summary>
    internal static void StrokePolyline(ChartRenderContext ctx, ReadOnlySpan<float> sx,
        ReadOnlySpan<float> sy,
        Color color, float width, ChartInterpolation interpolation, float dash = 0f,
        float dashGap = 4f)
    {
        if (sx.Length < 2) return;

        switch (interpolation)
        {
            case ChartInterpolation.Monotone when dash <= 0f:
            {
                var slopes = ChartGeometry.MonotoneSlopesScratch(xs: sx, ys: sy);
                // Stroke each cubic directly — no per-frame List of segments.
                for (int i = 0; i < sx.Length - 1; i++)
                {
                    var seg = ChartGeometry.HermiteToCubic(
                        x0: sx[i],
                        y0: sy[i],
                        m0: slopes[i],
                        x1: sx[i + 1],
                        y1: sy[i + 1],
                        m1: slopes[i + 1]
                    );
                    ctx.StrokeCubic(s: seg, color: color, width: width);
                }

                break;
            }
            case ChartInterpolation.Step:
            {
                for (int i = 0; i < sx.Length - 1; i++)
                {
                    ctx.StrokeLine(
                        x0: sx[i],
                        y0: sy[i],
                        x1: sx[i + 1],
                        y1: sy[i],
                        color: color,
                        width: width,
                        dash: dash,
                        gap: dashGap
                    );
                    ctx.StrokeLine(
                        x0: sx[i + 1],
                        y0: sy[i],
                        x1: sx[i + 1],
                        y1: sy[i + 1],
                        color: color,
                        width: width,
                        dash: dash,
                        gap: dashGap
                    );
                }

                break;
            }
            default:
            {
                // Linear — and the dashed fallback for Monotone (dashes need straight spans).
                for (int i = 0; i < sx.Length - 1; i++)
                {
                    ctx.StrokeLine(
                        x0: sx[i],
                        y0: sy[i],
                        x1: sx[i + 1],
                        y1: sy[i + 1],
                        color: color,
                        width: width,
                        dash: dash,
                        gap: dashGap
                    );
                }

                break;
            }
        }
    }
}
