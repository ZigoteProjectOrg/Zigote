using Zigote.Core;
using Zigote.UI.Charts.Rendering;
using Zigote.UI.Charts.Scales;

namespace Zigote.UI.Charts.Marks;

/// <summary>
///     The shared axis domain a chart builds before painting. Marks request a scale for the kind of
///     value they plot; the first mark to touch an axis decides its scale type (category → band,
///     time → temporal, number → linear) unless the user pinned one explicitly on the chart.
/// </summary>
public sealed class ChartDomain
{
    public ChartScale? XScale { get; set; }
    public ChartScale? YScale { get; set; }

    /// <summary>The secondary (opposite-side) y scale, created lazily when a mark binds to it.</summary>
    public ChartScale? YScale2 { get; set; }

    /// <summary>
    ///     Bumped by the chart on every animated data update. Marks snapshot their previous values
    ///     on the first layout of a new epoch (and only then), so mid-morph relayouts — scroll,
    ///     resize — don't collapse the interpolation source onto the target.
    /// </summary>
    public int DataEpoch { get; set; }

    public ChartScale X(ChartValue sample) => XScale ??= CreateFor(sample);

    /// <summary>The y scale for a mark; <paramref name="secondary" /> selects the opposite axis.</summary>
    public ChartScale Y(ChartValue sample, bool secondary = false)
    {
        return secondary
            ? YScale2 ??= CreateFor(sample)
            : YScale ??= CreateFor(sample);
    }

    private static ChartScale CreateFor(ChartValue sample)
    {
        return sample.Kind switch {
            ChartValueKind.Category => new BandScale(),
            ChartValueKind.Time => new TimeScale(),
            _ => new LinearScale(),
        };
    }

    /// <summary>Marks that measure from zero (bars, areas) anchor a linear value axis at the baseline.</summary>
    public static void RequestZeroBaseline(ChartScale scale)
    {
        if (scale is LinearScale linear) linear.IncludeZero = true;
    }
}

/// <summary>One legend swatch: a series (or sector category) name and its resolved color.</summary>
public readonly record struct LegendEntry(string Label, Color Color);

/// <summary>
///     Base of everything drawable inside a <see cref="Chart" />. Marks are composable: a chart holds
///     an ordered list of them, they accumulate a shared x/y domain, then paint back-to-front through
///     the same scales — so a BarMark, a LineMark, and a RuleMark overlay naturally.
///     <para>
///         Lifecycle per layout: <see cref="IncludeDomain" /> (resolve data, feed scales) →
///         <see cref="CollectSeries" /> (stable palette slots) → <see cref="CollectInteractive" />
///         (hover registry) → <see cref="Paint" /> per frame.
///     </para>
/// </summary>
public abstract class ChartMark
{
    private int _seenEpoch = -1;

    /// <summary>Fixed color override; otherwise the series/mark palette slot decides.</summary>
    public Color? Color { get; set; }

    /// <summary>Legend/series label for single-series marks (a per-datum series selector overrides it).</summary>
    public string? Name { get; set; }

    /// <summary>
    ///     Bind this mark to the chart's secondary (opposite-side) y-axis instead of the primary one.
    ///     Lets two series with different units share the plot (e.g. price + volume). Ignored by
    ///     polar marks.
    /// </summary>
    public bool UseSecondaryYAxis { get; set; }

    /// <summary>Index of this mark in the chart's mark list (assigned by the chart before layout).</summary>
    public int MarkIndex { get; internal set; }

    /// <summary>Polar marks (sectors) ignore the cartesian axes entirely.</summary>
    public virtual bool IsPolar => false;

    /// <summary>
    ///     True on the first <see cref="IncludeDomain" /> of a new data epoch — the one moment a
    ///     mark should snapshot its current values as the morph source before re-resolving.
    /// </summary>
    protected bool EpochChanged(ChartDomain domain)
    {
        bool changed = domain.DataEpoch != _seenEpoch;
        _seenEpoch = domain.DataEpoch;
        return changed;
    }

    public abstract void IncludeDomain(ChartDomain domain);

    /// <summary>Register series names (in paint order) so palette assignment is stable across marks.</summary>
    public virtual void CollectSeries(ChartRenderContext ctx) { }

    /// <summary>Contribute legend entries. Default: one entry per registered series of this mark.</summary>
    public virtual void CollectLegend(ChartRenderContext ctx, List<LegendEntry> entries) { }

    /// <summary>Register hover/tap targets into <see cref="ChartRenderContext.HoverPoints" />.</summary>
    public virtual void CollectInteractive(ChartRenderContext ctx) { }

    /// <summary>
    ///     Region-based hit test (sectors, cells). Marks with an area larger than their registered
    ///     hover point override this; the chart tries it before nearest-point resolution.
    /// </summary>
    public virtual bool TryHitTest(ChartRenderContext ctx, float x, float y,
        out ChartDataPoint point)
    {
        point = default;
        return false;
    }

    public abstract void Paint(ChartRenderContext ctx);
}

