namespace Zigote.UI.BottomSheets;

/// <summary>
///     Everything the sheet paints, as tokens rather than a design language — this package draws a
///     rounded card, a scrim and a drag pill and nothing else, so Material, libadwaita, or a game HUD
///     each get their own look by handing in a style.
///     <para>
///         Every colour/shape token is nullable and falls back to the ambient <see cref="ThemeData" />
///         at build time, so the default already matches whatever theme the app runs. A design package
///         supplies its own:
///         <code>
///   new BottomSheetStyle {              // libadwaita
///       Background   = palette.DialogBg,
///       CornerRadius = AdwMetrics.WindowRadius,
///   }
/// </code>
///     </para>
/// </summary>
public sealed record BottomSheetStyle
{
    /// <summary>Theme-driven defaults.</summary>
    public static readonly BottomSheetStyle Default = new();

    /// <summary>Card fill. Default: <see cref="ThemeData.Surface" />.</summary>
    public Color? Background { get; init; }

    /// <summary>Top-corner radius of the card. Default: <see cref="Radii.Xl" />.</summary>
    public float? CornerRadius { get; init; }

    /// <summary>Drop shadow under the card. Default: <see cref="Elevation.Z3" />. Pass <see cref="Elevation.None" /> for flat.</summary>
    public ShadowStyle? Shadow { get; init; }

    /// <summary>Scrim painted over the page behind a modal sheet. Default: <see cref="ThemeData.OverlayBackground" />.</summary>
    public Color? BarrierColor { get; init; }

    /// <summary>Show the drag pill at the top of the card (also the sheet's drag surface).</summary>
    public bool ShowDragHandle { get; init; } = true;

    /// <summary>Pill colour. Default: <see cref="ThemeData.Label3" />.</summary>
    public Color? DragHandleColor { get; init; }

    /// <summary>Pill size.</summary>
    public float DragHandleWidth { get; init; } = 32f;

    public float DragHandleHeight { get; init; } = 4f;

    /// <summary>Height of the grabbable strip the pill is centred in.</summary>
    public float DragHandleAreaHeight { get; init; } = 20f;

    /// <summary>Padding between the card edge and the sheet content. Default: none.</summary>
    public EdgeInsets Padding { get; init; } = EdgeInsets.Zero;

    /// <summary>Fill in the theme-dependent defaults for the appearance in scope.</summary>
    internal Resolved Resolve(ThemeData theme)
    {
        return new Resolved(
            Background ?? theme.Surface,
            CornerRadius ?? Radii.Xl,
            Shadow ?? Elevation.Z3,
            BarrierColor ?? theme.OverlayBackground,
            DragHandleColor ?? theme.Label3
        );
    }

    internal readonly record struct Resolved(
        Color Background,
        float CornerRadius,
        ShadowStyle Shadow,
        Color BarrierColor,
        Color DragHandleColor);
}