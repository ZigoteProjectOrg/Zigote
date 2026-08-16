using Zigote.Core.Animation;

namespace Zigote.UI.Adwaita;

/// <summary>
///     A Liquid Glass surface behind its child — the material that floating chrome is made of.
///     <para>
///         Adwaita in structure, Apple in material: GNOME decides where things go — the sidebar,
///         the header bars, the preference rows, the toast — and the chrome that <i>floats over
///         content</i> is glass rather than another flat fill. That split is the whole design. Glass
///         is not a skin applied to everything; it is what a pane is made of when it hovers above
///         something else and has to stay legible over whatever that is.
///     </para>
///     <para>
///         Apple's rules this follows, in the order they matter:
///     </para>
///     <list type="bullet">
///         <item>
///             Glass belongs to the navigation layer, never the content layer. A player bar or a
///             floating toolbar is glass; the list scrolling underneath it is not, and is drawn on
///             an opaque surface so there is something to refract.
///         </item>
///         <item>
///             Glass never stacks on glass. A pane's children are drawn flat — a glass button on a
///             glass bar reads as a smudge, and doubles the cost for it.
///         </item>
///         <item>
///             Two variants: <see cref="Regular" /> for chrome over a known surface, and
///             <see cref="Clear" /> for chrome over media, which leans on the shader's adaptive
///             anchor instead of a heavier scrim.
///         </item>
///         <item>
///             Interactive glass is a gel: hover thickens the lens, press compresses it. Wire it
///             with <see cref="Interactive" /> rather than by hand.
///         </item>
///         <item>
///             Concentric corners: something inset by <c>p</c> inside a pane of radius <c>r</c>
///             gets radius <c>r - p</c>, so the curves stay parallel. See <see cref="Inner" />.
///         </item>
///         <item>
///             Content takes its colour from the glass, not from the page — <see cref="OnGlass" />.
///         </item>
///     </list>
///     <para>
///         The lens itself (refraction, frost, dispersion) is the engine's hardware glass; what is
///         here is the material policy. <see cref="Theme.Glass" /> is the same material applied by
///         the individual system controls, which is why the gel response matches.
///     </para>
/// </summary>
/// <remarks>
///     Not cheap, and deliberately so: each pane is a render-pass break plus a backdrop copy, and
///     any glass on screen disables partial repaint for the frame. That is what makes a page full
///     of these a workload rather than just a look, and the reason an app that uses them should
///     have a preference that turns them off.
/// </remarks>
public sealed class LiquidPane : Widget
{
    /// <summary>Ink for content riding on light glass — near-black, as macOS glass labels are.</summary>
    private static readonly Color Ink = new(
        r: 0.10f,
        g: 0.11f,
        b: 0.13f
    );

    /// <summary>
    ///     How hard the backdrop is pulled toward the glass's luminance anchor.
    ///     <para>
    ///         Low on purpose. This started near its maximum, reasoning that a strong clamp is what
    ///         keeps content legible over any picture — but a full clamp compresses everything
    ///         behind the pane toward one tone, and a pane whose interior is flatter than its
    ///         surroundings does not read as glass, it reads as a dark box someone drew a rim
    ///         around. Legibility is the scrim's job and the content colour's job; the lens should
    ///         mostly get out of the way, which is what makes it look like glass at all.
    ///     </para>
    /// </summary>
    private const float Legibility = 0.35f;

