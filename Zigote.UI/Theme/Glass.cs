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
    /// <summary>Paint a Liquid Glass surface filling <paramref name="bounds" />.</summary>
    /// <param name="tintStrength">0 = clear glass, 1 = fully colour-tinted by <paramref name="tint" />.</param>
    /// <param name="elevation">Soft float shadow size; 0 disables it (for inline / nested glass).</param>
    public static void Surface(
        PaintList paint, Rect bounds, ThemeData theme,
        float radius, Color tint, float tintStrength,
        bool hovered = false, bool pressed = false, float elevation = 6f)
    {
        // Floating drop shadow — glass planes hover above whatever is behind them.
        if (elevation > 0f)
            paint.AddShadow(
                bounds,
                new Color(
                    0f,
                    0f,
                    0f,
                    0.22f
                ),
                radius,
                elevation * 2.2f,
                elevation * 0.12f
            );

        // Gel response: press compresses the glass (thinner core, stronger lensing); hover lifts
        // the rim and tint slightly so controls feel alive under the pointer.
        var thickness = theme.GlassThickness * (pressed ? 0.72f : hovered ? 1.12f : 1f);
        var pinch = theme.GlassPinch * (pressed ? 1.5f : 1f);
        var a = Math.Clamp(tintStrength * (pressed ? 1.3f : hovered ? 1.1f : 1f), 0f, 1f);

        paint.AddLiquidGlass(
            bounds,
            tint.WithAlpha(a),
            radius,
            thickness,
            theme.GlassGlowX,
            theme.GlassGlowY,
            pinch
        );

        // Rim: a hairline that reads as the glass edge catching light, brighter when interactive.
        var rim = theme.OnSurface.WithAlpha(hovered || pressed ? 0.30f : 0.16f);
        paint.AddBorder(bounds, rim, radius);
    }

    /// <summary>Clear glass surface using the theme's default tint/strength.</summary>
    public static void Surface(
        PaintList paint, Rect bounds, ThemeData theme, float radius,
        bool hovered = false, bool pressed = false, float elevation = 6f)
    {
        Surface(
            paint,
            bounds,
            theme,
            radius,
            theme.GlassTint,
            theme.GlassTintStrength,
            hovered,
            pressed,
            elevation
        );
    }

    /// <summary>Accent-tinted glass (primary buttons, selected chips, focused fields).</summary>
    public static void Accent(
        PaintList paint, Rect bounds, ThemeData theme, float radius, Color accent,
        bool hovered = false, bool pressed = false, float elevation = 7f)
    {
        Surface(
            paint,
            bounds,
            theme,
            radius,
            accent,
            0.55f,
            hovered,
            pressed,
            elevation
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
        var (strength, thickScale) = material switch {
            Material.UltraThin => (0.04f, 0.6f),
            Material.Thin => (0.06f, 0.8f),
            Material.Regular => (0.10f, 1.0f),
            Material.Thick => (0.18f, 1.3f),
            _ => (0.30f, 1.6f), // UltraThick
        };
        var saved = theme.GlassThickness;
        // Reuse Surface's gel logic with a material-scaled thickness via a tweaked tint/strength.
        if (elevation > 0f)
            paint.AddShadow(
                bounds,
                new Color(
                    0f,
                    0f,
                    0f,
                    0.22f
                ),
                radius,
                elevation * 2.2f,
                elevation * 0.12f
            );
        var thickness = saved * thickScale * (pressed ? 0.72f : hovered ? 1.12f : 1f);
        var pinch = theme.GlassPinch * (pressed ? 1.5f : 1f);
        var a = Math.Clamp(strength * (pressed ? 1.3f : hovered ? 1.1f : 1f), 0f, 1f);
        paint.AddLiquidGlass(
            bounds,
            theme.GlassTint.WithAlpha(a),
            radius,
            thickness,
            theme.GlassGlowX,
            theme.GlassGlowY,
            pinch
        );
        paint.AddBorder(
            bounds,
            theme.OnSurface.WithAlpha(hovered || pressed ? 0.30f : 0.16f),
            radius
        );
    }
}
