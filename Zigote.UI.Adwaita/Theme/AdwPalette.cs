namespace Zigote.UI.Adwaita;

/// <summary>
///     The libadwaita 1.10 (GNOME 51) named-color palette, both appearances, as raw values.
///     Names mirror the UI named colors (window_bg_color, view_bg_color, headerbar_*, …) defined in
///     <c>src/stylesheet/_colors.scss</c>. <see cref="AdwTheme" /> maps these onto
///     <see cref="Zigote.UI.Theme.ThemeData" />; widgets that need an Adwaita color with no ThemeData
///     equivalent (headerbar shade, card shade, the state fills, …) read it here via
///     <see cref="For" />.
///     The state fills (<see cref="AdwColors.ButtonFill" /> and friends) are the stylesheet's
///     <c>color-mix(in srgb, currentColor N%, transparent)</c> ladders, resolved once per appearance
///     against that appearance's window background — see <see cref="Fill" />.
/// </summary>
public static class AdwPalette
{
    // The surface the translucent tokens below are resolved against, used as the reference
    // background for Overlay().
    private static readonly Color LightSurface = Color.Rgb(r: 250, g: 250, b: 251);
    private static readonly Color DarkSurface = Color.Rgb(r: 34, g: 34, b: 38);

    /// <summary>The near-black GNOME uses for light-mode overlays and text (<c>#000006</c>).</summary>
    private static readonly Color LightTint = Color.Rgb(r: 0, g: 0, b: 6);

    public static readonly AdwColors Light = Build(false);
    public static readonly AdwColors Dark = Build(true);

    public static AdwColors For(ThemeData theme) => theme.IsDark ? Dark : Light;

    /// <summary>
    ///     The stylesheet's alphas assume sRGB-space compositing, but the renderer decodes colours
    ///     to linear before blending (see <c>srgb_decode</c> in shape_shader_source.wgsl), so a
    ///     literal <c>alpha(black, .08)</c> lands nowhere near the GNOME result — in dark mode
    ///     white@.10 renders as #5e5e60 instead of #383839. This solves for the alpha that
    ///     reproduces the stylesheet's colour once blended in linear space over
    ///     <paramref name="over" />. Do not "simplify" the results back to the CSS numbers; the
    ///     pre-composited foregrounds below exist for the same reason.
    /// </summary>
    private static Color Overlay(Color tint, float alpha, Color over)
    {
        static float Lin(float c) => MathF.Pow(x: MathF.Max(x: c, y: 0f), y: 2.2f);

        // Per channel: solve (1-a')*bg_lin + a'*fg_lin = lin((1-a)*bg + a*fg).
        static float Solve(float fg, float bg, float a)
        {
            float fgL = Lin(fg), bgL = Lin(bg);
            if (MathF.Abs(fgL - bgL) < 1e-6f) return a;
            return (Lin(((1f - a) * bg) + (a * fg)) - bgL) / (fgL - bgL);
        }

        float solved = (Solve(fg: tint.R, bg: over.R, a: alpha)
                        + Solve(fg: tint.G, bg: over.G, a: alpha)
                        + Solve(fg: tint.B, bg: over.B, a: alpha)) / 3f;
        return tint.WithAlpha(Math.Clamp(value: solved, min: 0f, max: 1f));
    }

    /// <summary>
    ///     The stylesheet's
    ///     <c>
    ///         color-mix(in srgb, currentColor <paramref name="percent" />,
    ///         transparent)
    ///     </c>
    ///     — a translucent wash of the appearance's foreground, ready to paint over
    ///     <paramref name="over" /> (the appearance's window background when omitted). Every neutral
    ///     state fill in Adwaita 1.10 is one of these, which is why the ladders are identical in both
    ///     appearances: only currentColor flips.
    /// </summary>
    public static Color Fill(ThemeData theme, float percent, Color? over = null) => Fill(
        dark: theme.IsDark,
        percent: percent,
        over: over
    );

    /// <inheritdoc cref="Fill(ThemeData, float, Color?)" />
    public static Color Fill(bool dark, float percent, Color? over = null)
    {
        return Wash(
            tint: dark ? Color.White : LightTint,
            percent: percent,
            over: over ?? (dark ? DarkSurface : LightSurface)
        );
    }