/// <summary>
///     Base for data-driven cartesian marks: a data list plus x/y selectors returning
///     <see cref="ChartValue" />s, and an optional per-datum series selector for multi-series data
///     in long format. Resolution happens once per layout and is cached for paint.
/// </summary>
public abstract class SeriesMark<T> : ChartMark
{
    private readonly HashSet<string> _seenSeries = [];
    private Dictionary<string, List<ResolvedPoint>>? _groups;
    private Dictionary<(string Series, ChartValue X), double>? _prevValues;

    private List<ResolvedPoint>? _resolved;
    private List<string>? _seriesOrder;

    protected SeriesMark(IReadOnlyList<T> data, Func<T, ChartValue> x, Func<T, ChartValue> y)
    {
        Data = data;
        X = x;
        Y = y;
    }

    public IReadOnlyList<T> Data { get; set; }
    public Func<T, ChartValue> X { get; set; }
    public Func<T, ChartValue> Y { get; set; }

    /// <summary>Split the data into one visual series per distinct value (long/tidy data format).</summary>
    public Func<T, string>? SeriesBy { get; set; }

    /// <summary>Resolved points in data order (call after <see cref="IncludeDomain" />).</summary>
    protected IReadOnlyList<ResolvedPoint> Resolved => _resolved ?? [];

    /// <summary>Distinct series names in first-seen order.</summary>
    protected IReadOnlyList<string> SeriesOrder => _seriesOrder ?? [];

    /// <summary>Bumped every <see cref="ResolveData" />; per-frame paint caches key on it.</summary>
    protected int ResolveVersion { get; private set; }

    /// <summary>Index of <paramref name="series" /> in <see cref="SeriesOrder" /> (-1 if unknown).</summary>
    protected int IndexOfSeries(string series)
    {
        var order = SeriesOrder;
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] == series)
                return i;
        }

        return -1;
    }

    protected void ResolveData(bool snapshotPrevious = false)
    {
        // Morph source: the values shown before this epoch's data change.
        if (snapshotPrevious && _resolved is { Count: > 0 })
        {
            _prevValues ??= new Dictionary<(string, ChartValue), double>();
            _prevValues.Clear();
            foreach (var p in _resolved)
            {
                if (p.Y.Kind != ChartValueKind.Category)
                    _prevValues[(p.Series, p.X)] = p.Y.Numeric;
            }
        }

        // Reuse the resolve scratch across resolves — a live chart re-resolves many times a second,
        // so fresh collections here were the second-biggest churn after the hover registry.
        if (_resolved is null) _resolved = new List<ResolvedPoint>(Data.Count);
        else _resolved.Clear();
        if (_seriesOrder is null) _seriesOrder = [];
        else _seriesOrder.Clear();
        _groups ??= new Dictionary<string, List<ResolvedPoint>>();
        foreach (var list in _groups.Values) list.Clear();
        _seenSeries.Clear();
        var seen = _seenSeries;
        foreach (var datum in Data)
        {
            string series = SeriesBy?.Invoke(datum) ?? Name ?? string.Empty;
            if (series.Length > 0 && seen.Add(series)) _seriesOrder.Add(series);
            var p = new ResolvedPoint(X: X(datum), Y: Y(datum), Series: series);
            _resolved.Add(p);
            // Group as we resolve so Paint never re-allocates the grouping (per-frame hot path).
            if (!_groups.TryGetValue(key: series, value: out var g)) _groups[series] = g = [];
            g.Add(p);
        }

        if (_seriesOrder.Count == 0) _seriesOrder.Add(string.Empty);
        ResolveVersion++;
    }

    public override void CollectSeries(ChartRenderContext ctx)
    {
        foreach (string s in SeriesOrder) ctx.RegisterSeries(s);
    }

    public override void CollectLegend(ChartRenderContext ctx, List<LegendEntry> entries)
    {
        foreach (string s in SeriesOrder)
        {
            if (s.Length == 0) continue;
            entries.Add(
                new LegendEntry(
                    Label: s,
                    Color: ctx.ColorFor(series: s, markOverride: Color, markIndex: MarkIndex)
                )
            );
        }
    }

    /// <summary>
    ///     Resolved points grouped per series (order-preserving), built once in
    ///     <see cref="ResolveData" /> and reused every paint — never re-allocated on the hot path.
    ///     A series present last resolve but absent now yields an empty (cleared) list; callers skip
    ///     empties.
    /// </summary>
    protected Dictionary<string, List<ResolvedPoint>> GroupBySeries() => _groups ?? [];

    /// <summary>
    ///     The datum's numeric y, interpolated from its previous-epoch value while a data-update
    ///     morph is in flight. Category values and points with no previous value return the target.
    /// </summary>
    protected double MorphedY(ChartRenderContext ctx, in ResolvedPoint p)
    {
        double target = p.Y.Numeric;
        if (ctx.DataProgress >= 1f || _prevValues is null || p.Y.Kind == ChartValueKind.Category)
            return target;
        return _prevValues.TryGetValue(key: (p.Series, p.X), value: out double old)
            ? old + ((target - old) * ctx.DataProgress)
            : target;
    }

    /// <summary>Default numeric label for tooltips.</summary>
    protected static string FormatValue(ChartValue v) => v.Kind == ChartValueKind.Number
        ? NiceScale.FormatNumber(v.Numeric)
        : v.ToString();

    protected internal readonly record struct ResolvedPoint(
        ChartValue X,
        ChartValue Y,
        string Series);
}
