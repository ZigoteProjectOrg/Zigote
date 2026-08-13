using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Theme;

/// <summary>
///     Liquid Glass surface painter — the default material for Zigote UI controls.
///     <para>
///         Renders a refractive glass plane (dynamic lensing of the backdrop) that floats above the
///         UI with a soft drop shadow and a hairline rim highlight, and modulates thickness / tint /
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
            ? tint.R * 0.299f + tint.G * 0.587f + tint.B * 0.114f < 0.5f
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
                spread: elevation * 0.12f
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

        paint.AddLiquidGlass(
            bounds: bounds,
            color: tint.WithAlpha(a),
            radius: radius,
            thickness: thickness,
            glowX: theme.GlassGlowX,
            glowY: theme.GlassGlowY,
            pinch: pinch,
            adapt: adapt ?? AutoAdapt(theme: theme, tint: tint, tintStrength: tintStrength)
        );

        // Rim: a hairline that reads as the glass edge catching light, brighter when interactive.
        var rim = theme.OnSurface.WithAlpha(hovered || pressed ? 0.30f : 0.16f);
        paint.AddBorder(bounds: bounds, color: rim, radius: radius);
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
                spread: elevation * 0.12f
            );
        }

        float thickness = saved * thickScale * (pressed ? 0.72f : hovered ? 1.12f : 1f);
        float pinch = theme.GlassPinch * (pressed ? 1.5f : 1f);
        float a = Math.Clamp(
            value: strength * (pressed ? 1.3f : hovered ? 1.1f : 1f),
            min: 0f,
            max: 1f
        );
        paint.AddLiquidGlass(
            bounds: bounds,
            color: theme.GlassTint.WithAlpha(a),
            radius: radius,
            thickness: thickness,
            glowX: theme.GlassGlowX,
            glowY: theme.GlassGlowY,
            pinch: pinch,
            adapt: AutoAdapt(theme: theme, tint: theme.GlassTint, tintStrength: strength)
        );
        paint.AddBorder(
            bounds: bounds,
            color: theme.OnSurface.WithAlpha(hovered || pressed ? 0.30f : 0.16f),
            radius: radius
        );
    }
}