    /// <summary>
    ///     <see cref="Fill(ThemeData, float, Color?)" /> for an arbitrary tint — the status washes,
    ///     where currentColor is a standalone red/green/amber rather than the window foreground.
    /// </summary>
    public static Color Wash(Color tint, float percent, Color over) =>
        Overlay(tint: tint, alpha: percent, over: over);

    /// <summary>
    ///     <c>color-mix(in srgb, <paramref name="a" /> <paramref name="t" />, <paramref name="b" />)</c>
    ///     for two opaque colours — a plain sRGB-space lerp, as CSS does it.
    /// </summary>
    public static Color Mix(Color a, Color b, float t)
    {
        return new Color(
            r: b.R + ((a.R - b.R) * t),
            g: b.G + ((a.G - b.G) * t),
            b: b.B + ((a.B - b.B) * t),
            a: b.A + ((a.A - b.A) * t)
        );
    }

    private static AdwColors Build(bool dark)
    {
        var surface = dark ? DarkSurface : LightSurface;
        var accentBg = AdwAccentColors.Bg(AdwAccent.Blue);
        var destructiveBg =
            dark
                ? Color.Rgb(r: 192, g: 28, b: 40)
                : Color.Rgb(r: 224, g: 27, b: 36); // red_4 / red_3
        var successBg =
            dark
                ? Color.Rgb(r: 38, g: 162, b: 105)
                : Color.Rgb(r: 46, g: 194, b: 126); // green_5 / green_4
        var warningBg =
            dark
                ? Color.Rgb(r: 205, g: 147, b: 9)
                : Color.Rgb(r: 229, g: 165, b: 10); // #cd9309 / yellow_5

        Color F(float percent) => Fill(dark: dark, percent: percent, over: surface);

        return new AdwColors {
            AccentBg = accentBg,
            AccentFg = Color.White,
            Accent = AdwAccentColors.Standalone(bg: accentBg, dark: dark),
            DestructiveBg = destructiveBg,
            DestructiveFg = Color.White,
            Destructive = AdwAccentColors.Standalone(bg: destructiveBg, dark: dark),
            SuccessBg = successBg,
            SuccessFg = Color.White,
            Success = AdwAccentColors.Standalone(bg: successBg, dark: dark),
            WarningBg = warningBg,
            // The one status foreground that is not white: warning_fg_color is rgb(0 0 0 / 80%).
            WarningFg = Color.Rgba(
                r: 0,
                g: 0,
                b: 0,
                a: 0.8f
            ),
            Warning = AdwAccentColors.Standalone(bg: warningBg, dark: dark),
            // error_* is destructive_* with a different name in the stylesheet (same red_3/red_4).
            ErrorBg = destructiveBg,
            ErrorFg = Color.White,
            Error = AdwAccentColors.Standalone(bg: destructiveBg, dark: dark),
            WindowBg =
                dark
                    ? Color.Rgb(r: 34, g: 34, b: 38)
                    : Color.Rgb(r: 250, g: 250, b: 251), // #222226 / #fafafb
            // The GNOME light foregrounds are alpha(black, 0.8) over their surface. Kept translucent
            // they come out visibly washed here — the renderer composites in linear space, where 80%
            // black over white lands near #7a7a7a instead of the #323237 CSS gives. These are those
            // colours already composited, so light-mode text is opaque and reads at full contrast.
            //
            // Held a little darker than the 0.8 the stylesheet specifies (0.85 here). At 0.8 the
            // composited result is correct on paper but reads washed on this renderer, and small text
            // — captions, subtitles, list metadata, of which this UI has a great deal — is where that
            // shows first.
            WindowFg =
                dark
                    ? Color.White
                    : Color.Rgb(r: 38, g: 38, b: 43), // alpha(#000006, .85) over #fafafb

            ViewBg = dark ? Color.Rgb(r: 29, g: 29, b: 32) : Color.White, // #1d1d20
            ViewFg = dark ? Color.White : Color.Rgb(r: 38, g: 38, b: 44), // …over #ffffff

            HeaderbarBg = dark ? Color.Rgb(r: 46, g: 46, b: 50) : Color.White, // #2e2e32
            HeaderbarFg = dark ? Color.White : Color.Rgb(r: 38, g: 38, b: 43),
            HeaderbarBackdrop =
                dark ? Color.Rgb(r: 34, g: 34, b: 38) : Color.Rgb(r: 250, g: 250, b: 251),
            // Dark shades are near-black over a dark surface — the linear/sRGB gap barely moves them,
            // so these stay as the stylesheet authored them.
            HeaderbarShade = dark ? Shade(0.36f) : Fill(dark: false, percent: 0.12f, over: surface),
            HeaderbarDarkerShade = dark
                ? Color.Rgba(
                    r: 0,
                    g: 0,
                    b: 12,
                    a: 0.90f
                )
                : Fill(dark: false, percent: 0.12f, over: surface),
            SidebarBg =
                dark
                    ? Color.Rgb(r: 46, g: 46, b: 50)
                    : Color.Rgb(r: 235, g: 235, b: 237), // #2e2e32 / #ebebed
            SidebarFg = dark ? Color.White : Color.Rgb(r: 38, g: 38, b: 43),
            SidebarBackdrop =
                dark ? Color.Rgb(r: 40, g: 40, b: 44) : Color.Rgb(r: 242, g: 242, b: 244),
            SidebarShade = dark ? Shade(0.25f) : Fill(dark: false, percent: 0.07f, over: surface),
            SidebarBorder = dark ? Shade(0.36f) : Fill(dark: false, percent: 0.07f, over: surface),
            SecondarySidebarBg =
                dark ? Color.Rgb(r: 40, g: 40, b: 44) : Color.Rgb(r: 243, g: 243, b: 245),
            SecondarySidebarFg = dark ? Color.White : Color.Rgb(r: 38, g: 38, b: 43),
            SecondarySidebarBackdrop =
                dark ? Color.Rgb(r: 37, g: 37, b: 41) : Color.Rgb(r: 246, g: 246, b: 250),
            SecondarySidebarShade =
                dark ? Shade(0.25f) : Fill(dark: false, percent: 0.07f, over: surface),
            SecondarySidebarBorder =
                dark ? Shade(0.36f) : Fill(dark: false, percent: 0.07f, over: surface),
            CardBg = dark ? Fill(dark: true, percent: 0.08f, over: surface) : Color.White,
            CardFg = dark ? Color.White : Color.Rgb(r: 38, g: 38, b: 43),
            CardShade = dark ? Shade(0.36f) : Fill(dark: false, percent: 0.07f, over: surface),
            DialogBg =
                dark
                    ? Color.Rgb(r: 54, g: 54, b: 58)
                    : Color.Rgb(r: 250, g: 250, b: 251), // #36363a
            DialogFg = dark ? Color.White : Color.Rgb(r: 38, g: 38, b: 43),
            PopoverBg = dark ? Color.Rgb(r: 54, g: 54, b: 58) : Color.White,
            PopoverFg = dark ? Color.White : Color.Rgb(r: 38, g: 38, b: 43),
            PopoverShade = dark ? Shade(0.25f) : Fill(dark: false, percent: 0.07f, over: surface),
            ThumbnailBg = dark ? Color.Rgb(r: 57, g: 57, b: 61) : Color.White, // #39393d
            ThumbnailFg = dark ? Color.White : Color.Rgb(r: 38, g: 38, b: 43),

            // The checked segment of a toggle group / inline view switcher.
            ActiveToggleBg = dark ? Fill(dark: true, percent: 0.20f, over: surface) : Color.White,
            ActiveToggleFg = dark ? Color.White : Color.Rgb(r: 38, g: 38, b: 43),
            OverviewBg = dark ? Color.Rgb(r: 40, g: 40, b: 44) : Color.Rgb(r: 243, g: 243, b: 245),
            OverviewFg = dark ? Color.White : Color.Rgb(r: 38, g: 38, b: 43),
            Shade = dark ? Shade(0.25f) : Fill(dark: false, percent: 0.07f, over: surface),

            // ── State fills: currentColor N%, straight off the stylesheet ──────────────
            // Raised buttons, entries, window controls, toggle-group backgrounds (_buttons.scss).
            ButtonFill = F(0.10f),
            ButtonFillHover = F(0.15f),
            ButtonFillActive = F(0.30f),
            ButtonFillChecked = F(0.30f),
            ButtonFillCheckedHover = F(0.35f),
            ButtonFillCheckedActive = F(0.40f),
            // Flat buttons, menu items, nav-sidebar rows, tabs (_colors.scss $hover_color …).
            HoverFill = F(0.07f),
            ActiveFill = F(0.16f),
            SelectedFill = F(0.10f),
            SelectedFillHover = F(0.13f),
            SelectedFillActive = F(0.19f),
            // Rows inside a .view (list views, flow boxes, bottom-sheet buttons).
            ViewHoverFill = F(0.04f),
            ViewActiveFill = F(0.08f),
            // Boxed-list rows and .card.activatable — deliberately fainter than everything else.
            CardHoverFill = F(0.03f),
            CardActiveFill = F(0.08f),
            // Switch/scale/check troughs.
            TroughFill = F(0.15f),
            TroughFillHover = F(0.20f),
            TroughFillActive = F(0.25f),

            // Adwaita's dim-label is 0.55, which composites to #717174 — about 4.5:1 on the window
            // background, i.e. exactly the AA floor for normal text and under it for the 12px captions
            // this UI leans on. 0.65 gives ~6.7:1 and still reads as clearly secondary.
            DimLabel = dark
                ? Color.Rgba(
                    r: 255,
                    g: 255,
                    b: 255,
                    a: 0.55f
                )
                : Color.Rgb(r: 88, g: 88, b: 92), // alpha(#000006, .65) over #fafafb
            Border = F(0.15f),
            // The scrim is a full-screen wash with no single reference surface; left as authored.
            // AdwDialog/AdwBottomSheet double the shade colour's alpha for their dimming layer.
            Scrim = dark
                ? Shade(0.50f)
                : Color.Rgba(
                    r: 0,
                    g: 0,
                    b: 6,
                    a: 0.14f
                ),
            // window.csd outline: white 7%, over whatever the window's own background is.
            WindowOutline = Color.Rgba(
                r: 255,
                g: 255,
                b: 255,
                a: 0.07f
            ),
        };

        static Color Shade(float alpha) => Color.Rgba(
            r: 0,
            g: 0,
            b: 6,
            a: alpha
        );
    }
}

