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
        Func<T, ChartValue> y,
        Func<T, string>? series = null)
    {
        return new BarMark<T>(data, x, y) { SeriesBy = series };
    }
}

/// <summary>
///     Bars measured from the zero baseline. The category axis is whichever axis holds discrete
///     values, so <c>x: category, y: number</c> gives columns and <c>x: number, y: category</c> gives
///     horizontal bars. Multi-series data stacks by default (<see cref="Stacking" />); set
///     <see cref="ChartStacking.None" /> for side-by-side grouping.
/// </summary>
public class BarMark<T>(IReadOnlyList<T> data, Func<T, ChartValue> x, Func<T, ChartValue> y)
    : SeriesMark<T>(data, x, y)
{
    private readonly Dictionary<ChartValue, double> _roundedBottom = new();
    private readonly Dictionary<ChartValue, double> _roundedTop = new();

    private bool _horizontal;

    // Spans key on the raw ChartValue (value equality) — no per-point key strings — and the two
    // maps double-buffer across morph epochs so a re-resolve reuses them instead of reallocating.
    private Dictionary<(string Series, ChartValue X), StackedSpan>? _prevSpans;
    private Dictionary<(string Series, ChartValue X), StackedSpan> _spans = new();

    // Reused stacking-input scratch (StackCompute does not retain it) + the pooled column maps.
    private readonly List<(string Series, ChartValue X, double Value)> _triples = [];
    private readonly StackScratch _stackScratch = new();

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
        var snapshot = EpochChanged(domain);
        if (snapshot && _spans.Count > 0)
            // Morph source for animated updates — swap buffers so the displaced prev map is reused.
            (_prevSpans, _spans) = (_spans,
                _prevSpans ?? new Dictionary<(string, ChartValue), StackedSpan>());
        ResolveData(snapshot);
        if (Resolved.Count == 0) return;

        var first = Resolved[0];
        _horizontal = first.Y.Kind == ChartValueKind.Category &&
                      first.X.Kind != ChartValueKind.Category;

        var catScale = _horizontal ? domain.Y(first.Y) : domain.X(first.X);
        var valScale = _horizontal ? domain.X(first.X) : domain.Y(first.Y, UseSecondaryYAxis);
        ChartDomain.RequestZeroBaseline(valScale);

        foreach (var p in Resolved)
            catScale.Include(_horizontal ? p.Y : p.X);

        var mode = SeriesOrder.Count > 1 || SeriesOrder[0].Length > 0
            ? Stacking
            : ChartStacking.None;
        _triples.Clear();
        foreach (var p in Resolved)
        {
            var value = (_horizontal ? p.X : p.Y).Numeric;
            _triples.Add((p.Series, _horizontal ? p.Y : p.X, value));
        }

        StackCompute.Compute(
            _triples,
            SeriesOrder,
            mode,
            _spans,
            _stackScratch
        );

        _roundedTop.Clear();
        _roundedBottom.Clear();
        foreach (var ((_, key), span) in _spans)
        {
            valScale.IncludeNumeric(span.Bottom);
            valScale.IncludeNumeric(span.Top);
            // Only the outermost segment of a stacked column rounds its corner.
            if (!_roundedTop.TryGetValue(key, out var top) || span.Top > top)
                _roundedTop[key] = span.Top;
            if (!_roundedBottom.TryGetValue(key, out var bot) || span.Bottom < bot)
                _roundedBottom[key] = span.Bottom;
        }
    }

    public override void CollectInteractive(ChartRenderContext ctx)
    {
        for (var i = 0; i < Resolved.Count; i++)
        {
            var p = Resolved[i];
            var (rect, _) = BarRect(ctx, p);
            if (rect.IsEmpty && rect.Width <= 0 && rect.Height <= 0) continue;
            var value = _horizontal ? p.X : p.Y;
            var screenX = _horizontal ? rect.Right : rect.X + rect.Width / 2f;
            var screenY = _horizontal ? rect.Y + rect.Height / 2f : rect.Y;
            ctx.HoverPoints.Add(
                new ChartDataPoint(
                    screenX,
                    screenY,
                    p.X,
                    p.Y,
                    p.Series,
                    FormatValue(value),
                    ctx.ColorFor(p.Series, Color, MarkIndex)
                )
            );
        }
    }

    public override void Paint(ChartRenderContext ctx)
    {
        var paint = ctx.Paint;
        if (paint is null) return;

        for (var i = 0; i < Resolved.Count; i++)
        {
            var p = Resolved[i];
            var (rect, roundedEnd) = BarRect(ctx, p);
            if (rect.Width <= 0.01f || rect.Height <= 0.01f) continue;

            var color = ctx.ColorFor(p.Series, Color, MarkIndex);
            var r = MathF.Min(CornerRadius, MathF.Min(rect.Width, rect.Height) / 2f);
            if (r <= 0f || roundedEnd == RoundedEnd.None)
            {
                paint.AddRect(rect, color);
                continue;
            }

            // Round only the free end: clip to the bar, fill a rect extended past the baseline end
            // by the radius so the baseline corners stay square.
            var extended = roundedEnd switch {
                RoundedEnd.Top => new Rect(
                    rect.X,
                    rect.Y,
                    rect.Width,
                    rect.Height + r
                ),
                RoundedEnd.Bottom => new Rect(
                    rect.X,
                    rect.Y - r,
                    rect.Width,
                    rect.Height + r
                ),
                RoundedEnd.Right => new Rect(
                    rect.X - r,
                    rect.Y,
                    rect.Width + r,
                    rect.Height
                ),
                _ => new Rect(
                    rect.X,
                    rect.Y,
                    rect.Width + r,
                    rect.Height
                ), // Left
            };
            paint.AddClipStart(rect);
            paint.AddRect(extended, color, r);
            paint.AddClipEnd();
        }
    }

    private (Rect Rect, RoundedEnd Rounded) BarRect(ChartRenderContext ctx, ResolvedPoint p)
    {
        var catValue = _horizontal ? p.Y : p.X;
        var catScale = _horizontal ? ctx.YScale : ctx.XScale;
        var plotLen = _horizontal ? ctx.PlotRect.Height : ctx.PlotRect.Width;

        var groupWidth = catScale.IsBand
            ? catScale.NormalizedBandWidth * plotLen * WidthFraction
            : FixedWidth;

        var mode = SeriesOrder.Count > 1 || SeriesOrder[0].Length > 0
            ? Stacking
            : ChartStacking.None;
        var grouped = mode == ChartStacking.None && SeriesOrder.Count > 1;
        var barWidth = grouped
            ? MathF.Max(1f, (groupWidth - GroupGap * (SeriesOrder.Count - 1)) / SeriesOrder.Count)
            : groupWidth;

        var center = _horizontal ? ctx.MapY(catValue) : ctx.MapX(catValue);
        var catStart = center - groupWidth / 2f;
        if (grouped)
            catStart += Math.Max(0, IndexOfSeries(p.Series)) * (barWidth + GroupGap);

        if (!_spans.TryGetValue((p.Series, catValue), out var span))
            return (Rect.Zero, RoundedEnd.None);

        // Data-update morph: interpolate from the previous epoch's span (new bars grow from zero).
        var spanBottom = span.Bottom;
        var spanTop = span.Top;
        if (ctx.DataProgress < 1f)
        {
            var old = _prevSpans is not null &&
                      _prevSpans.TryGetValue((p.Series, catValue), out var o)
                ? o
                : new StackedSpan(0, 0);
            spanBottom = old.Bottom + (spanBottom - old.Bottom) * ctx.DataProgress;
            spanTop = old.Top + (spanTop - old.Top) * ctx.DataProgress;
        }

        // Entrance animation grows the bar out of the baseline.
        var bottom = spanBottom;
        var top = bottom + (spanTop - spanBottom) * ctx.Progress;

        var isOuterPositive = span.Top > span.Bottom &&
                              _roundedTop.TryGetValue(catValue, out var maxTop) &&
                              span.Top >= maxTop;
        var isOuterNegative = span.Bottom < 0 &&
                              _roundedBottom.TryGetValue(catValue, out var minBot) &&
                              span.Bottom <= minBot;

        if (_horizontal)
        {
            var x0 = ctx.MapXNumeric(bottom);
            var x1 = ctx.MapXNumeric(top);
            if (x1 < x0) (x0, x1) = (x1, x0);
            var rect = new Rect(
                x0,
                catStart,
                x1 - x0,
                barWidth
            );
            var end = isOuterPositive ? RoundedEnd.Right :
                isOuterNegative ? RoundedEnd.Left : RoundedEnd.None;
            return (rect, end);
        }
        else
        {
            var y0 = ctx.MapYNumeric(top);
            var y1 = ctx.MapYNumeric(bottom);
            if (y1 < y0) (y0, y1) = (y1, y0);
            var rect = new Rect(
                catStart,
                y0,
                barWidth,
                y1 - y0
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