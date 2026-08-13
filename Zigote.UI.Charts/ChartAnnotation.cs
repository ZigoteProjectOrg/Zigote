using Zigote.Core;

namespace Zigote.UI.Charts;

/// <summary>Where an annotation sits relative to its (x, y) data anchor.</summary>
public enum ChartAnnotationPlacement : byte
{
    Over,
    Above,
    Below,
    Leading,
    Trailing,
}

/// <summary>
///     A text label (with an optional dot + connector) pinned to a data coordinate, painted above
///     the marks and clipped to the plot. The chart projects <see cref="X" />/<see cref="Y" /> through
///     its scales each frame, so annotations track scroll, zoom, and animated data updates.
///     <para>
///         Anchor either axis: give both <see cref="X" /> and <see cref="Y" /> to pin a point, or just
///         one to pin to a vertical/horizontal position (the other coordinate is taken from
///         <see cref="ChartAnnotationPlacement" /> against the plot edge).
///     </para>
/// </summary>
public sealed class ChartAnnotation
{
    /// <summary>X data position; null pins horizontally to the plot edge per <see cref="Placement" />.</summary>
    public ChartValue? X { get; set; }

    /// <summary>Y data position; null pins vertically to the plot edge per <see cref="Placement" />.</summary>
    public ChartValue? Y { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Where the label sits relative to the anchor point.</summary>
    public ChartAnnotationPlacement Placement { get; set; } = ChartAnnotationPlacement.Above;

    /// <summary>Text colour; null = the theme's secondary text colour.</summary>
    public Color? Color { get; set; }

    /// <summary>Optional pill behind the text for contrast over busy marks.</summary>
    public Color? Background { get; set; }

    /// <summary>Draw a small dot at the anchor and a connector to the label.</summary>
    public bool ShowMarker { get; set; } = true;

    /// <summary>
    ///     Bind the y coordinate to the secondary axis (see
    ///     <see cref="ChartMark.UseSecondaryYAxis" />).
    /// </summary>
    public bool UseSecondaryYAxis { get; set; }
}