/// <summary>One appearance's worth of Adwaita named colors. See <see cref="AdwPalette" />.</summary>
public sealed class AdwColors
{
    public Color AccentBg { get; init; }
    public Color AccentFg { get; init; }
    public Color Accent { get; init; }
    public Color DestructiveBg { get; init; }
    public Color DestructiveFg { get; init; }
    public Color Destructive { get; init; }
    public Color SuccessBg { get; init; }
    public Color SuccessFg { get; init; }
    public Color Success { get; init; }
    public Color WarningBg { get; init; }
    public Color WarningFg { get; init; }
    public Color Warning { get; init; }
    public Color ErrorBg { get; init; }
    public Color ErrorFg { get; init; }
    public Color Error { get; init; }

    public Color WindowBg { get; init; }
    public Color WindowFg { get; init; }
    public Color ViewBg { get; init; }
    public Color ViewFg { get; init; }

    public Color HeaderbarBg { get; init; }
    public Color HeaderbarFg { get; init; }
    public Color HeaderbarBackdrop { get; init; }
    public Color HeaderbarShade { get; init; }
    public Color HeaderbarDarkerShade { get; init; }

    public Color SidebarBg { get; init; }
    public Color SidebarFg { get; init; }
    public Color SidebarBackdrop { get; init; }
    public Color SidebarShade { get; init; }
    public Color SidebarBorder { get; init; }

