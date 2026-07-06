using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Theme;

/// <summary>A soft drop-shadow recipe: blur radius, downward offset, opacity and optional spread.</summary>
public readonly record struct ShadowStyle(float Blur, float OffsetY, float Alpha, float Spread = 0f)
{
    public bool IsNone => Alpha <= 0f || Blur <= 0f;
}

/// <summary>
///     Elevation tokens. macOS leans on very soft, low-opacity shadows for depth rather than heavy
///     drop shadows. Cards barely lift; menus and popovers float a little; dialogs sit highest.
///     Use <see cref="ElevationPaint.AddElevation" /> to paint one consistently.
/// </summary>
public static class Elevation
{
    public static readonly ShadowStyle None = new(0f, 0f, 0f);

    /// <summary>Resting surfaces — cards, raised rows.</summary>
    public static readonly ShadowStyle Z1 = new(6f, 1f, 0.10f);

    /// <summary>Transient chrome — menus, popovers, tooltips.</summary>
    public static readonly ShadowStyle Z2 = new(14f, 4f, 0.16f);

    /// <summary>Modal surfaces — dialogs, sheets.</summary>
    public static readonly ShadowStyle Z3 = new(28f, 10f, 0.24f);

    // Semantic aliases (match the design-system "Low / Medium / High" shadow scale).
    /// <summary>Resting lift — cards, raised rows. Alias of <see cref="Z1" />.</summary>
    public static readonly ShadowStyle Low = Z1;

    /// <summary>Floating chrome — menus, popovers, tooltips. Alias of <see cref="Z2" />.</summary>
    public static readonly ShadowStyle Medium = Z2;

    /// <summary>Modal surfaces — dialogs, sheets. Alias of <see cref="Z3" />.</summary>
    public static readonly ShadowStyle High = Z3;
}

/// <summary>Extension for emitting an <see cref="Elevation" /> shadow into a <see cref="PaintList" />.</summary>
public static class ElevationPaint
{
    /// <summary>
    ///     Paint a soft drop shadow under <paramref name="bounds" /> at the given corner radius. The
    ///     shadow is offset downward per the style, matching macOS's light-from-above convention.
    /// </summary>
    public static void AddElevation(this PaintList paint, Rect bounds, float radius,
        ShadowStyle style)
    {
        if (style.IsNone) return;
        var shifted = new Rect(
            bounds.X,
            bounds.Y + style.OffsetY,
            bounds.Width,
            bounds.Height
        );
        paint.AddShadow(
            shifted,
            new Color(
                0f,
                0f,
                0f,
                style.Alpha
            ),
            radius,
            style.Blur,
            style.Spread
        );
    }
}