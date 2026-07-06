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
        Func<T, ChartValue> y,
        Func<T, string>? series = null)
    {
        return new LineMark<T>(data, x, y) { SeriesBy = series };
    }

    /// <summary>Vectorized: plot ys against their indices (0, 1, 2, …) — no row type needed.</summary>
    public static LineMark<ChartSample> Of(ReadOnlySpan<double> ys)
    {
        return new LineMark<ChartSample>(
            ChartSamples.Pair(default, ys),
            ChartSamples.X,
            ChartSamples.Y
        );
    }

    /// <summary>Vectorized: plot paired xs/ys arrays directly.</summary>
    public static LineMark<ChartSample> Of(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        return new LineMark<ChartSample>(
            ChartSamples.Pair(xs, ys),
            ChartSamples.X,
            ChartSamples.Y
        );
    }
}

/// <summary>
///     A polyline/curve per series. Compose with <see cref="PointMark{T}" /> for symbols or set
///     <see cref="ShowSymbols" /> for the built-in dots.
/// </summary>
public class LineMark<T>(IReadOnlyList<T> data, Func<T, ChartValue> x, Func<T, ChartValue> y)
    : SeriesMark<T>(data, x, y)
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
        var ys = domain.Y(Resolved[0].Y, UseSecondaryYAxis);
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
        foreach (var (series, points) in GroupBySeries())
        {
            if (points.Count == 0) continue;
            var order = RenderOrder(series, points);
            var color = ctx.ColorFor(series, Color, MarkIndex);
            foreach (var i in order)
            {
                var p = points[i];
                ctx.HoverPoints.Add(
                    new ChartDataPoint(
                        ctx.MapX(p.X),
                        ctx.MapY(p.Y),
                        p.X,
                        p.Y,
                        p.Series,
                        FormatValue(p.Y),
                        color
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
        return _renderOrder.TryGetValue(series, out var cached)
            ? cached
            : BuildRenderOrder(series, pts);
    }

    private int[] BuildRenderOrder(string series, List<ResolvedPoint> pts)
    {
        var n = pts.Count;
        var idx = new int[n];
        for (var i = 0; i < n; i++) idx[i] = i;
        if (SortByX) Array.Sort(idx, (a, b) => pts[a].X.Numeric.CompareTo(pts[b].X.Numeric));

        if (MaxRenderPoints > 2 && n > MaxRenderPoints)
        {
            // Decimate on the data values (x normalized so a large time-axis base keeps float precision).
            var xs = new float[n];
            var ys = new float[n];
            var x0 = pts[idx[0]].X.Numeric;
            for (var i = 0; i < n; i++)
            {
                var p = pts[idx[i]];
                xs[i] = (float)(p.X.Numeric - x0);
                ys[i] = (float)p.Y.Numeric;
            }

            var keep = ChartGeometry.LttbIndices(xs, ys, MaxRenderPoints);
            if (keep is not null)
            {
                var survivors = new int[keep.Length];
                for (var k = 0; k < keep.Length; k++) survivors[k] = idx[keep[k]];
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
        var reveal = ctx.Progress < 1f;
        if (reveal)
            paint.AddClipStart(
                new Rect(
                    ctx.PlotRect.X,
                    ctx.PlotRect.Y - StrokeWidth,
                    MathF.Max(0.01f, ctx.PlotRect.Width * ctx.Progress),
                    ctx.PlotRect.Height + StrokeWidth * 2
                )
            );

        foreach (var (series, points) in GroupBySeries())
        {
            if (points.Count == 0) continue;
            var color = ctx.ColorFor(series, Color, MarkIndex);
            var count = ProjectOrder(ctx, points, RenderOrder(series, points));
            var sx = _sx.AsSpan(0, count);
            var sy = _sy.AsSpan(0, count);
            StrokePolyline(
                ctx,
                sx,
                sy,
                color,
                StrokeWidth,
                Interpolation,
                Dash,
                DashGap
            );

            if (ShowSymbols)
            {
                var r = SymbolSize / 2f;
                for (var i = 0; i < count; i++)
                    paint.AddRect(
                        new Rect(
                            sx[i] - r,
                            sy[i] - r,
                            SymbolSize,
                            SymbolSize
                        ),
                        color,
                        r
                    );
            }
        }

        if (reveal) paint.AddClipEnd();
    }

    /// <summary>Project a series' render-order points into the reused scratch; returns the count.</summary>
    private int ProjectOrder(ChartRenderContext ctx, List<ResolvedPoint> points, int[] order)
    {
        var count = order.Length;
        if (_sx.Length < count)
        {
            _sx = new float[count];
            _sy = new float[count];
        }

        for (var k = 0; k < count; k++)
        {
            var p = points[order[k]];
            _sx[k] = ctx.MapX(p.X);
            _sy[k] = p.Y.Kind == ChartValueKind.Category
                ? ctx.MapY(p.Y)
                : ctx.MapYNumeric(MorphedY(ctx, p));
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
                var slopes = ChartGeometry.MonotoneSlopesScratch(sx, sy);
                // Stroke each cubic directly — no per-frame List of segments.
                for (var i = 0; i < sx.Length - 1; i++)
                {
                    var seg = ChartGeometry.HermiteToCubic(
                        sx[i],
                        sy[i],
                        slopes[i],
                        sx[i + 1],
                        sy[i + 1],
                        slopes[i + 1]
                    );
                    ctx.StrokeCubic(seg, color, width);
                }

                break;
            }
            case ChartInterpolation.Step:
            {
                for (var i = 0; i < sx.Length - 1; i++)
                {
                    ctx.StrokeLine(
                        sx[i],
                        sy[i],
                        sx[i + 1],
                        sy[i],
                        color,
                        width,
                        dash,
                        dashGap
                    );
                    ctx.StrokeLine(
                        sx[i + 1],
                        sy[i],
                        sx[i + 1],
                        sy[i + 1],
                        color,
                        width,
                        dash,
                        dashGap
                    );
                }

                break;
            }
            default:
            {
                // Linear — and the dashed fallback for Monotone (dashes need straight spans).
                for (var i = 0; i < sx.Length - 1; i++)
                    ctx.StrokeLine(
                        sx[i],
                        sy[i],
                        sx[i + 1],
                        sy[i + 1],
                        color,
                        width,
                        dash,
                        dashGap
                    );
                break;
            }
        }
    }
}