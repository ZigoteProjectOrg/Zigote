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
///             Two variants: <see cref="Regular" /> for adaptive chrome over any content, and
///             <see cref="Clear" /> for chrome over dimmed media, without adaptive darkening.
///             <see cref="Tinted" /> is the dial between them,
///             so a control can blend a pane from one to the other live, the way the system's own
///             settings do.
///         </item>
///         <item>
///             Glass is captured once per cluster, not once per pane. The panes of a toolbar, a
///             control centre, a lock screen go in one <see cref="LiquidGroup" />, which lays every
///             lens down before any of their content — one backdrop copy and one render-pass break
///             for the lot.
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
///     Not cheap, and deliberately so: every run of lenses is a render-pass break plus a backdrop
///     copy — one per pane left on its own, one per <see cref="LiquidGroup" /> — and a lens never
///     repaints a patch of itself: a change anywhere under one redraws the whole lens and captures
///     again, so a ticking label inside a glass bar costs a bar-sized redraw. That is what makes a
///     page full of these a workload rather than just a look, and the reason an app that uses them
///     should have a preference that turns them off.
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
    ///     Regular glass compresses backdrop contrast for legibility. Clear glass disables this
    ///     adaptation; its caller supplies dimming beneath the material and bright foreground ink.
    /// </summary>
    private const float Legibility = 0.65f;

    /// <summary>
    ///     The clear variant's haze: barely there, because it floats on media the page is *about*,
    ///     so it lifts rather than shades. Also the floor <see cref="Tinted" /> blends up from.
    /// </summary>
    private const float ClearHaze = 0.035f;

    /// <summary>
    ///     The two rims, as fractions of the theme's slab thickness. The shader's bevel is a
    ///     squircle section — flat nearly to the edge, bending only in its outer third — so these
    ///     are the lens's full run-in, not the width of the visible distortion. At the theme's
    ///     10px slab they are 6 and 9 logical pixels.
    /// </summary>
    private const float ThinBevel = 0.6f;

    /// <inheritdoc cref="ThinBevel" />
    private const float RegularBevel = 0.9f;

    private float _adapt = Legibility;
    private float _bevel = ThinBevel;
    private float _elevation;
    private LiquidGroup? _group;
    private float _radius = AdwMetrics.Pill;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private Color? _tint;
    private float _tinted = 1f;

    public Widget? Child { get; set; }

    /// <summary>Corner radius. The default — <see cref="AdwMetrics.Pill" /> — makes a chip or a bar
    ///     a capsule; a pane carrying more than one row wants something nearer a card.</summary>
    public float Radius
    {
        get => _radius;
        set => SetPaint(field: ref _radius, value: value);
    }

    /// <summary>
    ///     Scrim colour; its alpha is the strength at <see cref="Tinted" /> = 1. Null — the default
    ///     — follows the theme: a light haze in <b>both</b> themes, milkier in the light one. Set it
    ///     only to force one family regardless of theme, or to colour the glass.
    /// </summary>
    public Color? Tint
    {
        get => _tint;
        set => SetPaint(field: ref _tint, value: value);
    }

    /// <summary>
    ///     Where the pane sits between the two variants: 0 is <see cref="Clear" /> — the barest
    ///     white haze, the lens doing the work — and 1 is <see cref="Regular" />, the full scrim
    ///     of <see cref="Tint" /> or the theme. Between them the scrim's colour and strength and
    ///     the adaptive pull all blend, and the pane repaints live, so a slider can drive it
    ///     without rebuilding anything. The clear end is white whatever the tint: a dark tint
    ///     faded to a whisper is a faintly darker hole in the page, not clear glass.
    /// </summary>
    public float Tinted
    {
        get => _tinted;
        set => SetPaint(
            field: ref _tinted,
            value: Math.Clamp(
                value: value,
                min: 0f,
                max: 1f
            )
        );
    }

    /// <summary>
    ///     How hard the shader pulls the backdrop toward the scrim's luminance anchor: 0 shows the
    ///     backdrop as it is, 1 is a full legibility clamp. This is what keeps a title readable
    ///     over artwork the pane cannot see. Also controls scattering in the shader.
    ///     Scaled to zero at the clear end of <see cref="Tinted" />.
    /// </summary>
    public float Adapt
    {
        get => _adapt;
        set => SetPaint(field: ref _adapt, value: Math.Clamp(value: value, min: 0f, max: 1f));
    }

    /// <summary>
    ///     Bevel width as a fraction of the theme's slab thickness — the material's <i>weight</i>,
    ///     Apple's thin-to-thick vibrancy scale collapsed to the two variants that earn their keep.
    ///     Thin glass is clearer, so it is the default and what <see cref="Clear" /> uses; a
    ///     <see cref="Regular" /> pane carries a slightly fuller rim. The lens bends only in the
    ///     outer third of this width, so the middle of the pane stays clear.
    /// </summary>
    public float Bevel
    {
        get => _bevel;
        set => SetPaint(field: ref _bevel, value: MathF.Max(x: 0f, y: value));
    }

    /// <summary>Soft float shadow size; 0 for a pane riding inline on what is behind it.</summary>
    public float Elevation
    {
        get => _elevation;
        set
        {
            if (_elevation == value) return;
            // The shadow paints outside Bounds, and DamageBounds is read when the repaint is
            // requested — so a shrinking shadow must damage the ring it is about to leave
            // before the field changes, or that ring stays on the persistent scene.
            MarkNeedsPaint();
            _elevation = value;
            MarkNeedsPaint();
        }
    }

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
    private Motion _thicknessMotion = new(spec: MotionSpec.Snappy, value: 1f, target: 1f);
    private Motion _pinchMotion = new(spec: MotionSpec.Snappy, value: 1f, target: 1f);
    private Motion _scrimMotion = new(spec: MotionSpec.Snappy, value: 1f, target: 1f);
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
        SnapGel();
    }

    // A pane that leaves the tree leaves its group, or the group would keep drawing a lens at
    // wherever the pane last was. A re-attach lays out again, and Layout joins again.
    protected override void OnUnmount() => Join(null);

    private void SnapGel()
    {
        var target = GelTarget;
        _thicknessMotion.SnapTo(target.Thick);
        _pinchMotion.SnapTo(target.Pinch);
        _scrimMotion.SnapTo(target.Scrim);
        _ticker?.Stop();
    }

    private void WakeGel()
    {
        if (!Mounted || _ticker is null || AnimationController.ReduceMotion)
        {
            SnapGel();
            MarkNeedsPaint();
            return;
        }

        // Quick compression, a gently elastic release. Retargeting retains velocity when
        // the pointer changes direction mid-flight; all three optical layers move together.
        var spec = _pressed
            ? MotionSpec.Spring(response: 0.18f, damping: 1f)
            : MotionSpec.Spring(response: 0.32f, damping: 0.78f);
        _thicknessMotion.Spec = _pinchMotion.Spec = _scrimMotion.Spec = spec;
        var target = GelTarget;
        _thicknessMotion.Retarget(target.Thick);
        _pinchMotion.Retarget(target.Pinch);
        _scrimMotion.Retarget(target.Scrim);
        _ticker.Start();
    }

    private void TickGel(float dt)
    {
        if (AnimationController.ReduceMotion)
            SnapGel();
        else
        {
            // Non-short-circuit OR: every motion must advance, including the settling frame.
            bool moving = _thicknessMotion.Tick(dt) | _pinchMotion.Tick(dt) | _scrimMotion.Tick(dt);
            if (!moving) _ticker?.Stop();
        }

        MarkNeedsPaint();
    }

    /// <summary>A straight channel blend; the alpha is decided separately.</summary>
    private static Color Mix(Color from, Color to, float t) => new(
        r: from.R + ((to.R - from.R) * t),
        g: from.G + ((to.G - from.G) * t),
        b: from.B + ((to.B - from.B) * t)
    );

    /// <summary>
    ///     Chrome over the app's own surfaces — a player bar, a floating control cluster. The full
    ///     scrim and a fuller bevel, because the theme already knows roughly what is behind it.
    /// </summary>
    public static LiquidPane Regular(
        Widget child, float radius = AdwMetrics.CardRadius, float elevation = 6f) =>
        new() {
            Child = child,
            Radius = radius,
            Elevation = elevation,
            Bevel = RegularBevel,
        };

    /// <summary>
    ///     Transparent chrome over media, with a thin bevel and minimal scattering. This variant
    ///     does not adapt to the backdrop. Supply a dimming layer beneath it and use bright ink.
    /// </summary>
    public static LiquidPane Clear(
        Widget child, float radius = AdwMetrics.Pill, float elevation = 0f) =>
        new() {
            Child = child,
            Radius = radius,
            Elevation = elevation,
            Tinted = 0f,
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

    // The float shadow paints outside Bounds, and partial repaint trusts this to cover it.
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

    private void Join(LiquidGroup? group)
    {
        if (ReferenceEquals(objA: _group, objB: group)) return;
        _group?.Remove(this);
        _group = group;
        group?.Add(this);
    }

    public override void Layout(Offset origin)
    {
        // The group is resolved here, not in Measure: the inherited scope is in force in both, but
        // Layout is never cached. A build that wraps retained content in a group, or unwraps it,
        // re-parents the content without detaching it — no unmount, and a measure cache above
        // the pane still valid — so a pane that only joined on measure would keep pointing at a
        // group no longer in the tree and paint no glass at all. Layout reaches every live pane
        // every pass, so the pane follows on the next one.
        Join(BuildContext.Current.FindAncestor<LiquidGroup>());
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
        // Inside a group the lens is already down: the group drew every pane's shadow and glass
        // before any of their content, so what is left of this pane is its content.
        if (_group is null)
        {
            PaintShadow(paint);
            PaintLens(paint);
        }

        Child?.Paint(paint);
    }

    /// <summary>
    ///     The float shadow alone. A <see cref="LiquidGroup" /> draws every pane's shadow before
    ///     any pane's lens: the shadow is an ordinary draw, and one between two lenses is exactly
    ///     what makes the engine capture the backdrop twice.
    /// </summary>
    internal void PaintShadow(PaintList paint)
    {
        if (Elevation <= 0f) return;
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
            spread: Elevation * 0.12f,
            squircle: true
        );
    }

    /// <summary>
    ///     The lens alone — no shadow, no content. What a <see cref="LiquidGroup" /> draws for each
    ///     of its panes ahead of all their content, so the lenses land consecutively and share one
    ///     backdrop capture.
    /// </summary>
    internal void PaintLens(PaintList paint)
    {
        // The shader's optical model is a clear middle inside a bevel that lenses at the rim — and
        // the bevel is `thickness` pixels wide. Scaling it to the shape as well as to the material
        // keeps a clear centre on small controls: past a fifth of the shorter side the two rims
        // start to meet and a capsule reads as a bead rather than a pane.
        float side = MathF.Min(x: Bounds.Width, y: Bounds.Height);
        float bevel = MathF.Min(x: _theme.GlassThickness * Bevel, y: side * 0.2f);

        // A restrained haze preserves the backdrop's colour. Contrast compression handles
        // legibility separately; a large white fill would turn dark glass into a grey slab.
        var haze = Tint ?? (_theme.IsDark
            ? new Color(
                r: 1f,
                g: 1f,
                b: 1f,
                a: 0.012f
            )
            : new Color(
                r: 1f,
                g: 1f,
                b: 1f,
                a: 0.18f
            ));

        // Clear to tinted: the scrim runs from the clear variant's white floor up to the haze's
        // own colour and strength, and the adaptive pull eases in alongside it — at 0 this is the
        // clear variant exactly, at 1 the regular one, and a slider between them moves the
        // material, not the widget tree. A Tint fainter than the floor is its own floor, so the
        // dial never runs backwards.
        float floor = MathF.Min(x: ClearHaze, y: haze.A);
        float strength = floor + ((haze.A - floor) * Tinted);
        var colour = Mix(from: Color.White, to: haze, t: Tinted);
        float adapt = Adapt * Tinted;

        // The gel: hover thickens the lens a touch, press compresses it and squeezes the
        // refraction — the same response Glass.Surface gives the system controls, eased through
        // the animated factors above rather than snapped.
        float thickness = Math.Clamp(
            value: bevel * _thicknessMotion.Value,
            min: 0f,
            max: side * 0.2f
        );
        float pinch = _theme.GlassPinch * _pinchMotion.Value;
        float scrim = Math.Clamp(
            value: strength * _scrimMotion.Value,
            min: 0f,
            max: 1f
        );

        // The anchor follows the CONTENT, not the page. With no Tint the content is the theme's
        // OnGlass ink, so the theme decides: dark under white content, light under dark content.
        // Deriving that default from the haze's own luminance (the obvious heuristic, and the
        // first version of this) inverted exactly when it mattered most: dark-mode glass is a
        // *milky* tint, whose high luminance anchored the backdrop light — lifting a bright album
        // cover toward white directly underneath white text. An explicit Tint is different: it
        // declares a family the theme knows nothing about, and its content was chosen against it
        // (white on dark or accent glass, ink on light) — so there, and only there, the tint's own
        // luminance is the truth. Clear has no adaptive pull and needs dimming beneath it.
        bool lightContent = Tint is { } family
            ? (family.R * 0.299f) + (family.G * 0.587f) + (family.B * 0.114f) < 0.5f
            : _theme.IsDark;
        paint.AddLiquidGlass(
            bounds: Bounds,
            color: colour.WithAlpha(scrim),
            radius: Radius,
            thickness: thickness,
            glowX: _theme.GlassGlowX,
            glowY: _theme.GlassGlowY,
            pinch: pinch,
            adapt: lightContent ? -adapt : adapt
        );

        // No drawn border. The rim light IS the shader's directional specular and fresnel; a flat
        // outline painted over it is exactly what makes glass read as brushed aluminium.
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