    public Color SecondarySidebarBg { get; init; }
    public Color SecondarySidebarFg { get; init; }
    public Color SecondarySidebarBackdrop { get; init; }
    public Color SecondarySidebarShade { get; init; }
    public Color SecondarySidebarBorder { get; init; }

    public Color CardBg { get; init; }
    public Color CardFg { get; init; }
    public Color CardShade { get; init; }
    public Color DialogBg { get; init; }
    public Color DialogFg { get; init; }
    public Color PopoverBg { get; init; }
    public Color PopoverFg { get; init; }
    public Color PopoverShade { get; init; }
    public Color ThumbnailBg { get; init; }
    public Color ThumbnailFg { get; init; }
    public Color ActiveToggleBg { get; init; }
    public Color ActiveToggleFg { get; init; }
    public Color OverviewBg { get; init; }
    public Color OverviewFg { get; init; }
    public Color Shade { get; init; }

    /// <summary>Raised button fill — <c>currentColor 10%</c>. Also entries and window controls.</summary>
    public Color ButtonFill { get; init; }

    public Color ButtonFillHover { get; init; }
    public Color ButtonFillActive { get; init; }
    public Color ButtonFillChecked { get; init; }
    public Color ButtonFillCheckedHover { get; init; }
    public Color ButtonFillCheckedActive { get; init; }

