using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Charts.Scales;
using Zigote.UI.Theme;

namespace Zigote.UI.Charts.Rendering;

/// <summary>One interactive datum a mark registered for hover/tap resolution.</summary>
public readonly struct ChartDataPoint(
    float screenX,
    float screenY,
    ChartValue x,
    ChartValue y,
    string series,
    string valueLabel,
    Color color)
{
    public readonly float ScreenX = screenX;
    public readonly float ScreenY = screenY;
    public readonly ChartValue X = x;
    public readonly ChartValue Y = y;
    public readonly string Series = series;
    public readonly string ValueLabel = valueLabel;
    public readonly Color Color = color;
}

/// <summary>
///     Everything a mark needs to place and paint itself: the plot rectangle, the finalized scales,
///     value→pixel mapping, series color resolution, the entrance-animation progress, and the hover
///     registry. Built by <see cref="Chart" /> during layout; <see cref="Paint" /> is only set while
///     the chart is painting.
/// </summary>
public sealed class ChartRenderContext
{
    /// <summary>
    ///     Palette-slot assignment, shared across a primary/secondary-axis context pair so a series
    ///     keeps one colour regardless of which y-axis its mark binds to. Defaults to its own map.
    /// </summary>
    public Dictionary<string, int> SeriesSlotsShared { get; init; } = new();

    public required Rect PlotRect { get; init; }
    public required ChartScale XScale { get; init; }
    public required ChartScale YScale { get; init; }
    public required ThemeData Theme { get; init; }
    public required ChartPalette Palette { get; init; }

    /// <summary>Entrance-animation progress, eased [0,1]. 1 when not animating.</summary>
    public float Progress { get; set; } = 1f;

    /// <summary>
    ///     Data-update morph progress, eased [0,1]. While &lt; 1, marks interpolate from their
    ///     previous values toward the current data (see <c>Chart.AnimateDataUpdate</c>).
    /// </summary>
    public float DataProgress { get; set; } = 1f;

    /// <summary>Set only during the chart's Paint pass.</summary>
    public PaintList? Paint { get; set; }

    /// <summary>Interactive points registered by marks during layout, consumed by hover/tap.</summary>
    public List<ChartDataPoint> HoverPoints { get; init; } = [];

    /// <summary>Screen y of the value-axis zero line, clamped into the plot (bar/area baseline).</summary>
    public float BaselineY => Math.Clamp(
        value: MapYNumeric(0),
        min: PlotRect.Y,
        max: PlotRect.Bottom
    );

    public float BaselineX => Math.Clamp(
        value: MapXNumeric(0),
        min: PlotRect.X,
        max: PlotRect.Right
    );

    public IReadOnlyDictionary<string, int> SeriesSlots => SeriesSlotsShared;

    /// <summary>
    ///     A sibling context over the same plot but a different y-scale (the secondary axis),
    ///     sharing this context's series colours and hover registry.
    /// </summary>
    public ChartRenderContext WithYScale(ChartScale yScale)
    {
        return new ChartRenderContext {
            PlotRect = PlotRect,
            XScale = XScale,
            YScale = yScale,
            Theme = Theme,
            Palette = Palette,
            SeriesSlotsShared = SeriesSlotsShared,
            HoverPoints = HoverPoints,
            Progress = Progress,
            DataProgress = DataProgress,
        };
    }

    // ── Mapping ──────────────────────────────────────────────────────────────

    public float MapX(ChartValue v) => PlotRect.X + (XScale.Normalize(v) * PlotRect.Width);

    /// <summary>Y is flipped: larger values render higher (screen y grows downward).</summary>
    public float MapY(ChartValue v) => PlotRect.Bottom - (YScale.Normalize(v) * PlotRect.Height);

    public float MapXNumeric(double v) =>
        PlotRect.X + (XScale.NormalizeNumeric(v) * PlotRect.Width);

    public float MapYNumeric(double v) =>
        PlotRect.Bottom - (YScale.NormalizeNumeric(v) * PlotRect.Height);

    // ── Series colors ─────────────────────────────────────────────────────────

    /// <summary>Register <paramref name="series" /> in first-seen order so palette slots are stable.</summary>
    public void RegisterSeries(string series)
    {
        if (series.Length == 0) return;
        SeriesSlotsShared.TryAdd(key: series, value: SeriesSlotsShared.Count);
    }

    /// <summary>
    ///     Resolve a datum's color: the mark's explicit override wins, then the series' palette slot,
    ///     then the palette slot of the mark itself (<paramref name="markIndex" />).
    /// </summary>
    public Color ColorFor(string series, Color? markOverride, int markIndex)
    {
        if (markOverride.HasValue) return markOverride.Value;
        if (series.Length > 0 && SeriesSlotsShared.TryGetValue(key: series, value: out int slot))
            return Palette[slot];
        return Palette[markIndex];
    }

    // ── Stroke helpers ────────────────────────────────────────────────────────

    /// <summary>Stroke a list of cubic segments as one visual path.</summary>
    public void StrokeCubics(IReadOnlyList<CubicSegment> segments, Color color, float width)
    {
        var paint = Paint;
        if (paint is null) return;
        foreach (var s in segments)
        {
            paint.AddBezier(
                x0: s.X0,
                y0: s.Y0,
                x1: s.X1,
                y1: s.Y1,
                x2: s.X2,
                y2: s.Y2,
                x3: s.X3,
                y3: s.Y3,
                color: color,
                width: width
            );
        }
    }

    /// <summary>Stroke one cubic segment (used on the hot path to avoid building a segment list).</summary>
    public void StrokeCubic(in CubicSegment s, Color color, float width)
    {
        Paint?.AddBezier(
            x0: s.X0,
            y0: s.Y0,
            x1: s.X1,
            y1: s.Y1,
            x2: s.X2,
            y2: s.Y2,
            x3: s.X3,
            y3: s.Y3,
            color: color,
            width: width
        );
    }

    /// <summary>Straight line, optionally dashed (<paramref name="dash" /> ≤ 0 = solid).</summary>
    public void StrokeLine(float x0, float y0, float x1, float y1, Color color, float width,
        float dash = 0f, float gap = 0f)
    {
        var paint = Paint;
        if (paint is null) return;
        float dx = x1 - x0;
        float dy = y1 - y0;
        float len = MathF.Sqrt((dx * dx) + (dy * dy));
        if (len < 0.01f) return;
        if (dash <= 0f)
        {
            var s = CubicSegment.Line(
                x0: x0,
                y0: y0,
                x1: x1,
                y1: y1
            );
            paint.AddBezier(
                x0: s.X0,
                y0: s.Y0,
                x1: s.X1,
                y1: s.Y1,
                x2: s.X2,
                y2: s.Y2,
                x3: s.X3,
                y3: s.Y3,
                color: color,
                width: width
            );
            return;
        }

        float ux = dx / len;
        float uy = dy / len;
        foreach ((float start, float end) in ChartGeometry.Dashes(
                     length: len,
                     dash: dash,
                     gap: gap
                 ))
        {
            var s = CubicSegment.Line(
                x0: x0 + (ux * start),
                y0: y0 + (uy * start),
                x1: x0 + (ux * end),
                y1: y0 + (uy * end)
            );
            paint.AddBezier(
                x0: s.X0,
                y0: s.Y0,
                x1: s.X1,
                y1: s.Y1,
                x2: s.X2,
                y2: s.Y2,
                x3: s.X3,
                y3: s.Y3,
                color: color,
                width: width
            );
        }
    }
}
