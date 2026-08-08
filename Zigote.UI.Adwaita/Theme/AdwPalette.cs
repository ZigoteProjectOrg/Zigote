namespace Zigote.UI.Adwaita;

/// <summary>
///     The libadwaita 1.9 named-color palette, both appearances, as raw values.
///     Names mirror the UI named colors (window_bg_color, view_bg_color, headerbar_*, …) from
///     https://gnome.pages.gitlab.gnome.org/libadwaita/doc/main/named-colors.html.
///     <see cref="AdwTheme" /> maps these onto <see cref="Zigote.UI.Theme.ThemeData" />; widgets that
///     need an Adwaita color with no ThemeData equivalent (headerbar shade, card shade, …) read it
///     here via <see cref="For" />.
/// </summary>
public static class AdwPalette
{
    // The two surfaces the translucent tokens below actually sit on, used as the reference
    // background for Overlay().
    private static readonly Color LightSurface = Color.Rgb(250, 250, 251);
    private static readonly Color DarkSurface = Color.Rgb(34, 34, 38);

    public static AdwColors For(ThemeData theme)
    {
        return theme.IsDark ? Dark : Light;
    }

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
        static float Lin(float c)
        {
            return MathF.Pow(MathF.Max(c, 0f), 2.2f);
        }

        // Per channel: solve (1-a')*bg_lin + a'*fg_lin = lin((1-a)*bg + a*fg).
        static float Solve(float fg, float bg, float a)
        {
            float fgL = Lin(fg), bgL = Lin(bg);
            if (MathF.Abs(fgL - bgL) < 1e-6f) return a;
            return (Lin((1f - a) * bg + a * fg) - bgL) / (fgL - bgL);
        }

        var solved = (Solve(tint.R, over.R, alpha)
                      + Solve(tint.G, over.G, alpha)
                      + Solve(tint.B, over.B, alpha)) / 3f;
        return tint.WithAlpha(Math.Clamp(solved, 0f, 1f));
    }

    /// <summary>The near-black GNOME uses for light-mode overlays (<c>#000006</c>).</summary>
    private static readonly Color LightTint = Color.Rgb(0, 0, 6);

    private static Color OnLight(float alpha)
    {
        return Overlay(LightTint, alpha, LightSurface);
    }

    private static Color OnDark(float alpha)
    {
        return Overlay(Color.White, alpha, DarkSurface);
    }

    public static readonly AdwColors Light = new() {
        AccentBg = Color.Rgb(53, 132, 228), // #3584e4
        AccentFg = Color.Rgb(255, 255, 255),
        Accent = Color.Rgb(4, 97, 190), // standalone #0461be
        DestructiveBg = Color.Rgb(224, 27, 36), // #e01b24
        Destructive = Color.Rgb(195, 0, 0), // #c30000
        SuccessBg = Color.Rgb(46, 194, 126), // #2ec27e
        Success = Color.Rgb(0, 124, 61), // #007c3d
        WarningBg = Color.Rgb(229, 165, 10), // #e5a50a
        Warning = Color.Rgb(144, 84, 0), // #905400
        WindowBg = Color.Rgb(250, 250, 251), // #fafafb
        // The GNOME light foregrounds are alpha(black, 0.8) over their surface. Kept translucent
        // they come out visibly washed here — the renderer composites in linear space, where 80%
        // black over white lands near #7a7a7a instead of the #323237 CSS gives. These are those
        // colours already composited, so light-mode text is opaque and reads at full contrast.
        //
        // Held a little darker than the 0.8 the stylesheet specifies (0.85 here). At 0.8 the
        // composited result is correct on paper but reads washed on this renderer, and small text
        // — captions, subtitles, list metadata, of which this UI has a great deal — is where that
        // shows first.
        WindowFg = Color.Rgb(38, 38, 43), // alpha(#000006, .85) over #fafafb
        ViewBg = Color.Rgb(255, 255, 255),
        ViewFg = Color.Rgb(38, 38, 44), // …over #ffffff
        HeaderbarBg = Color.Rgb(255, 255, 255),
        HeaderbarShade = OnLight(0.12f),
        SidebarBg = Color.Rgb(235, 235, 237), // #ebebed
        SidebarShade = OnLight(0.07f),
        CardBg = Color.Rgb(255, 255, 255),
        CardShade = OnLight(0.07f),
        DialogBg = Color.Rgb(250, 250, 251),
        PopoverBg = Color.Rgb(255, 255, 255),
        // Neutral control fill ladder (button bg / :hover / :active / disabled).
        ButtonFill = OnLight(0.08f),
        ButtonFillHover = OnLight(0.12f),
        ButtonFillActive = OnLight(0.16f),
        ButtonFillDisabled = OnLight(0.04f),
        // Adwaita's dim-label is 0.55, which composites to #717174 — about 4.5:1 on the window
        // background, i.e. exactly the AA floor for normal text and under it for the 12px captions
        // this UI leans on. 0.65 gives ~6.7:1 and still reads as clearly secondary.
        DimLabel = Color.Rgb(88, 88, 92), // alpha(#000006, .65) over #fafafb
        Border = OnLight(0.15f),
        // The scrim is a full-screen wash with no single reference surface; left as authored.
        Scrim = Color.Rgba(
            0,
            0,
            6,
            0.32f
        ),
    };

    public static readonly AdwColors Dark = new() {
        AccentBg = Color.Rgb(53, 132, 228), // #3584e4
        AccentFg = Color.Rgb(255, 255, 255),
        Accent = Color.Rgb(129, 208, 255), // standalone #81d0ff
        DestructiveBg = Color.Rgb(192, 28, 40), // #c01c28
        Destructive = Color.Rgb(255, 147, 140), // #ff938c
        SuccessBg = Color.Rgb(38, 162, 105), // #26a269
        Success = Color.Rgb(120, 233, 171), // #78e9ab
        WarningBg = Color.Rgb(205, 147, 9), // #cd9309
        Warning = Color.Rgb(255, 194, 82), // #ffc252
        WindowBg = Color.Rgb(34, 34, 38), // #222226
        WindowFg = Color.Rgb(255, 255, 255),
        ViewBg = Color.Rgb(29, 29, 32), // #1d1d20
        ViewFg = Color.Rgb(255, 255, 255),
        HeaderbarBg = Color.Rgb(46, 46, 50), // #2e2e32
        // Dark shades are near-black over a dark surface — the linear/sRGB gap barely moves them,
        // so these stay as the stylesheet authored them.
        HeaderbarShade = Color.Rgba(
            0,
            0,
            6,
            0.36f
        ),
        SidebarBg = Color.Rgb(46, 46, 50),
        SidebarShade = Color.Rgba(
            0,
            0,
            6,
            0.36f
        ),
        CardBg = OnDark(0.08f),
        CardShade = Color.Rgba(
            0,
            0,
            6,
            0.36f
        ),
        DialogBg = Color.Rgb(54, 54, 58), // #36363a
        PopoverBg = Color.Rgb(54, 54, 58),
        ButtonFill = OnDark(0.10f),
        ButtonFillHover = OnDark(0.15f),
        ButtonFillActive = OnDark(0.20f),
        ButtonFillDisabled = OnDark(0.05f),
        DimLabel = Color.Rgba(
            255,
            255,
            255,
            0.55f
        ),
        Border = OnDark(0.15f),
        Scrim = Color.Rgba(
            0,
            0,
            6,
            0.5f
        ),
    };
}