    /// <summary>Flat/menu hover — <c>currentColor 7%</c>.</summary>
    public Color HoverFill { get; init; }

    public Color ActiveFill { get; init; }
    public Color SelectedFill { get; init; }
    public Color SelectedFillHover { get; init; }
    public Color SelectedFillActive { get; init; }

    /// <summary>List-view row hover — <c>currentColor 4%</c>.</summary>
    public Color ViewHoverFill { get; init; }

    public Color ViewActiveFill { get; init; }

    /// <summary>Boxed-list row hover — <c>currentColor 3%</c>.</summary>
    public Color CardHoverFill { get; init; }

    public Color CardActiveFill { get; init; }

    /// <summary>Switch / scale / check trough — <c>currentColor 15%</c>.</summary>
    public Color TroughFill { get; init; }

    public Color TroughFillHover { get; init; }
    public Color TroughFillActive { get; init; }

    public Color DimLabel { get; init; }
    public Color Border { get; init; }
    public Color Scrim { get; init; }
    public Color WindowOutline { get; init; }
}

/// <summary>The nine libadwaita system accent hues (AdwAccentColor).</summary>
public enum AdwAccent
{
    Blue,
    Teal,
    Green,
    Yellow,
    Orange,
    Red,
    Pink,
    Purple,
    Slate,
}

public static class AdwAccentColors
{
    /// <summary>The accent background color for a hue (same in both appearances).</summary>
    public static Color Bg(AdwAccent accent)
    {
        return accent switch {
            AdwAccent.Teal => Color.Rgb(r: 33, g: 144, b: 164), // #2190a4
            AdwAccent.Green => Color.Rgb(r: 58, g: 148, b: 74), // #3a944a
            AdwAccent.Yellow => Color.Rgb(r: 200, g: 136, b: 0), // #c88800
            AdwAccent.Orange => Color.Rgb(r: 237, g: 91, b: 0), // #ed5b00
            AdwAccent.Red => Color.Rgb(r: 230, g: 45, b: 66), // #e62d42
            AdwAccent.Pink => Color.Rgb(r: 213, g: 97, b: 153), // #d56199
            AdwAccent.Purple => Color.Rgb(r: 145, g: 65, b: 172), // #9141ac
            AdwAccent.Slate => Color.Rgb(r: 111, g: 131, b: 150), // #6f8396
            _ => Color.Rgb(r: 53, g: 132, b: 228), // blue #3584e4
        };
    }

    /// <summary>
    ///     The named hue nearest an sRGB triple, by squared distance. This is how a raw accent color
    ///     — the form <c>org.freedesktop.portal.Settings</c> hands out, since not every desktop names
    ///     its accents — maps back onto libadwaita's nine. Returns null for the portal's "no accent
    ///     set" answer, which reports negative components; the caller keeps whatever it had.
    /// </summary>
    public static AdwAccent? Nearest(float r, float g, float b)
    {
        if (r < 0 || g < 0 || b < 0) return null;

        var best = AdwAccent.Blue;
        float bestDistance = float.MaxValue;
        foreach (var candidate in Enum.GetValues<AdwAccent>())
        {
            var c = Bg(candidate);
            float d = Sq(c.R - r) + Sq(c.G - g) + Sq(c.B - b);
            if (d >= bestDistance) continue;
            bestDistance = d;
            best = candidate;
        }

        return best;

        static float Sq(float x) => x * x;
    }

