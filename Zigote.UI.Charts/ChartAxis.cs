using Zigote.Core;
using Zigote.UI.Charts.Rendering;

namespace Zigote.UI.Charts;

/// <summary>Where the value-axis labels sit (defaults to trailing).</summary>
public enum ChartYAxisSide : byte
{
    Trailing,
    Leading,
}

public enum ChartLegendPosition : byte
{
    /// <summary>Show at the top when there are two or more entries.</summary>
    Auto,
    Hidden,
    Top,
    Bottom,
}

/// <summary>
///     Style for one tick's grid line + label, resolved per tick via
///     <see cref="ChartAxis.TickStyle" /> (the AxisMarks styling analogue). A plain struct in, struct
///     out — resolution runs on the paint hot path, so it allocates nothing. <c>default</c> keeps the
///     axis defaults.
/// </summary>
public readonly struct AxisTickStyle
{
    /// <summary>Grid line colour; null = the theme separator.</summary>
    public Color? GridColor { get; init; }

    /// <summary>Grid line thickness in px; 0 = the default 1px hairline.</summary>
    public float GridWidth { get; init; }

    /// <summary>Label colour; null = the theme's muted text.</summary>
    public Color? LabelColor { get; init; }

    /// <summary>Suppress this tick's grid line (label still drawn).</summary>
    public bool HideGrid { get; init; }

    /// <summary>Suppress this tick's label (grid line still drawn).</summary>
    public bool HideLabel { get; init; }
}

/// <summary>Per-axis configuration: visibility, grid, labels, title, and label formatting.</summary>
public sealed class ChartAxis
{
    public bool Show { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public bool ShowLabels { get; set; } = true;

    /// <summary>Draw the axis baseline itself (off by default — grid + labels carry the axis).</summary>
    public bool ShowLine { get; set; }

    public string? Title { get; set; }

    /// <summary>Approximate tick count; null derives it from the plot extent.</summary>
    public int? TickTarget { get; set; }

    public Func<ChartValue, string>? Formatter { get; set; }

    /// <summary>
    ///     Explicit tick values (the AxisMarks custom-values analogue); null = the scale's automatic
    ///     ticks. Values outside the visible window are skipped, so pinned ticks compose with
    ///     scroll/zoom. Labels come from <see cref="Formatter" /> (or the scale's default format).
    /// </summary>
    public IReadOnlyList<ChartValue>? TickValues { get; set; }

    /// <summary>
    ///     Per-tick styling, called with each tick's domain value during paint. Hot path — return the
    ///     struct from pure logic, no strings or captures that allocate.
    /// </summary>
    public Func<ChartValue, AxisTickStyle>? TickStyle { get; set; }
}

/// <summary>What the pointer is over: the shared x position and every series' datum there.</summary>
public sealed class ChartHoverInfo
{
    public required string XLabel { get; init; }
    public required ChartValue X { get; init; }
    public required IReadOnlyList<ChartDataPoint> Points { get; init; }
}
