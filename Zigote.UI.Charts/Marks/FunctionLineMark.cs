using Zigote.Core;
using Zigote.UI.Charts.Rendering;
using Zigote.UI.Charts.Scales;

namespace Zigote.UI.Charts.Marks;

/// <summary>
///     Plots <c>y = f(x)</c> sampled per pixel column across the visible x window — the LinePlot
///     analogue. The curve tracks scroll and zoom automatically: samples regenerate from the windowed
///     scale whenever the plot rect or visible window changes, into reused scratch, so the
///     steady-state paint allocates nothing. NaN / infinite samples (and x outside
///     [<see cref="XMin" />, <see cref="XMax" />]) break the stroke into segments, so functions with
///     poles (1/x, tan) render correctly. Composes with data marks like any other mark.
///     <code>
///     new FunctionLineMark(x => Math.Sin(x), -10, 10) { Name = "sin" }
///     </code>
/// </summary>
public sealed class FunctionLineMark : ChartMark
{
    /// <summary>Coarse fixed sampling for the y-domain estimate (plot width unknown at domain time).</summary>
    private const int DomainSamples = 128;

    private int _count;
    private Rect _sampledPlot;
    private double _sampledX0 = double.NaN, _sampledX1 = double.NaN;
    private double _sampledY0 = double.NaN, _sampledY1 = double.NaN;

    // Reused projection scratch, grown on demand — the per-paint path allocates nothing steady-state.
    private float[] _sx = [];
    private float[] _sy = [];

    public FunctionLineMark(Func<double, double> function, double xMin, double xMax)
    {
        Function = function;
        XMin = xMin;
        XMax = xMax;
    }

    public Func<double, double> Function { get; set; }

    /// <summary>Function domain: the mark feeds this x extent into the shared scale.</summary>
    public double XMin { get; set; }

    public double XMax { get; set; }

    public float StrokeWidth { get; set; } = 2f;

    /// <summary>Dash length in px; 0 = solid.</summary>
    public float Dash { get; set; }

    public float DashGap { get; set; } = 4f;

    /// <summary>Approximate px between samples (smaller = smoother, more paint commands).</summary>
    public float PixelsPerSample { get; set; } = 2f;

    /// <summary>Hard cap on samples per paint regardless of plot width.</summary>
    public int MaxSamples { get; set; } = 2048;

    public override void IncludeDomain(ChartDomain domain)
    {
        if (XMax <= XMin || Function is null) return;

        var xs = domain.X(ChartValue.Number(XMin));
        xs.IncludeNumeric(XMin);
        xs.IncludeNumeric(XMax);

        // Estimate the y extent by coarse sampling the full domain (finite samples only).
        var ys = domain.Y(ChartValue.Number(0), UseSecondaryYAxis);
        for (var i = 0; i < DomainSamples; i++)
        {
            var x = XMin + (XMax - XMin) * i / (DomainSamples - 1);
            var y = Function(x);
            if (double.IsFinite(y)) ys.IncludeNumeric(y);
        }

        // The function (or its domain) may have changed with this relayout — force a re-sample.
        _sampledX0 = double.NaN;
    }

    public override void CollectSeries(ChartRenderContext ctx)
    {
        if (Name is { Length: > 0 } n) ctx.RegisterSeries(n);
    }

    public override void CollectLegend(ChartRenderContext ctx, List<LegendEntry> entries)
    {
        if (Name is { Length: > 0 } n)
            entries.Add(new LegendEntry(n, ctx.ColorFor(n, Color, MarkIndex)));
    }

