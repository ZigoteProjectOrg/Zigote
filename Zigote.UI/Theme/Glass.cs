using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Theme;

/// <summary>
///     Liquid Glass surface painter — the default material for Zigote UI controls.
///     <para>
///         Renders a refractive glass plane (dynamic lensing of the backdrop) that floats above the
///         UI with a soft drop shadow and the shader's own rim light, and modulates thickness / tint /
///         glow for hover and the gel-like press response. Controls call <see cref="Surface" /> in
///         their <c>Paint</c> before drawing their content (text / icon) on top.
///     </para>
/// </summary>
public static class Glass
{
    /// <summary>Adaptive-luminance strength for system controls — gentle, so glass sitting on the
    ///     theme's own surfaces barely moves while glass floating over media still gets rescued.</summary>
    private const float AdaptStrength = 0.6f;

    /// <summary>
    ///     The adaptive anchor for a glass surface (see <see cref="PaintList.AddLiquidGlass" />).
    ///     Direction follows the content the glass carries: a strongly tinted pane keeps content
    ///     chosen against its tint (a deep accent means light content → anchor dark), while barely
    ///     tinted glass carries <see cref="ThemeData.OnSurface" /> content, so the theme decides.
    /// </summary>
    private static float AutoAdapt(ThemeData theme, Color tint, float tintStrength)
    {
        bool darkGlass = tintStrength >= 0.3f
            ? (tint.R * 0.299f) + (tint.G * 0.587f) + (tint.B * 0.114f) < 0.5f
            : theme.IsDark;
        return darkGlass ? -AdaptStrength : AdaptStrength;
    }

    /// <summary>Paint a Liquid Glass surface filling <paramref name="bounds" />.</summary>
    /// <param name="tintStrength">0 = clear glass, 1 = fully colour-tinted by <paramref name="tint" />.</param>
    /// <param name="elevation">Soft float shadow size; 0 disables it (for inline / nested glass).</param>
    /// <param name="adapt">
    ///     Adaptive-luminance override in [-1, 1] (negative = anchor the backdrop dark, positive =
    ///     light, 0 = off); null derives it from the tint and theme.
    /// </param>
    // What the OS's glass preferences push a scrim toward: Tinted glass is a matte of the
    // glass's own family at these strengths; Reduce transparency is all but opaque.
    private const float TintedDark = 0.45f;
    private const float TintedLight = 0.62f;
    private const float Opaque = 0.9f;
    private static readonly Color MatteDark = new(r: 0.11f, g: 0.11f, b: 0.13f);
    private static readonly Color MatteLight = new(r: 0.97f, g: 0.97f, b: 0.98f);

    /// <summary>
    ///     Fold <see cref="SystemGlass" /> into a pane's scrim. <paramref name="weight" /> is how far
    ///     the pane follows the Clear/Tinted setting — 1 for regular glass, 0 for clear glass over
    ///     media, which stays clear as the system's does. Reduce transparency applies to every pane.
    ///     The family comes from the sign of <paramref name="adapt" /> (negative = dark glass), the
    ///     theme when it is 0. A scrim already stronger than the target (an accent or latched pane)
    ///     is left alone.
    /// </summary>
    public static (Color Tint, float Adapt) ApplySystem(ThemeData theme, Color tint, float adapt,
        float weight = 1f)
    {
        bool reduce = SystemGlass.ReduceTransparency;
        float t = reduce ? 1f : SystemGlass.Tinted * Math.Clamp(value: weight, min: 0f, max: 1f);
        if (t <= 0f) return (tint, adapt);

        bool dark = adapt != 0f ? adapt < 0f : theme.IsDark;
        float target = reduce ? Opaque : dark ? TintedDark : TintedLight;
        if (tint.A >= target) return (tint, adapt);

        var matte = dark ? MatteDark : MatteLight;
        var colour = new Color(
            r: tint.R + ((matte.R - tint.R) * t),
            g: tint.G + ((matte.G - tint.G) * t),
            b: tint.B + ((matte.B - tint.B) * t),
            a: tint.A + ((target - tint.A) * t)
        );
        float strength = MathF.Abs(adapt) + ((1f - MathF.Abs(adapt)) * t);
        return (colour, dark ? -strength : strength);
    }

