namespace Zigote.UI.Charts.Scales;

/// <summary>One axis tick: a normalized [0,1] position along the scale plus its display label.</summary>
public readonly struct ChartTick(float position, string label, ChartValue value = default)
{
    public readonly float Position = position;
    public readonly string Label = label;

    /// <summary>The domain value this tick marks — per-tick styling keys on it.</summary>
    public readonly ChartValue Value = value;
}

/// <summary>
///     Maps <see cref="ChartValue" />s onto a normalized [0,1] axis. A scale is built in two phases:
///     every mark feeds its values through <see cref="Include" /> (domain accumulation), then the
///     chart
///     calls <see cref="FinalizeDomain" /> once before any <see cref="Normalize" /> / tick query.
///     Scales are pure logic — no widget or engine dependency — so they are headless-testable.
/// </summary>
public abstract class ChartScale
{
    /// <summary>True once <see cref="FinalizeDomain" /> ran; Include calls after that are ignored.</summary>
    protected bool Finalized;

    /// <summary>Band scales position values at discrete band centres (bars, categories).</summary>
    public virtual bool IsBand => false;

    /// <summary>Width of one band in normalized units (0 for continuous scales).</summary>
    public virtual float NormalizedBandWidth => 0f;

    /// <summary>Whether <see cref="SetVisibleWindow" /> restricts what [0,1] maps to (scrollable charts).</summary>
    public virtual bool SupportsWindowing => false;

    /// <summary>
    ///     Full data extent after <see cref="FinalizeDomain" />, in window units (numeric domain,
    ///     seconds for time, band indices for categories). Scroll clamping runs against this.
    /// </summary>
    public virtual (double Min, double Max) FullExtent => (0, 1);

    /// <summary>
    ///     Restrict the [0,1] range to the given sub-window of the finalized domain. Values outside
    ///     normalize past [0,1] and clip at the plot edge. No-op on scales without windowing.
    /// </summary>
    public virtual void SetVisibleWindow(double min, double max)
    {
    }

    /// <summary>
    ///     Clear accumulated domain state (keeping user configuration) so the scale can be rebuilt.
    ///     The chart resets its scales every layout, so a user-supplied scale instance stays reusable.
    /// </summary>
    public virtual void Reset()
    {
        Finalized = false;
    }

    public abstract void Include(ChartValue value);

    /// <summary>
    ///     Expand the domain so it also covers <paramref name="value" /> (stack tops, rule
    ///     positions).
    /// </summary>
    public virtual void IncludeNumeric(double value)
    {
    }

    public abstract void FinalizeDomain();

    /// <summary>
    ///     Normalized [0,1] position of <paramref name="value" /> (may exceed the range; callers
    ///     clip).
    /// </summary>
    public abstract float Normalize(ChartValue value);

    /// <summary>Normalized position for a raw numeric magnitude (stacked totals share the value axis).</summary>
    public virtual float NormalizeNumeric(double value)
    {
        return 0f;
    }

    /// <summary>
    ///     Domain magnitude at a normalized [0,1] position — the inverse of
    ///     <see cref="NormalizeNumeric" />, honouring the visible window. Units match
    ///     <see cref="FullExtent" /> (numeric value, seconds for time, band index for categories).
    /// </summary>
    public virtual double NumericAt(float normalized)
    {
        return 0;
    }

    public IReadOnlyList<ChartTick> BuildTicks(int targetCount,
        Func<ChartValue, string>? formatter)
    {
        var ticks = new List<ChartTick>();
        BuildTicksInto(targetCount, formatter, ticks);
        return ticks;
    }

    /// <summary>
    ///     Build ticks into a caller-reused list (cleared first). The chart relayouts — and therefore
    ///     re-ticks — on every scroll/zoom step, so the warm path reuses one list per axis.
    /// </summary>
    public abstract void BuildTicksInto(int targetCount, Func<ChartValue, string>? formatter,
        List<ChartTick> into);

    /// <summary>
    ///     Ticks at explicit user-pinned domain values (<c>ChartAxis.TickValues</c>, the AxisMarks
    ///     analogue). Values outside the visible window are skipped.
    /// </summary>
    public void BuildTicksFor(IReadOnlyList<ChartValue> values,
        Func<ChartValue, string>? formatter, List<ChartTick> into)
    {
        into.Clear();
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            var pos = Normalize(v);
            if (pos is < -0.02f or > 1.02f) continue;
            into.Add(new ChartTick(pos, formatter?.Invoke(v) ?? DefaultTickLabel(v), v));
        }
    }

    /// <summary>Label for a pinned tick value when the axis has no formatter.</summary>
    protected virtual string DefaultTickLabel(ChartValue value)
    {
        return value.ToString();
    }
}