    public override void CollectInteractive(ChartRenderContext ctx)
    {
        // Sparse samples across the visible window so hover/tooltips resolve onto the curve.
        const int hoverSamples = 48;
        if (XMax <= XMin || Function is null) return;
        var color = ctx.ColorFor(Name ?? string.Empty, Color, MarkIndex);
        for (var i = 0; i <= hoverSamples; i++)
        {
            var t = i / (float)hoverSamples;
            var x = ctx.XScale.NumericAt(t);
            if (x < XMin || x > XMax) continue;
            var y = Function(x);
            if (!double.IsFinite(y)) continue;
            // Register only on-plot samples — a pole's huge magnitudes would otherwise dominate
            // nearest-point hover resolution from far off-screen.
            var sy = ctx.MapYNumeric(y);
            if (sy < ctx.PlotRect.Y || sy > ctx.PlotRect.Bottom) continue;
            ctx.HoverPoints.Add(
                new ChartDataPoint(
                    ctx.PlotRect.X + t * ctx.PlotRect.Width,
                    sy,
                    ChartValue.Number(x),
                    ChartValue.Number(y),
                    Name ?? string.Empty,
                    NiceScale.FormatNumber(y),
                    color
                )
            );
        }
    }

    public override void Paint(ChartRenderContext ctx)
    {
        var paint = ctx.Paint;
        if (paint is null || XMax <= XMin || Function is null) return;

        EnsureSamples(ctx);
        if (_count < 2) return;

        var color = ctx.ColorFor(Name ?? string.Empty, Color, MarkIndex);

        // Entrance animation: reveal the plot left→right (matches LineMark).
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

        // Stroke each finite run; NaN samples (poles, out-of-domain) break the polyline.
        var runStart = -1;
        for (var i = 0; i <= _count; i++)
        {
            var finite = i < _count && float.IsFinite(_sy[i]);
            if (finite)
            {
                if (runStart < 0) runStart = i;
                continue;
            }

            if (runStart >= 0 && i - runStart >= 2)
                LineMark<ChartSample>.StrokePolyline(
                    ctx,
                    _sx.AsSpan(runStart, i - runStart),
                    _sy.AsSpan(runStart, i - runStart),
                    color,
                    StrokeWidth,
                    ChartInterpolation.Linear,
                    Dash,
                    DashGap
                );
            runStart = -1;
        }

        if (reveal) paint.AddClipEnd();
    }

    /// <summary>
    ///     Re-sample when the plot rect or either axis window changed; otherwise the cached
    ///     projection is still valid and paint touches nothing.
    /// </summary>
    private void EnsureSamples(ChartRenderContext ctx)
    {
        var plot = ctx.PlotRect;
        var x0 = ctx.XScale.NumericAt(0f);
        var x1 = ctx.XScale.NumericAt(1f);
        var y0 = ctx.YScale.NumericAt(0f);
        var y1 = ctx.YScale.NumericAt(1f);
        if (plot == _sampledPlot && x0 == _sampledX0 && x1 == _sampledX1 &&
            y0 == _sampledY0 && y1 == _sampledY1)
            return;

        _sampledPlot = plot;
        _sampledX0 = x0;
        _sampledX1 = x1;
        _sampledY0 = y0;
        _sampledY1 = y1;

        var count = Math.Clamp(
            (int)(plot.Width / MathF.Max(0.5f, PixelsPerSample)) + 1,
            2,
            Math.Max(2, MaxSamples)
        );
        if (_sx.Length < count)
        {
            _sx = new float[count];
            _sy = new float[count];
        }

        // Clamp far-off-plot samples to a band around the plot. The plot clip hides everything
        // past it anyway, and UNclamped magnitudes (1/x after a deep zoom toward the pole) turn
        // segment length into dash/tessellation cost — millions of commands in one frame, enough
        // to blow past wgpu's 256 MB vertex-buffer cap. Inside the band the curve is exact; a
        // clamped exit still reads as a vertical asymptote (±2 plot heights ≈ slope 500:1).
        var yLo = plot.Y - 2f * plot.Height;
        var yHi = plot.Bottom + 2f * plot.Height;
        for (var i = 0; i < count; i++)
        {
            var t = i / (float)(count - 1);
            _sx[i] = plot.X + t * plot.Width;
            var x = ctx.XScale.NumericAt(t);
            if (x < XMin || x > XMax)
            {
                _sy[i] = float.NaN;
                continue;
            }

            var y = Function(x);
            _sy[i] = double.IsFinite(y) ? Math.Clamp(ctx.MapYNumeric(y), yLo, yHi) : float.NaN;
        }

        _count = count;
    }
}