/// <summary>One appearance's worth of Adwaita named colors. See <see cref="AdwPalette" />.</summary>
public sealed class AdwColors
{
    public Color AccentBg { get; init; }
    public Color AccentFg { get; init; }
    public Color Accent { get; init; }
    public Color DestructiveBg { get; init; }
    public Color Destructive { get; init; }
    public Color SuccessBg { get; init; }
    public Color Success { get; init; }
    public Color WarningBg { get; init; }
    public Color Warning { get; init; }
    public Color WindowBg { get; init; }
    public Color WindowFg { get; init; }
    public Color ViewBg { get; init; }
    public Color ViewFg { get; init; }
    public Color HeaderbarBg { get; init; }
    public Color HeaderbarShade { get; init; }
    public Color SidebarBg { get; init; }
    public Color SidebarShade { get; init; }
    public Color CardBg { get; init; }
    public Color CardShade { get; init; }
    public Color DialogBg { get; init; }
    public Color PopoverBg { get; init; }
    public Color ButtonFill { get; init; }
    public Color ButtonFillHover { get; init; }
    public Color ButtonFillActive { get; init; }
    public Color ButtonFillDisabled { get; init; }
    public Color DimLabel { get; init; }
    public Color Border { get; init; }
    public Color Scrim { get; init; }
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
            AdwAccent.Teal => Color.Rgb(33, 144, 164), // #2190a4
            AdwAccent.Green => Color.Rgb(58, 148, 74), // #3a944a
            AdwAccent.Yellow => Color.Rgb(200, 136, 0), // #c88800
            AdwAccent.Orange => Color.Rgb(237, 91, 0), // #ed5b00
            AdwAccent.Red => Color.Rgb(230, 45, 66), // #e62d42
            AdwAccent.Pink => Color.Rgb(213, 97, 153), // #d56199
            AdwAccent.Purple => Color.Rgb(145, 65, 172), // #9141ac
            AdwAccent.Slate => Color.Rgb(111, 131, 150), // #6f8396
            _ => Color.Rgb(53, 132, 228), // blue #3584e4
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
        var bestDistance = float.MaxValue;
        foreach (var candidate in Enum.GetValues<AdwAccent>())
        {
            var c = Bg(candidate);
            var d = Sq(c.R - r) + Sq(c.G - g) + Sq(c.B - b);
            if (d >= bestDistance) continue;
            bestDistance = d;
            best = candidate;
        }

        return best;

        static float Sq(float x)
        {
            return x * x;
        }
    }

    /// <summary>
    ///     Standalone (on-surface) variant of an accent hue — the colour used for link text, alert
    ///     response labels and menu check marks, where the accent sits on the window background
    ///     rather than behind white text.
    ///     libadwaita hand-tunes these in Oklab. Rather than carry an 18-entry table, this walks the
    ///     hue toward the far end until it clears WCAG AA (4.5:1) against the surface it will be
    ///     drawn on — a flat Darken(0.18f) left yellow at 4.35:1, i.e. below AA.
    ///     ponytail: contrast-driven, not hue-preserving like Oklab; swap in the exact table if a
    ///     designer objects to a specific hue.
    /// </summary>
    public static Color Standalone(AdwAccent accent, bool dark)
    {
        var surface = dark ? Color.Rgb(34, 34, 38) : Color.Rgb(250, 250, 251);
        var c = Bg(accent);
        for (var i = 0; i < 24 && Contrast(c, surface) < 4.5f; i++)
            c = dark ? c.Lighten(0.05f) : c.Darken(0.05f);
        return c;
    }

    /// <summary>WCAG relative-luminance contrast ratio between two opaque colours.</summary>
    private static float Contrast(Color a, Color b)
    {
        static float L(Color c)
        {
            static float Ch(float v)
            {
                return v <= 0.03928f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
            }

            return 0.2126f * Ch(c.R) + 0.7152f * Ch(c.G) + 0.0722f * Ch(c.B);
        }

        float la = L(a), lb = L(b);
        return (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
    }
}