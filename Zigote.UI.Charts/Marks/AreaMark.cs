using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Charts.Rendering;

namespace Zigote.UI.Charts.Marks;

public static class AreaMark
{
    public static AreaMark<T> Of<T>(IReadOnlyList<T> data, Func<T, ChartValue> x,
        Func<T, ChartValue> y, Func<T, string>? series = null) =>
        new(data: data, x: x, y: y) { SeriesBy = series };

    /// <summary>Vectorized: fill ys against their indices (0, 1, 2, …) — no row type needed.</summary>
    public static AreaMark<ChartSample> Of(ReadOnlySpan<double> ys)
    {
        return new AreaMark<ChartSample>(
            data: ChartSamples.Pair(xs: default, ys: ys),
            x: ChartSamples.X,
            y: ChartSamples.Y
        );
    }

    /// <summary>Vectorized: fill paired xs/ys arrays directly.</summary>
    public static AreaMark<ChartSample> Of(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        return new AreaMark<ChartSample>(
            data: ChartSamples.Pair(xs: xs, ys: ys),
            x: ChartSamples.X,
            y: ChartSamples.Y
        );
    }
}

/// <summary>
///     Filled region between the series curve and the zero baseline (or the stack below it —
///     multi-series areas stack by default). The fill edge follows the exact interpolation a
///     <see cref="LineMark{T}" /> would stroke, so composing both marks lines up pixel-perfectly.
///     <para>
///         The renderer has no filled-path primitive, so the fill rasterises as vertical strips of
///         <see cref="FillResolution" /> px sampled from the interpolated edges.
///     </para>
/// </summary>
public class AreaMark<T>(IReadOnlyList<T> data, Func<T, ChartValue> x, Func<T, ChartValue> y)
    : SeriesMark<T>(data: data, x: x, y: y)
{
    // Cached x-sort order per series (stable per resolve) + reusable projection/slope scratch, so
    // the per-frame fill path re-sorts nothing and allocates nothing steady-state.
    private readonly Dictionary<string, int[]> _order = new();
    private readonly StackScratch _stackScratch = new();

    // Reused stacking-input scratch (StackCompute does not retain it) + the pooled column maps.
    private readonly List<(string Series, ChartValue X, double Value)> _triples = [];
    private ChartStacking _effectiveMode;

    private int _orderVersion = -1;

    // Spans key on the raw ChartValue (value equality) — no per-point key strings — and the two
    // maps double-buffer across morph epochs so a re-resolve reuses them instead of reallocating.
    private Dictionary<(string Series, ChartValue X), StackedSpan>? _prevSpans;
    private Dictionary<(string Series, ChartValue X), StackedSpan> _spans = new();
    private float[] _xs = [], _topY = [], _botY = [], _topSlopes = [], _botSlopes = [];

    public ChartInterpolation Interpolation { get; set; } = ChartInterpolation.Monotone;
    public ChartStacking Stacking { get; set; } = ChartStacking.Standard;

    /// <summary>Fill alpha (the series color at 30% reads as a soft default area style).</summary>
    public float Opacity { get; set; } = 0.3f;

    /// <summary>Stroke the top edge with the solid series color.</summary>
    public bool StrokeTop { get; set; } = true;

    public float StrokeWidth { get; set; } = 2f;

    /// <summary>Width in px of one fill strip (smaller = smoother edge, more commands).</summary>
    public float FillResolution { get; set; } = 2f;

    /// <summary>
    ///     Fill with the native polygon primitive (seam-free translucent trapezoids that follow the
    ///     curve exactly) instead of the default vertical rect strips. Strictly nicer for a
    ///     translucent single-series fill; costs a small per-segment allocation, so the default stays
    ///     the zero-alloc strip path. Stacked fills that must read cleanly through each other benefit
    ///     most.
    /// </summary>
    public bool UsePolygonFill { get; set; }

    public override void IncludeDomain(ChartDomain domain)
    {
        bool snapshot = EpochChanged(domain);
        if (snapshot && _spans.Count > 0)
            // Morph source for animated updates — swap buffers so the displaced prev map is reused.
        {
            (_prevSpans, _spans) = (_spans,
                _prevSpans ?? new Dictionary<(string, ChartValue), StackedSpan>());
        }

        ResolveData(snapshot);
        if (Resolved.Count == 0) return;

        var xs = domain.X(Resolved[0].X);
        var ys = domain.Y(sample: Resolved[0].Y, secondary: UseSecondaryYAxis);
        ChartDomain.RequestZeroBaseline(ys);

        foreach (var p in Resolved) xs.Include(p.X);

        _effectiveMode = SeriesOrder.Count > 1 ? Stacking : ChartStacking.None;
        _triples.Clear();
        foreach (var p in Resolved)
            _triples.Add((p.Series, p.X, p.Y.Numeric));
        StackCompute.Compute(
            points: _triples,
            seriesOrder: SeriesOrder,
            mode: _effectiveMode,
            result: _spans,
            scratch: _stackScratch
        );

        foreach (var span in _spans.Values)
        {
            ys.IncludeNumeric(span.Bottom);
            ys.IncludeNumeric(span.Top);
        }
    }

    public override void CollectInteractive(ChartRenderContext ctx)
    {
        foreach (var p in Resolved)
        {
            double top = _spans.TryGetValue(key: (p.Series, p.X), value: out var span)
                ? span.Top
                : p.Y.Numeric;
            ctx.HoverPoints.Add(
                new ChartDataPoint(
                    screenX: ctx.MapX(p.X),
                    screenY: ctx.MapYNumeric(top),
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

        // Paint series back-to-front in series order so stacked layers overlay predictably.
        // Indexed loop: foreach over the IReadOnlyList prop boxes an enumerator per paint.
        var groups = GroupBySeries();
        var seriesOrder = SeriesOrder;
        for (int i = 0; i < seriesOrder.Count; i++)
        {
            string series = seriesOrder[i];
            if (!groups.TryGetValue(key: series, value: out var points) || points.Count < 2)
                continue;
            var color = ctx.ColorFor(series: series, markOverride: Color, markIndex: MarkIndex);
            PaintSeries(
                ctx: ctx,
                paint: paint,
                series: series,
                points: points,
                color: color
            );
        }

        if (reveal) paint.AddClipEnd();
    }

    private int[] OrderFor(string series, List<ResolvedPoint> points)
    {
        if (_orderVersion != ResolveVersion)
        {
            _order.Clear();
            _orderVersion = ResolveVersion;
        }

        // The build lives in its own method: its points-capturing sort lambda would otherwise hoist
        // a closure allocation into THIS method's entry — paid on every cache-hit paint.
        return _order.TryGetValue(key: series, value: out int[]? cached)
            ? cached
            : BuildOrder(series: series, points: points);
    }

    private int[] BuildOrder(string series, List<ResolvedPoint> points)
    {
        int n = points.Count;
        int[] idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        Array.Sort(
            array: idx,
            comparison: (a, b) => points[a].X.Numeric.CompareTo(points[b].X.Numeric)
        );
        _order[series] = idx;
        return idx;
    }

    private void PaintSeries(ChartRenderContext ctx, PaintList paint, string series,
        List<ResolvedPoint> points, Color color)
    {
        int[] order = OrderFor(series: series, points: points);
        int n = order.Length;
        if (n < 2) return;
        if (_xs.Length < n)
        {
            _xs = new float[n];
            _topY = new float[n];
            _botY = new float[n];
            _topSlopes = new float[n];
            _botSlopes = new float[n];
        }

        // Project the top/bottom edges directly in x-sorted order (morph-aware) into reused buffers.
        for (int k = 0; k < n; k++)
        {
            var p = points[order[k]];
            var key = p.X;
            var span = _spans.TryGetValue(key: (series, key), value: out var s)
                ? s
                : new StackedSpan(Bottom: 0, Top: p.Y.Numeric);
            double top = span.Top;
            double bottom = span.Bottom;
            if (ctx.DataProgress < 1f)
            {
                var old = _prevSpans is not null && _prevSpans.TryGetValue(
                    key: (series, key),
                    value: out var o
                )
                    ? o
                    : new StackedSpan(Bottom: 0, Top: 0);
                top = old.Top + ((top - old.Top) * ctx.DataProgress);
                bottom = old.Bottom + ((bottom - old.Bottom) * ctx.DataProgress);
            }

            _xs[k] = ctx.MapX(p.X);
            _topY[k] = ctx.MapYNumeric(top);
            _botY[k] = ctx.MapYNumeric(bottom);
        }

        var xs = _xs.AsSpan(start: 0, length: n);
        var topY = _topY.AsSpan(start: 0, length: n);
        var botY = _botY.AsSpan(start: 0, length: n);
        var topSlopes = ReadOnlySpan<float>.Empty;
        var botSlopes = ReadOnlySpan<float>.Empty;
        if (Interpolation == ChartInterpolation.Monotone)
        {
            ChartGeometry.MonotoneSlopes(
                xs: xs,
                ys: topY,
                m: _topSlopes.AsSpan(start: 0, length: n)
            );
            ChartGeometry.MonotoneSlopes(
                xs: xs,
                ys: botY,
                m: _botSlopes.AsSpan(start: 0, length: n)
            );
            topSlopes = _topSlopes.AsSpan(start: 0, length: n);
            botSlopes = _botSlopes.AsSpan(start: 0, length: n);
        }

        var fill = color.WithAlpha(color.A * Opacity);
        float x0 = xs[0];
        float x1 = xs[n - 1];
        float step = MathF.Max(x: 1f, y: FillResolution);

        if (UsePolygonFill)
        {
            // Seam-free trapezoids following the exact curve — one convex quad per step.
            Span<Offset> quad = stackalloc Offset[4];
            float prevX = x0;
            float prevTop = Sample(
                xs: xs,
                ys: topY,
                slopes: topSlopes,
                x: x0
            );
            float prevBot = Sample(
                xs: xs,
                ys: botY,
                slopes: botSlopes,
                x: x0
            );
            for (float sx = x0 + step; sx <= x1 + 0.001f; sx += step)
            {
                float cx = MathF.Min(x: sx, y: x1);
                float t = Sample(
                    xs: xs,
                    ys: topY,
                    slopes: topSlopes,
                    x: cx
                );
                float b = Sample(
                    xs: xs,
                    ys: botY,
                    slopes: botSlopes,
                    x: cx
                );
                quad[0] = new Offset(x: prevX, y: prevTop);
                quad[1] = new Offset(x: cx, y: t);
                quad[2] = new Offset(x: cx, y: b);
                quad[3] = new Offset(x: prevX, y: prevBot);
                paint.AddPolygon(points: quad, color: fill);
                prevX = cx;
                prevTop = t;
                prevBot = b;
            }
        }
        else
        {
            // Vertical strip fill between the two interpolated edges (zero-alloc default).
            for (float sx = x0; sx < x1; sx += step)
            {
                float sw = MathF.Min(x: step, y: x1 - sx);
                float mid = sx + (sw / 2f);
                float yTop = Sample(
                    xs: xs,
                    ys: topY,
                    slopes: topSlopes,
                    x: mid
                );
                float yBot = Sample(
                    xs: xs,
                    ys: botY,
                    slopes: botSlopes,
                    x: mid
                );
                if (yBot < yTop) (yTop, yBot) = (yBot, yTop);
                if (yBot - yTop < 0.01f) continue;
                paint.AddRect(
                    bounds: new Rect(
                        x: sx,
                        y: yTop,
                        width: sw,
                        height: yBot - yTop
                    ),
                    color: fill
                );
            }
        }

        if (StrokeTop)
        {
            LineMark<T>.StrokePolyline(
                ctx: ctx,
                sx: xs,
                sy: topY,
                color: color,
                width: StrokeWidth,
                interpolation: Interpolation
            );
        }
    }

    /// <summary>Sample an edge at <paramref name="x" /> per the mark's interpolation (closure-free).</summary>
    private float Sample(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, ReadOnlySpan<float> slopes,
        float x)
    {
        return Interpolation == ChartInterpolation.Step
            ? StepAt(xs: xs, ys: ys, x: x)
            : Interpolation == ChartInterpolation.Monotone
                ? ChartGeometry.EvaluateMonotone(
                    xs: xs,
                    ys: ys,
                    slopes: slopes,
                    x: x
                )
                : ChartGeometry.EvaluateLinear(xs: xs, ys: ys, x: x);
    }

    private static float StepAt(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, float x)
    {
        // Hold each sample's value until the next one (matches the Step stroke).
        float y = ys[0];
        for (int i = 0; i < xs.Length; i++)
        {
            if (xs[i] > x) break;
            y = ys[i];
        }

        return y;
    }
}