    /// <inheritdoc cref="Standalone(Color, bool)" />
    public static Color Standalone(AdwAccent accent, bool dark) =>
        Standalone(bg: Bg(accent), dark: dark);

    /// <summary>
    ///     Standalone (on-surface) variant of a status or accent colour — link text, alert response
    ///     labels, menu check marks, the <c>.error</c>/<c>.warning</c>/<c>.success</c> label classes:
    ///     anywhere the hue sits on the window background rather than behind white text.
    ///     This is the stylesheet's rule verbatim — <c>oklab(from @accent_bg_color min(l, 0.5) a b)</c>
    ///     in light, <c>max(l, 0.85)</c> in dark — so the nine hues and the four status colours all
    ///     land on libadwaita's published values (blue → #0461be / #81d0ff, destructive → #c30000 /
    ///     #ff938c, and so on). Out-of-gamut results are clipped per channel rather than gamut-mapped;
    ///     none of the palette hues reach that far.
    /// </summary>
    public static Color Standalone(Color bg, bool dark)
    {
        (float l, float a, float b) = Oklab.FromSrgb(bg);
        return Oklab.ToSrgb(
            L: dark ? MathF.Max(x: l, y: 0.85f) : MathF.Min(x: l, y: 0.5f),
            A: a,
            B: b
        );
    }
}

/// <summary>
///     Björn Ottosson's Oklab, the space libadwaita derives its standalone colours in. sRGB in and
///     out, gamma 2.4 transfer as CSS specifies (not the renderer's 2.2 approximation — this is
///     colour maths on authored values, not compositing).
/// </summary>
internal static class Oklab
{
    public static (float L, float A, float B) FromSrgb(Color c)
    {
        float r = Lin(c.R), g = Lin(c.G), b = Lin(c.B);

        float l = MathF.Cbrt((0.4122214708f * r) + (0.5363325363f * g) + (0.0514459929f * b));
        float m = MathF.Cbrt((0.2119034982f * r) + (0.6806995451f * g) + (0.1073969566f * b));
        float s = MathF.Cbrt((0.0883024619f * r) + (0.2817188376f * g) + (0.6299787005f * b));

        return ((0.2104542553f * l) + (0.7936177850f * m) - (0.0040720468f * s),
            (1.9779984951f * l) - (2.4285922050f * m) + (0.4505937099f * s),
            (0.0259040371f * l) + (0.7827717662f * m) - (0.8086757660f * s));

        static float Lin(float v) =>
            v <= 0.04045f ? v / 12.92f : MathF.Pow(x: (v + 0.055f) / 1.055f, y: 2.4f);
    }

    public static Color ToSrgb(float L, float A, float B)
    {
        float l = Cube(L + (0.3963377774f * A) + (0.2158037573f * B));
        float m = Cube(L - (0.1055613458f * A) - (0.0638541728f * B));
        float s = Cube(L - (0.0894841775f * A) - (1.2914855480f * B));

        return new Color(
            r: Srgb((4.0767416621f * l) - (3.3077115913f * m) + (0.2309699292f * s)),
            g: Srgb((-1.2684380046f * l) + (2.6097574011f * m) - (0.3413193965f * s)),
            b: Srgb((-0.0041960863f * l) - (0.7034186147f * m) + (1.7076147010f * s))
        );

        static float Cube(float v) => v * v * v;

        static float Srgb(float v)
        {
            float s = v <= 0.0031308f
                ? v * 12.92f
                : (1.055f * MathF.Pow(x: MathF.Max(x: v, y: 0f), y: 1f / 2.4f)) - 0.055f;
            return Math.Clamp(value: s, min: 0f, max: 1f);
        }
    }
}
