namespace Zigote.UI.Adwaita;

/// <summary>
///     Adwaita appearances as <see cref="ThemeData" /> instances. Assign to
///     <see cref="Zigote.UI.Host.ZigoteApp.Theme" /> (or use <see cref="AdwaitaApp" />, which defaults
///     to <see cref="Light" /> like GNOME). Tokens with no ThemeData slot live in
///     <see cref="AdwPalette" />.
/// </summary>
public static class AdwTheme
{
    /// <summary>Adwaita light (the GNOME default appearance).</summary>
    public static ThemeData Light { get; } = Create(accent: AdwAccent.Blue, dark: false);

    /// <summary>Adwaita dark.</summary>
    public static ThemeData Dark { get; } = Create(accent: AdwAccent.Blue, dark: true);

    /// <summary>
    ///     An Adwaita theme with one of the nine system accent hues. Cache the result — theme
    ///     switching is reference-identity based.
    /// </summary>
    public static ThemeData Create(AdwAccent accent, bool dark)
    {
        var p = dark ? AdwPalette.Dark : AdwPalette.Light;
        var accentBg = AdwAccentColors.Bg(accent);
        var accentStandalone = AdwAccentColors.Standalone(bg: accentBg, dark: dark);

        return new ThemeData {
            IsDark = dark,

            // Legacy trio.
            Background = p.WindowBg,
            Surface = p.ViewBg,
            SurfaceAlt = p.SidebarBg,

            // Chrome surfaces.
            Window = p.WindowBg,
            TitleBar = p.HeaderbarBg,
            Toolbar = p.HeaderbarBg,
            Sidebar = p.SidebarBg,
            Content = p.ViewBg,
            Panel = p.WindowBg,
            PanelRaised = dark ? p.DialogBg : p.ViewBg,
            PanelSunken = dark ? p.ViewBg : p.SidebarBg,
            Card = p.CardBg,
            CardRaised = dark ? p.DialogBg : p.CardBg,

            // Control fills — the Adwaita translucent button ladder (currentColor 10/15/30%).
            Control = p.ButtonFill,
            ControlHover = p.ButtonFillHover,
            ControlPressed = p.ButtonFillActive,
            // A disabled Adwaita control keeps its fill and dims as a whole
            // (filter: Opacity(--disabled-opacity)), so this is the same fill, not a fainter one.
            ControlDisabled = p.ButtonFill,

            // Accent / status.
            Primary = accentBg,
            PrimaryDark = accentStandalone,
            Accent = accentBg,
            // %opaque_button overlays currentColor (= white) on hover and rgb(0 0 6 / 20%) on
            // press: lighter, then darker. See AdwStyle.Solid.
            AccentHover = AdwPalette.Mix(a: Color.White, b: accentBg, t: 0.10f),
            AccentPressed = AdwPalette.Mix(a: AdwStyle.Ink, b: accentBg, t: 0.20f),
            Error = p.Error,
            Danger = p.DestructiveBg,
            Success = p.SuccessBg,
            Warning = p.WarningBg,
            Info = accentStandalone,

            // Text.
            OnBackground = p.WindowFg,
            OnSurface = p.ViewFg,
            OnPrimary = p.AccentFg,
            TextSecondary = p.DimLabel,
            TextMuted = p.DimLabel,
            // Opaque in light mode: a translucent dark disabled label washes out entirely.
            TextDisabled = dark ? p.WindowFg.WithAlpha(0.32f) : Color.Rgb(r: 150, g: 150, b: 154),
            Hint = p.DimLabel,
            Disabled = p.WindowFg.WithAlpha(0.32f),
            // separator { background: $border_color } — the same currentColor 15% as every border.
            Separator = p.Border,
            Border = p.Border,
            ViewportBackground = p.ViewBg,
            GraphBackground = p.WindowBg,
            OverlayBackground = p.Scrim,
            SelectionTint = accentBg.WithAlpha(0.25f),
            SelectionStrong = accentBg,

            // Type ramp (Adwaita body 11pt ≈ 14px, caption 9pt ≈ 12px).
            FontSizeBody = 14f,
            FontSizeCaption = 12f,
            FontSizeTitle = 17f,
            FontSizeH1 = 27f,

            // Shape.
            ButtonRadius = AdwMetrics.ControlRadius,
            InputRadius = AdwMetrics.ControlRadius,
            CardRadius = AdwMetrics.CardRadius,
            ToastRadius = AdwMetrics.Pill,
            // GNOME draws a 2px focus ring; ThemeData's default is the 3px macOS one.
            FocusRingWidth = 2f,

            // Neutral fill ladder, straight off the stylesheet's currentColor steps: raised
            // button (10%), flat hover (7%), view hover (4%), boxed-list row hover (3%), and one
            // step below that for anything wanting a hint of a surface.
            Fill1 = p.ButtonFill,
            Fill2 = p.HoverFill,
            Fill3 = p.ViewHoverFill,
            Fill4 = p.CardHoverFill,
            Fill5 = AdwPalette.Fill(dark: dark, percent: 0.02f),
            // Light-mode label ramps are pre-composited like the palette's foregrounds (see
            // AdwPalette.Light.WindowFg): translucent dark text renders washed out.
            Label1 = dark ? Color.Rgb(r: 255, g: 255, b: 255) : Color.Rgb(r: 38, g: 38, b: 43),
            Label2 = p.DimLabel,
            Label3 = dark
                ? Color.Rgba(
                    r: 255,
                    g: 255,
                    b: 255,
                    a: 0.32f
                )
                : Color.Rgb(r: 150, g: 150, b: 154), // alpha(#000006, .40) over #fafafb
            // Pre-composited for the same reason as the ramp above: a translucent dark value goes
            // through the renderer's linear blend and lands lighter than the CSS equivalent.
            Label4 = dark
                ? Color.Rgba(
                    r: 255,
                    g: 255,
                    b: 255,
                    a: 0.15f
                )
                : Color.Rgb(r: 213, g: 213, b: 215), // alpha(#000006, .15) over #fafafb
        };
    }
}