    /// <summary>
    ///     The clear variant's haze: barely there, because it floats on media the page is *about*,
    ///     so it lifts rather than shades.
    /// </summary>
    private static readonly Color ClearTint = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.10f
    );

    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public Widget? Child { get; set; }

    /// <summary>Corner radius. The default — <see cref="AdwMetrics.Pill" /> — makes a chip or a bar
    ///     a capsule; a pane carrying more than one row wants something nearer a card.</summary>
    public float Radius { get; set; } = AdwMetrics.Pill;

    /// <summary>
    ///     Scrim colour; its alpha is the strength (0 = a clear lens, 1 = opaque paint). Null — the
    ///     default — follows the theme: a light haze in <b>both</b> themes, milkier in the light
    ///     one. Set it only to force one family regardless of theme.
    /// </summary>
    public Color? Tint { get; set; }

    /// <summary>
    ///     How hard the shader pulls the backdrop toward the scrim's luminance anchor: 0 shows the
    ///     backdrop as it is, 1 is a full legibility clamp. This is what keeps a title readable
    ///     over artwork the pane cannot see, and it is why glass over media needs no heavy fill.
    /// </summary>
    public float Adapt { get; set; } = Legibility;

    /// <summary>
    ///     Bevel width as a fraction of the theme's slab thickness — the material's <i>weight</i>,
    ///     Apple's thin-to-thick vibrancy scale collapsed to the two variants that earn their keep.
    ///     Thin glass is clearer, so it is the default and what <see cref="Clear" /> uses; a
    ///     <see cref="Regular" /> pane carries a fuller rim.
    /// </summary>
    public float Bevel { get; set; } = 0.35f;

    /// <summary>Soft float shadow size; 0 for a pane riding inline on what is behind it.</summary>
    public float Elevation { get; set; }

    /// <summary>Gel inputs, driven by whatever <see cref="Pressable" /> owns the pane. Setting
    ///     one animates the lens toward its new state rather than snapping — a gel deforms, it
    ///     does not switch.</summary>
    public bool Hovered
    {
        get => _hovered;
        set
        {
            if (_hovered == value) return;
            _hovered = value;
            WakeGel();
        }
    }

    /// <inheritdoc cref="Hovered" />
    public bool Pressed
    {
        get => _pressed;
        set
        {
            if (_pressed == value) return;
            _pressed = value;
            WakeGel();
        }
    }

    private bool _hovered;
    private bool _pressed;

    // The gel's animated factors — what Paint actually uses. Each relaxes toward the target its
    // state pair dictates, so at rest the pane looks exactly as the unanimated version did.
    private float _gelThick = 1f;
    private float _gelPinch = 1f;
    private float _gelScrim = 1f;
    private Ticker? _ticker;

    /// <summary>Steady-state gel targets for the current state — the values the old snap used.</summary>
    private (float Thick, float Pinch, float Scrim) GelTarget => (
        Thick: _pressed ? 0.72f : _hovered ? 1.12f : 1f,
        Pinch: _pressed ? 1.5f : 1f,
        Scrim: _pressed ? 1.3f : _hovered ? 1.1f : 1f
    );

    protected override void OnMount()
    {
        // Mount-scoped, like every animated control: the ticker CreateTicker hands out is
        // disposed on unmount, so a re-attach rebinds instead of leaking one per attach cascade.
        _ticker = CreateTicker(TickGel);

        // A pane mounted mid-state starts there — animating from idle on attach would flash.
        (_gelThick, _gelPinch, _gelScrim) = GelTarget;
    }

    private void WakeGel()
    {
        if (_ticker is { } ticker && Mounted)
        {
            ticker.Start();
            return;
        }

        // Not live: snap. There is no frame to animate across, and Paint must still be right.
        (_gelThick, _gelPinch, _gelScrim) = GelTarget;
    }

    private void TickGel(float dt)
    {
        // A gel compresses on contact and relaxes on release: the press direction is brisk, the
        // way back is slower. Exponential approach — no spring, no overshoot.
        // ponytail: springless; add a small overshoot on release if the gel ever reads as stiff.
        float rate = _pressed ? 24f : 12f;
        (float thick, float pinch, float scrim) = GelTarget;
        _gelThick = Approach(value: _gelThick, target: thick, k: rate * dt);
        _gelPinch = Approach(value: _gelPinch, target: pinch, k: rate * dt);
        _gelScrim = Approach(value: _gelScrim, target: scrim, k: rate * dt);
        MarkNeedsPaint();

        if (MathF.Abs(_gelThick - thick) < 0.004f &&
            MathF.Abs(_gelPinch - pinch) < 0.004f &&
            MathF.Abs(_gelScrim - scrim) < 0.004f)
        {
            (_gelThick, _gelPinch, _gelScrim) = (thick, pinch, scrim);
            _ticker?.Stop();
        }
    }

    private static float Approach(float value, float target, float k) =>
        value + (target - value) * (1f - MathF.Exp(-k));

    /// <summary>
    ///     Chrome over the app's own surfaces — a player bar, a floating control cluster. A light
    ///     scrim and a fuller bevel, because the theme already knows roughly what is behind it.
    /// </summary>
    public static LiquidPane Regular(
        Widget child, float radius = AdwMetrics.CardRadius, float elevation = 6f) =>
        new() {
            Child = child,
            Radius = radius,
            Elevation = elevation,
            Bevel = 0.6f,
        };

    /// <summary>
    ///     Chrome over media, where the page's palette says nothing about what the glass lands on.
    ///     Barely any scrim and a thin bevel: the shader dims a bright sky and lifts a black coat
    ///     per pixel, which is a scrim exactly where one is needed and a clear lens everywhere else.
    /// </summary>
    public static LiquidPane Clear(
        Widget child, float radius = AdwMetrics.Pill, float elevation = 0f) =>
        new() {
            Child = child,
            Radius = radius,
            Elevation = elevation,
            Adapt = Legibility * 0.7f,
            Tint = ClearTint,
        };

    /// <summary>
    ///     Glass as a button: the returned <see cref="Pressable" /> drives the pane's gel, so the
    ///     lens thickens under the pointer and compresses on the press. Wrapping the pane and
    ///     copying two flags across by hand is the same thing, three times out of three — so it
    ///     lives here instead.
    /// </summary>
    public static Pressable Interactive(LiquidPane pane, Action onPressed, string? semantics = null)
    {
        var pressable = new Pressable {
            Child = pane,
            OnPressed = onPressed,
            // The focus ring has to trace the pane, but a capsule's radius is a sentinel rather
            // than a measurement — and the height it would be half of is not known until layout.
            FocusRadius = MathF.Min(x: pane.Radius, y: 20f),
            SemanticsLabel = semantics,
        };
        pressable.OnStateChanged = () =>
        {
            pane.Hovered = pressable.Hovered;
            pane.Pressed = pressable.Pressed;
        };
        return pressable;
    }

    /// <summary>
    ///     The radius for something inset by <paramref name="padding" /> inside a pane of
    ///     <paramref name="outer" /> radius, so the two curves stay concentric rather than one
    ///     cutting across the other. Apple's rule, and the difference between a nested control
    ///     looking placed and looking pasted on.
    /// </summary>
    public static float Inner(float outer, float padding) =>
        MathF.Max(x: 0f, y: MathF.Min(x: outer, y: AdwMetrics.Pill) - padding);

    /// <summary>Content colour for glass in this theme — white on dark glass, ink on light.</summary>
    public static Color OnGlass(ThemeData theme) => theme.IsDark ? Color.White : Ink;

    /// <inheritdoc cref="OnGlass" />
    public static Color OnGlassMuted(ThemeData theme) => OnGlass(theme).WithAlpha(0.72f);

    // The float shadow paints outside Bounds. A frame with glass on it is a full redraw anyway,
    // but the damage contract stays honest for the day that stops being true.
    public override Rect DamageBounds =>
        Elevation > 0f ? Bounds.Inflate(Elevation * 2.4f) : Bounds;

    public override Size Measure(Constraints c)
    {
        // Read here rather than in a Build, as every control does — the theme provider expects
        // controls to read the theme in Measure.
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

        // The shader's optical model is a clear middle inside a bevel that lenses at the rim — and
        // the bevel is `thickness` pixels wide. On anything shorter than about four times that the
        // bevel swallows the surface and the whole thing reads as frosted rim: a milky pill rather
        // than glass. Scaling the bevel to the shape as well as to the material is what keeps a
        // clear centre for the backdrop to show through, which is the entire effect.
        float side = MathF.Min(x: Bounds.Width, y: Bounds.Height);
        float bevel = MathF.Min(x: _theme.GlassThickness * Bevel, y: side * 0.22f);

        // A light haze in BOTH themes, which is the correction that made this look like glass.
        // Dark-mode glass is not a dark scrim: on Apple's dark surfaces the pane is visibly
        // *lighter* than what surrounds it, a milky lift with the backdrop showing through. Tinting
        // it black instead — the obvious reading of "dark theme" — makes the pane darker than its
        // own backdrop, and a hole in the page reads as a box with a rim round it no matter how the
        // rest is tuned. It also flips the shader's anchor: a light tint lifts what is behind the
        // glass, which is the lift you actually see in the reference designs.
        Color tint = Tint ?? (_theme.IsDark
            ? new Color(
                r: 1f,
                g: 1f,
                b: 1f,
                a: 0.14f
            )
            : new Color(
                r: 1f,
                g: 1f,
                b: 1f,
                a: 0.34f
            ));

        // The gel: hover thickens the lens a touch, press compresses it and squeezes the
        // refraction — the same response Glass.Surface gives the system controls, eased through
        // the animated factors above rather than snapped.
        float thickness = bevel * _gelThick;
        float pinch = _theme.GlassPinch * _gelPinch;
        float scrim = Math.Clamp(
            value: tint.A * _gelScrim,
            min: 0f,
            max: 1f
        );

        // The anchor follows the CONTENT, not the tint. What sits on this pane is OnGlass: white in
        // the dark theme, near-black in the light one — so over an arbitrary backdrop the shader
        // must pull toward whichever tone gives that ink contrast: dark under white content, light
        // under dark content. Deriving the sign from the tint's own luminance (the obvious
        // heuristic, and the first version of this) inverted exactly when it mattered most:
        // dark-mode glass is a *milky* tint, whose high luminance anchored the backdrop light —
        // lifting a bright album cover toward white directly underneath white text.
        paint.AddLiquidGlass(
            bounds: Bounds,
            color: tint.WithAlpha(scrim),
            radius: Radius,
            thickness: thickness,
            glowX: _theme.GlassGlowX,
            glowY: _theme.GlassGlowY,
            pinch: pinch,
            adapt: _theme.IsDark ? -Adapt : Adapt
        );

        // No drawn border. The rim light IS the shader's directional specular and fresnel; a flat
        // outline painted over it is exactly what makes glass read as brushed aluminium.
        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
