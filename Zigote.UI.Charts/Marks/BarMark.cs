using Zigote.Core;
using Zigote.UI.Charts.Rendering;

namespace Zigote.UI.Charts.Marks;

/// <summary>
///     Type-inference-friendly factories:
///     <c>BarMark.Of(data, d =&gt; d.Month, d =&gt; d.Sales)</c>.
/// </summary>
public static class BarMark
{
    public static BarMark<T> Of<T>(IReadOnlyList<T> data, Func<T, ChartValue> x,
        Func<T, ChartValue> y, Func<T, string>? series = null) =>
        new(data: data, x: x, y: y) { SeriesBy = series };
}

/// <summary>
///     Bars measured from the zero baseline. The category axis is whichever axis holds discrete
///     values, so <c>x: category, y: number</c> gives columns and <c>x: number, y: category</c> gives
///     horizontal bars. Multi-series data stacks by default (<see cref="Stacking" />); set
///     <see cref="ChartStacking.None" /> for side-by-side grouping.
/// </summary>
public class BarMark<T>(IReadOnlyList<T> data, Func<T, ChartValue> x, Func<T, ChartValue> y)
    : SeriesMark<T>(data: data, x: x, y: y)
{
    private readonly Dictionary<ChartValue, double> _roundedBottom = new();
    private readonly Dictionary<ChartValue, double> _roundedTop = new();
    private readonly StackScratch _stackScratch = new();

    // Reused stacking-input scratch (StackCompute does not retain it) + the pooled column maps.
    private readonly List<(string Series, ChartValue X, double Value)> _triples = [];

    private bool _horizontal;

    // Spans key on the raw ChartValue (value equality) — no per-point key strings — and the two
    // maps double-buffer across morph epochs so a re-resolve reuses them instead of reallocating.
    private Dictionary<(string Series, ChartValue X), StackedSpan>? _prevSpans;
    private Dictionary<(string Series, ChartValue X), StackedSpan> _spans = new();

    public ChartStacking Stacking { get; set; } = ChartStacking.Standard;

    /// <summary>Fraction of the category band the bar (or bar group) occupies.</summary>
    public float WidthFraction { get; set; } = 0.72f;

    /// <summary>Bar width in pixels when the category axis is continuous (numeric/time x).</summary>
    public float FixedWidth { get; set; } = 14f;

    /// <summary>Radius on the bar's free end (the end away from the baseline).</summary>
    public float CornerRadius { get; set; } = 3f;

    /// <summary>Gap in pixels between bars of a side-by-side group.</summary>
    public float GroupGap { get; set; } = 2f;

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

        var first = Resolved[0];
        _horizontal = first.Y.Kind == ChartValueKind.Category &&
                      first.X.Kind != ChartValueKind.Category;

        var catScale = _horizontal ? domain.Y(first.Y) : domain.X(first.X);
        var valScale = _horizontal
            ? domain.X(first.X)
            : domain.Y(sample: first.Y, secondary: UseSecondaryYAxis);
        ChartDomain.RequestZeroBaseline(valScale);

        foreach (var p in Resolved)
            catScale.Include(_horizontal ? p.Y : p.X);

        var mode = SeriesOrder.Count > 1 || SeriesOrder[0].Length > 0
            ? Stacking
            : ChartStacking.None;
        _triples.Clear();
        foreach (var p in Resolved)
        {
            double value = (_horizontal ? p.X : p.Y).Numeric;
            _triples.Add((p.Series, _horizontal ? p.Y : p.X, value));
        }

        StackCompute.Compute(
            points: _triples,
            seriesOrder: SeriesOrder,
            mode: mode,
            result: _spans,
            scratch: _stackScratch
        );

        _roundedTop.Clear();
        _roundedBottom.Clear();
        foreach (var ((_, key), span) in _spans)
        {
            valScale.IncludeNumeric(span.Bottom);
            valScale.IncludeNumeric(span.Top);
            // Only the outermost segment of a stacked column rounds its corner.
            if (!_roundedTop.TryGetValue(key: key, value: out double top) || span.Top > top)
                _roundedTop[key] = span.Top;
            if (!_roundedBottom.TryGetValue(key: key, value: out double bot) || span.Bottom < bot)
                _roundedBottom[key] = span.Bottom;
        }
    }

    public override void CollectInteractive(ChartRenderContext ctx)
    {
        for (int i = 0; i < Resolved.Count; i++)
        {
            var p = Resolved[i];
            var (rect, _) = BarRect(ctx: ctx, p: p);
            if (rect.IsEmpty && rect.Width <= 0 && rect.Height <= 0) continue;
            var value = _horizontal ? p.X : p.Y;
            float screenX = _horizontal ? rect.Right : rect.X + (rect.Width / 2f);
            float screenY = _horizontal ? rect.Y + (rect.Height / 2f) : rect.Y;
            ctx.HoverPoints.Add(
                new ChartDataPoint(
                    screenX: screenX,
                    screenY: screenY,
                    x: p.X,
                    y: p.Y,
                    series: p.Series,
                    valueLabel: FormatValue(value),
                    color: ctx.ColorFor(series: p.Series, markOverride: Color, markIndex: MarkIndex)
                )
            );
        }
    }

    public override void Paint(ChartRenderContext ctx)
    {
        var paint = ctx.Paint;
        if (paint is null) return;

        for (int i = 0; i < Resolved.Count; i++)
        {
            var p = Resolved[i];
            var (rect, roundedEnd) = BarRect(ctx: ctx, p: p);
            if (rect.Width <= 0.01f || rect.Height <= 0.01f) continue;

            var color = ctx.ColorFor(series: p.Series, markOverride: Color, markIndex: MarkIndex);
            float r = MathF.Min(x: CornerRadius, y: MathF.Min(x: rect.Width, y: rect.Height) / 2f);
            if (r <= 0f || roundedEnd == RoundedEnd.None)
            {
                paint.AddRect(bounds: rect, color: color);
                continue;
            }

            // Round only the free end: clip to the bar, fill a rect extended past the baseline end
            // by the radius so the baseline corners stay square.
            var extended = roundedEnd switch {
                RoundedEnd.Top => new Rect(
                    x: rect.X,
                    y: rect.Y,
                    width: rect.Width,
                    height: rect.Height + r
                ),
                RoundedEnd.Bottom => new Rect(
                    x: rect.X,
                    y: rect.Y - r,
                    width: rect.Width,
                    height: rect.Height + r
                ),
                RoundedEnd.Right => new Rect(
                    x: rect.X - r,
                    y: rect.Y,
                    width: rect.Width + r,
                    height: rect.Height
                ),
                _ => new Rect(
                    x: rect.X,
                    y: rect.Y,
                    width: rect.Width + r,
                    height: rect.Height
                ), // Left
            };
            paint.AddClipStart(rect);
            paint.AddRect(bounds: extended, color: color, radius: r);
            paint.AddClipEnd();
        }
    }

    private (Rect Rect, RoundedEnd Rounded) BarRect(ChartRenderContext ctx, ResolvedPoint p)
    {
        var catValue = _horizontal ? p.Y : p.X;
        var catScale = _horizontal ? ctx.YScale : ctx.XScale;
        float plotLen = _horizontal ? ctx.PlotRect.Height : ctx.PlotRect.Width;

        float groupWidth = catScale.IsBand
            ? catScale.NormalizedBandWidth * plotLen * WidthFraction
            : FixedWidth;

        var mode = SeriesOrder.Count > 1 || SeriesOrder[0].Length > 0
            ? Stacking
            : ChartStacking.None;
        bool grouped = mode == ChartStacking.None && SeriesOrder.Count > 1;
        float barWidth = grouped
            ? MathF.Max(
                x: 1f,
                y: (groupWidth - (GroupGap * (SeriesOrder.Count - 1))) / SeriesOrder.Count
            )
            : groupWidth;

        float center = _horizontal ? ctx.MapY(catValue) : ctx.MapX(catValue);
        float catStart = center - (groupWidth / 2f);
        if (grouped)
            catStart += Math.Max(val1: 0, val2: IndexOfSeries(p.Series)) * (barWidth + GroupGap);

        if (!_spans.TryGetValue(key: (p.Series, catValue), value: out var span))
            return (Rect.Zero, RoundedEnd.None);

        // Data-update morph: interpolate from the previous epoch's span (new bars grow from zero).
        double spanBottom = span.Bottom;
        double spanTop = span.Top;
        if (ctx.DataProgress < 1f)
        {
            var old = _prevSpans is not null &&
                      _prevSpans.TryGetValue(key: (p.Series, catValue), value: out var o)
                ? o
                : new StackedSpan(Bottom: 0, Top: 0);
            spanBottom = old.Bottom + ((spanBottom - old.Bottom) * ctx.DataProgress);
            spanTop = old.Top + ((spanTop - old.Top) * ctx.DataProgress);
        }

        // Entrance animation grows the bar out of the baseline.
        double bottom = spanBottom;
        double top = bottom + ((spanTop - spanBottom) * ctx.Progress);

        bool isOuterPositive = span.Top > span.Bottom &&
                               _roundedTop.TryGetValue(key: catValue, value: out double maxTop) &&
                               span.Top >= maxTop;
        bool isOuterNegative = span.Bottom < 0 &&
                               _roundedBottom.TryGetValue(
                                   key: catValue,
                                   value: out double minBot
                               ) &&
                               span.Bottom <= minBot;

        if (_horizontal)
        {
            float x0 = ctx.MapXNumeric(bottom);
            float x1 = ctx.MapXNumeric(top);
            if (x1 < x0) (x0, x1) = (x1, x0);
            var rect = new Rect(
                x: x0,
                y: catStart,
                width: x1 - x0,
                height: barWidth
            );
            var end = isOuterPositive ? RoundedEnd.Right :
                isOuterNegative ? RoundedEnd.Left : RoundedEnd.None;
            return (rect, end);
        }
        else
        {
            float y0 = ctx.MapYNumeric(top);
            float y1 = ctx.MapYNumeric(bottom);
            if (y1 < y0) (y0, y1) = (y1, y0);
            var rect = new Rect(
                x: catStart,
                y: y0,
                width: barWidth,
                height: y1 - y0
            );
            var end = isOuterPositive ? RoundedEnd.Top :
                isOuterNegative ? RoundedEnd.Bottom : RoundedEnd.None;
            return (rect, end);
        }
    }

    private enum RoundedEnd
    {
        None,
        Top,
        Bottom,
        Left,
        Right,
    }
}
