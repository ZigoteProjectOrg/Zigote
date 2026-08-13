namespace AdwaitaGallery;

/// <summary>
///     A Liquid Glass surface behind its child — the material for chrome that floats <i>over
///     pictures</i>, where the page's own palette says nothing about what the glass will land on.
///     So this is the over-media variant, and it adapts on two axes, the way Apple's does. The
///     theme picks the glass family: dark glass carrying light content in the dark theme, milky
///     light glass carrying ink in the light theme (content reads its colour from
///     <see cref="OnGlass" />). And the shader adapts to the picture: it compresses whatever is
///     behind the pane toward that family's luminance anchor per pixel, so a bright sky is dimmed
///     under dark glass and a black coat is lifted under light glass — a strong scrim exactly
///     where one is needed, clear lens where it is not. The lens itself (refraction, frost,
///     dispersion) is the engine's hardware glass; the gel response (hover lifts, press
///     compresses) matches what <see cref="Glass" /> gives the system controls.
/// </summary>
/// <remarks>
///     Not cheap, by design: every pane is its own render-pass break plus a full-scene backdrop
///     copy each frame, and any glass on screen turns partial repaint off. That is what makes a
///     page full of these a workload, not just a look — see <see cref="Pages.ImageGridPage" />,
///     which floats a dozen-plus of them over a scrolling feed on purpose.
/// </remarks>
internal sealed class LiquidPane : Widget
{
    /// <summary>Ink for content riding on light glass — near-black, like macOS glass labels.</summary>
    private static readonly Color Ink = new(
        r: 0.10f,
        g: 0.11f,
        b: 0.13f
    );

    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public Widget? Child { get; set; }

    /// <summary>Corner radius; the default turns any chip or bar into a capsule.</summary>
    public float Radius { get; set; } = AdwMetrics.Pill;

    /// <summary>
    ///     Scrim colour; the alpha is the strength (0 = clear lens, 1 = opaque paint). Null — the
    ///     default — follows the theme: a dark scrim under the dark theme's white content, a milky
    ///     white one under the light theme's ink. Set it only to force one family regardless of
    ///     theme; the adaptive anchor follows the scrim's own brightness either way.
    /// </summary>
    public Color? Tint { get; set; }

    /// <summary>
    ///     How hard the shader pulls the backdrop toward the scrim's luminance anchor (0 = the
    ///     lens shows the picture as-is, 1 = full legibility clamp). See
    ///     <see cref="PaintList.AddLiquidGlass" />.
    /// </summary>
    public float Adapt { get; set; } = 0.85f;

    /// <summary>Content colour for glass in the current theme — white on dark glass, ink on light.</summary>
    public static Color OnGlass(ThemeData theme) => theme.IsDark ? Color.White : Ink;

    /// <inheritdoc cref="OnGlass" />
    public static Color OnGlassMuted(ThemeData theme) => OnGlass(theme).WithAlpha(0.72f);

    /// <summary>Soft float shadow size; leave 0 for glass riding inline on a picture.</summary>
    public float Elevation { get; set; }

    /// <summary>Gel inputs — driven by the <see cref="Pressable" /> that owns the pane.</summary>
    public bool Hovered { get; set; }

    /// <inheritdoc cref="Hovered" />
    public bool Pressed { get; set; }

    // The float shadow paints beyond Bounds. A frame with glass on screen is a full redraw anyway,
    // but the damage contract stays honest for the day that changes.
    public override Rect DamageBounds =>
        Elevation > 0f ? Bounds.Inflate(Elevation * 2.4f) : Bounds;

    public override Size Measure(Constraints c)
    {
        // Read here, not in a Build — like every control, and like the theme provider expects
        // ("controls read the theme in Measure").
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = Child?.Measure(c) ?? c.Constrain(Size.Zero);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        if (Elevation > 0f)
        {
            paint.AddShadow(
                bounds: Bounds,
                color: Color.Rgba(
                    r: 0,
                    g: 0,
                    b: 0,
                    a: 0.22f
                ),
                borderRadius: Radius,
                blurRadius: Elevation * 2.2f,
                spread: Elevation * 0.12f
            );
        }

        // The shader's optical model is a flat, CLEAR middle with a bevel that lenses at the rim
        // — but the bevel is `thickness` px wide, so on a capsule smaller than ~4× the theme's
        // slab thickness the bevel would swallow the whole surface and everything reads as
        // frosted rim: a milky pill, not glass. Scale the bevel to the shape so a chip keeps a
        // clear centre the picture shows through, the way Apple's does.
        float side = MathF.Min(x: Bounds.Width, y: Bounds.Height);
        float bevel = MathF.Min(x: _theme.GlassThickness, y: side * 0.22f);

        // The theme picks the glass family; the scrim itself can stay light-handed because the
        // shader's adaptive anchor does the legibility work against the actual picture.
        Color tint = Tint ?? (_theme.IsDark
            ? new Color(
                r: 0f,
                g: 0f,
                b: 0f,
                a: 0.18f
            )
            : new Color(
                r: 1f,
                g: 1f,
                b: 1f,
                a: 0.26f
            ));
        bool darkGlass = tint.R * 0.299f + tint.G * 0.587f + tint.B * 0.114f < 0.5f;

        // The gel: hover thickens the lens a touch, press compresses it and squeezes the
        // refraction — the same response Glass.Surface gives the system controls.
        float thickness = bevel * (Pressed ? 0.72f : Hovered ? 1.12f : 1f);
        float pinch = _theme.GlassPinch * (Pressed ? 1.5f : 1f);
        float scrim = Math.Clamp(
            value: tint.A * (Pressed ? 1.3f : Hovered ? 1.1f : 1f),
            min: 0f,
            max: 1f
        );
        paint.AddLiquidGlass(
            bounds: Bounds,
            color: tint.WithAlpha(scrim),
            radius: Radius,
            thickness: thickness,
            glowX: _theme.GlassGlowX,
            glowY: _theme.GlassGlowY,
            pinch: pinch,
            adapt: darkGlass ? -Adapt : Adapt
        );

        // No drawn border: the rim light IS the shader's directional specular + fresnel. A flat
        // white outline on top of it is exactly what makes glass read as brushed aluminium.
        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