    public static void Surface(
        PaintList paint, Rect bounds, ThemeData theme,
        float radius, Color tint, float tintStrength,
        bool hovered = false, bool pressed = false, float elevation = 6f,
        float? adapt = null)
    {
        // Floating drop shadow — glass planes hover above whatever is behind them.
        if (elevation > 0f)
        {
            paint.AddShadow(
                bounds: bounds,
                color: new Color(
                    r: 0f,
                    g: 0f,
                    b: 0f,
                    a: 0.22f
                ),
                borderRadius: radius,
                blurRadius: elevation * 2.2f,
                spread: elevation * 0.12f,
                squircle: true
            );
        }

        // Gel response: press compresses the glass (thinner core, stronger lensing); hover lifts
        // the rim and tint slightly so controls feel alive under the pointer.
        float thickness = theme.GlassThickness * (pressed ? 0.72f : hovered ? 1.12f : 1f);
        float pinch = theme.GlassPinch * (pressed ? 1.5f : 1f);
        float a = Math.Clamp(
            value: tintStrength * (pressed ? 1.3f : hovered ? 1.1f : 1f),
            min: 0f,
            max: 1f
        );

        var (scrim, tone) = ApplySystem(
            theme: theme,
            tint: tint.WithAlpha(a),
            adapt: adapt ?? AutoAdapt(theme: theme, tint: tint, tintStrength: tintStrength)
        );
        paint.AddLiquidGlass(
            bounds: bounds,
            color: scrim,
            radius: radius,
            thickness: thickness,
            glowX: theme.GlassGlowX,
            glowY: theme.GlassGlowY,
            pinch: pinch,
            adapt: tone
        );

        // No drawn border: the rim light is the shader's own directional hairline, and a flat
        // outline over it reads as brushed metal.
    }

    /// <summary>Clear glass surface using the theme's default tint/strength.</summary>
    public static void Surface(
        PaintList paint, Rect bounds, ThemeData theme, float radius,
        bool hovered = false, bool pressed = false, float elevation = 6f)
    {
        Surface(
            paint: paint,
            bounds: bounds,
            theme: theme,
            radius: radius,
            tint: theme.GlassTint,
            tintStrength: theme.GlassTintStrength,
            hovered: hovered,
            pressed: pressed,
            elevation: elevation
        );
    }

    /// <summary>Accent-tinted glass (primary buttons, selected chips, focused fields).</summary>
    public static void Accent(
        PaintList paint, Rect bounds, ThemeData theme, float radius, Color accent,
        bool hovered = false, bool pressed = false, float elevation = 7f)
    {
        Surface(
            paint: paint,
            bounds: bounds,
            theme: theme,
            radius: radius,
            tint: accent,
            tintStrength: 0.55f,
            hovered: hovered,
            pressed: pressed,
            elevation: elevation
        );
    }

    /// <summary>
    ///     Glass keyed to a system vibrancy <see cref="Material" /> — thinner materials are
    ///     clearer (toolbars, capsules), thicker ones obscure more (sidebars, sheets).
    /// </summary>
    public static void OfMaterial(
        PaintList paint, Rect bounds, ThemeData theme, Material material, float radius,
        bool hovered = false, bool pressed = false, float elevation = 6f)
    {
        (float strength, float thickScale) = material switch {
            Material.UltraThin => (0.04f, 0.6f),
            Material.Thin => (0.06f, 0.8f),
            Material.Regular => (0.10f, 1.0f),
            Material.Thick => (0.18f, 1.3f),
            _ => (0.30f, 1.6f), // UltraThick
        };
        float saved = theme.GlassThickness;
        // Reuse Surface's gel logic with a material-scaled thickness via a tweaked tint/strength.
        if (elevation > 0f)
        {
            paint.AddShadow(
                bounds: bounds,
                color: new Color(
                    r: 0f,
                    g: 0f,
                    b: 0f,
                    a: 0.22f
                ),
                borderRadius: radius,
                blurRadius: elevation * 2.2f,
                spread: elevation * 0.12f,
                squircle: true
            );
        }

        float thickness = saved * thickScale * (pressed ? 0.72f : hovered ? 1.12f : 1f);
        float pinch = theme.GlassPinch * (pressed ? 1.5f : 1f);
        float a = Math.Clamp(
            value: strength * (pressed ? 1.3f : hovered ? 1.1f : 1f),
            min: 0f,
            max: 1f
        );
        var (scrim, tone) = ApplySystem(
            theme: theme,
            tint: theme.GlassTint.WithAlpha(a),
            adapt: AutoAdapt(theme: theme, tint: theme.GlassTint, tintStrength: strength)
        );
        paint.AddLiquidGlass(
            bounds: bounds,
            color: scrim,
            radius: radius,
            thickness: thickness,
            glowX: theme.GlassGlowX,
            glowY: theme.GlassGlowY,
            pinch: pinch,
            adapt: tone
        );
    }
}
