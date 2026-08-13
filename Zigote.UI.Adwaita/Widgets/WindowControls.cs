using Zigote.Core.Engine;
using Zigote.UI.Host;

namespace Zigote.UI.Adwaita;

/// <summary>Which titlebar side a <see cref="AdwWindowControls" /> cluster renders.</summary>
public enum AdwControlsSide
{
    Start,
    End,
}

/// <summary>
///     AdwWindowControls — the window-frame buttons for one side of a headerbar, in whatever
///     shape the host desktop draws them: on GNOME the libadwaita ✕/─/□ circles, following the
///     system's <c>button-layout</c> via <see cref="GnomeDesktop" />; on macOS the traffic
///     lights, centred in the bar the way every macOS titlebar centres them. Renders nothing when
///     the owning window has no client-side decorations (the OS already drew its buttons) or when
///     the layout puts no buttons on this side — so it is always safe to mount.
///     <para>
///         The one exception is macOS <i>unified</i> chrome, where the OS keeps drawing the real
///         traffic lights over the app's content: there this reserves their band rather than
///         drawing anything, so the bar's first packed widget still clears them
///         (<see cref="App.TitleBarLeftInset" />).
///     </para>
/// </summary>
public sealed class AdwWindowControls : ComposedWidget
{
    /// <summary>
    ///     What the headerbar itself already puts in front of its first packed widget: its own side
    ///     padding, plus the row gap that follows this cluster. Both spacer widths below work
    ///     backwards from it, so the first real widget lands exactly on the window's titlebar inset.
    /// </summary>
    private const float LeadIn = AdwMetrics.HeaderBarPaddingX + AdwMetrics.HeaderBarPadding;

    public AdwWindowControls(AdwControlsSide side) => Side = side;

    public AdwControlsSide Side { get; }

    /// <summary>
    ///     Is <paramref name="widget" /> part of the WINDOW's chrome — i.e. under Adwaita CSD and
    ///     not inside a floating sheet? Frame buttons belong to the window, never to a dialog
    ///     (Preferences, About): such a headerbar must not grow its own button cluster, must not
    ///     inset itself for traffic lights that are nowhere near it, and must not shed the pixel a
    ///     real titlebar gives up to the window outline.
    /// </summary>
    internal static bool IsWindowChrome(Widget widget)
    {
        if (widget.Owner is not { } app ||
            app.ChromeStyle != WindowChromeStyle.AdwaitaCsd)
            return false;

        for (var w = widget.Parent; w is not null; w = w.Parent)
        {
            if (w is AdwDialog)
                return false;
        }

        return true;
    }

    protected override Widget Build(BuildContext context)
    {
        var app = Owner;
        if (app is null) return new SizedBox(width: 0f, height: 0f);

        for (var w = Parent; w is not null; w = w.Parent)
        {
            if (w is AdwDialog)
                return new SizedBox(width: 0f, height: 0f);
        }

        // Unified chrome: the OS draws the window buttons ITSELF, over the app's content. Nothing
        // to draw here — but the start of the bar has to step aside for them, or the first packed
        // widget ends up underneath the lights.
        if (app.ChromeStyle == WindowChromeStyle.MacUnified)
        {
            return new SizedBox(
                width: Side == AdwControlsSide.Start ? TrafficLightReserve(app) : 0f,
                height: 0f
            );
        }

        if (app.ChromeStyle != WindowChromeStyle.AdwaitaCsd)
            return new SizedBox(width: 0f, height: 0f);

        // macOS CSD: the buttons are the traffic lights, and macOS puts them at the leading edge
        // whatever GNOME's button-layout says.
        if (OperatingSystem.IsMacOS())
        {
            return Side == AdwControlsSide.Start
                ? new MacTrafficLights(app)
                : new SizedBox(width: 0f, height: 0f);
        }

        var buttons = Side == AdwControlsSide.Start
            ? GnomeDesktop.LeftButtons
            : GnomeDesktop.RightButtons;
        if (buttons.Count == 0) return new SizedBox(width: 0f, height: 0f);

        var theme = ThemeProvider.Of(context);
        // `windowcontrols { border-spacing: 3px }` — the frame buttons sit closer together than
        // ordinary packed widgets do, which is what reads them as one cluster.
        var row = new Row(spacing: AdwMetrics.ToggleGroupPadding, mainAxisSize: MainAxisSize.Min);
        foreach (var kind in buttons)
            row.Children.Add(new FrameButton(app: app, theme: theme, kind: kind));
        return row;
    }

    /// <summary>
    ///     The spacer that clears the native traffic lights: the window's inset less the space the
    ///     headerbar already puts in front of the first packed widget (its own left padding, plus
    ///     the row gap after this spacer), so that widget lands exactly on the inset.
    /// </summary>
    private static float TrafficLightReserve(App app) =>
        MathF.Max(x: 0f, y: app.TitleBarLeftInset - LeadIn);

    /// <summary>
    ///     The macOS window buttons, drawn by the app: close · minimize · zoom as the three system
    ///     hues, dimmed to one grey while the window is not the key window, revealing their ✕ / ─ /
    ///     fullscreen glyphs while the pointer is over the cluster. One widget rather than three,
    ///     because the cluster lights up, dims and shows its glyphs as a unit — and because
    ///     <see cref="HitTest" /> can then claim exactly the band the three circles span, leaving
    ///     the lead-in and trailing reserve to the headerbar's drag surface.
    ///     <para>
    ///         The whole cluster is <see cref="App.MacTrafficLightInset" /> wide once the
    ///         headerbar's own padding and the row gap after it are counted in, so the first packed
    ///         widget lands on the same inset the native lights would have claimed.
    ///     </para>
    /// </summary>
    private sealed class MacTrafficLights(App app) : Widget
    {
        private const float Diameter = 12f;
        private const float Gap = 8f;

        /// <summary>Bar padding → first light. macOS puts its centre 20px in from the frame.</summary>
        private const float Lead = 8f;

        private static readonly Color[] Hues = [
            Color.Rgb(r: 255, g: 95, b: 87), // close
            Color.Rgb(r: 254, g: 188, b: 46), // minimize
            Color.Rgb(r: 40, g: 200, b: 64), // zoom
        ];

        /// <summary>Hover-glyph inks: each the system's darker shade of its light's hue.</summary>
        private static readonly Color[] GlyphHues = [
            Color.Rgb(r: 77, g: 0, b: 0), // close ✕
            Color.Rgb(r: 153, g: 87, b: 0), // minimize ─
            Color.Rgb(r: 0, g: 101, b: 0), // zoom triangles
        ];

        private bool _hovered;
        private int _pressed = -1;
        private Size _size;
        private ThemeData _theme = ThemeData.Dark;

        public override Size Measure(Constraints c)
        {
            _theme = ThemeProvider.Of(BuildContext.Current);
            // Trailing room folded in so the NEXT widget starts on the titlebar inset: the bar
            // already contributes its padding before this cluster and a row gap after it.
            _size = new Size(
                width: MathF.Max(
                    x: Lead + (Diameter * 3f) + (Gap * 2f),
                    y: App.MacTrafficLightInset - LeadIn
                ),
                height: Diameter
            );
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
        }

        public override void Paint(PaintList paint)
        {
            // Inactive windows grey their lights out — the strongest signal macOS gives that a
            // window is not the one taking keystrokes. Hover overrides the grey: macOS restores
            // the hues and reveals the glyphs, so a background window's buttons can still be
            // aimed at without focusing it first.
            bool showGlyphs = _hovered || _pressed >= 0;
            bool inactive = !app.WindowFocused && !showGlyphs;
            for (int i = 0; i < 3; i++)
            {
                var color = inactive ? _theme.Control : Hues[i];
                if (i == _pressed) color = Darken(color);
                var circle = LightRect(i);
                paint.AddRect(bounds: circle, color: color, radius: Diameter / 2f);
                if (showGlyphs) PaintGlyph(paint: paint, index: i, circle: circle);
            }
        }

        /// <summary>
        ///     One hover glyph, centred in its light: ✕ / ─ / the fullscreen triangle pair, sized
        ///     to the same ~55% of the circle the native lights use.
        /// </summary>
        private static void PaintGlyph(PaintList paint, int index, Rect circle)
        {
            float cx = circle.X + (circle.Width / 2f);
            float cy = circle.Y + (circle.Height / 2f);
            var ink = GlyphHues[index];
            switch (index)
            {
                case 0: // ✕ — two diagonal strokes, each a fan-safe quad
                {
                    const float arm = 2.6f; // centre → stroke end, per axis
                    const float o = 0.57f; // half stroke thickness, per axis (0.8 / √2)
                    Span<Offset> quad = stackalloc Offset[4];
                    quad[0] = new Offset(x: cx - arm - o, y: cy - arm + o);
                    quad[1] = new Offset(x: cx + arm - o, y: cy + arm + o);
                    quad[2] = new Offset(x: cx + arm + o, y: cy + arm - o);
                    quad[3] = new Offset(x: cx - arm + o, y: cy - arm - o);
                    paint.AddPolygon(points: quad, color: ink);
                    quad[0] = new Offset(x: cx - arm + o, y: cy + arm + o);
                    quad[1] = new Offset(x: cx + arm + o, y: cy - arm + o);
                    quad[2] = new Offset(x: cx + arm - o, y: cy - arm - o);
                    quad[3] = new Offset(x: cx - arm - o, y: cy + arm - o);
                    paint.AddPolygon(points: quad, color: ink);
                    break;
                }
                case 1: // ─
                    paint.AddRect(
                        bounds: new Rect(
                            x: cx - 4f,
                            y: cy - 0.8f,
                            width: 8f,
                            height: 1.6f
                        ),
                        color: ink,
                        radius: 0.8f
                    );
                    break;
                default: // zoom: two triangles split along the ↗ diagonal, pointing apart
                {
                    const float h = 3f; // half of the glyph box
                    const float g = 1.1f; // hypotenuse pull-back that opens the split
                    Span<Offset> tri = stackalloc Offset[3];
                    tri[0] = new Offset(x: cx - h, y: cy - h);
                    tri[1] = new Offset(x: cx + h - g, y: cy - h);
                    tri[2] = new Offset(x: cx - h, y: cy + h - g);
                    paint.AddPolygon(points: tri, color: ink);
                    tri[0] = new Offset(x: cx + h, y: cy + h);
                    tri[1] = new Offset(x: cx - h + g, y: cy + h);
                    tri[2] = new Offset(x: cx + h, y: cy - h + g);
                    paint.AddPolygon(points: tri, color: ink);
                    break;
                }
            }
        }

        private static Color Darken(Color c) => new(
            r: c.R * 0.75f,
            g: c.G * 0.75f,
            b: c.B * 0.75f,
            a: c.A
        );

        private Rect LightRect(int index)
        {
            return new Rect(
                x: Bounds.X + Lead + (index * (Diameter + Gap)),
                y: Bounds.Y + ((Bounds.Height - Diameter) / 2f),
                width: Diameter,
                height: Diameter
            );
        }

        private int LightAt(Offset point)
        {
            for (int i = 0; i < 3; i++)
            {
                if (LightRect(i).Contains(px: point.X, py: point.Y))
                    return i;
            }

            return -1;
        }

        /// <summary>The tight band the three circles span, gaps included.</summary>
        private Rect ClusterRect => new(
            x: Bounds.X + Lead,
            y: Bounds.Y + ((Bounds.Height - Diameter) / 2f),
            width: (Diameter * 3f) + (Gap * 2f),
            height: Diameter
        );

        /// <summary>
        ///     The whole cluster — circles AND the gaps between them — is ours, so the hover
        ///     glyphs come on and stay on while the pointer crosses the group, the way the
        ///     native lights track as one unit. The lead-in and the trailing reserve stay part
        ///     of the titlebar drag surface.
        /// </summary>
        public override Widget? HitTest(Offset point) =>
            ClusterRect.Contains(px: point.X, py: point.Y) ? this : null;

        public override void OnPointerEnter()
        {
            _hovered = true;
            MarkNeedsPaint();
        }

        public override void OnPointerDown(Offset point)
        {
            _pressed = LightAt(point);
            if (_pressed >= 0) MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            if (!_hovered && _pressed < 0) return;
            _hovered = false;
            _pressed = -1;
            MarkNeedsPaint();
        }

        public override void OnPointerUp(Offset point)
        {
            int hit = _pressed;
            _pressed = -1;
            MarkNeedsPaint();
            if (hit < 0 || LightAt(point) != hit) return;
            switch (hit)
            {
                case 0:
                    app.RequestClose();
                    break;
                case 1:
                    app.Engine.WindowChromeMinimize(app.WindowId);
                    break;
                default:
                    app.Engine.WindowChromeToggleMaximize(app.WindowId);
                    break;
            }
        }

        /// <summary>Pointer over the circles themselves; the gaps keep the default arrow.</summary>
        public override MouseCursor? GetCursor(Offset point) =>
            LightAt(point) >= 0 ? MouseCursor.Pointer : null;

        public override int DebugStateHash() => HashCode.Combine(
            value1: _pressed,
            value2: _hovered,
            value3: app.WindowFocused
        );
    }

    /// <summary>
    ///     One GNOME frame button: a 24px circle with a drawn ✕ / low-bar / square glyph, inside a
    ///     34px hit target — <c>windowcontrols > button { min-width: 24px; padding: 5px }</c> around
    ///     a <c>> image { padding: 4px }</c> circle. The padding is the target, not decoration: at
    ///     24px the close button is a small square in a 34px bar, and the 3px gaps between the
    ///     circles become dead pixels between them.
    ///     A raw widget rather than a Pressable so it stays OUT of the keyboard focus order — window
    ///     buttons never show a focus ring or steal the initial focus in GNOME.
    /// </summary>
    private sealed class FrameButton(App app, ThemeData theme, AdwWindowButton kind) : Widget
    {
        private const float Target = AdwMetrics.FrameButtonSize;
        private const float Diameter = AdwMetrics.FrameButtonCircle;
        private bool _hovered;
        private bool _pressed;

        /// <summary>The drawn circle, centred in the (larger) hit target.</summary>
        private Rect Circle => new(
            x: Bounds.X + ((Bounds.Width - Diameter) / 2f),
            y: Bounds.Y + ((Bounds.Height - Diameter) / 2f),
            width: Diameter,
            height: Diameter
        );

        public override Size Measure(Constraints c) => new(width: Target, height: Target);

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: Target,
                height: Target
            );
        }

        public override void Paint(PaintList paint)
        {
            var circle = Circle;
            paint.AddRect(
                bounds: circle,
                color: _pressed ? theme.ControlPressed :
                _hovered ? theme.ControlHover : theme.Control,
                radius: Diameter / 2f
            );

            var fg = theme.OnBackground;
            float cx = Bounds.X + (Bounds.Width / 2f);
            float cy = Bounds.Y + (Bounds.Height / 2f);
            switch (kind)
            {
                case AdwWindowButton.Close:
                    Icons.Draw(
                        paint: paint,
                        glyph: Icons.Close,
                        box: circle,
                        color: fg,
                        size: 13f
                    );
                    break;
                case AdwWindowButton.Maximize:
                    paint.AddBorder(
                        bounds: new Rect(
                            x: cx - 4f,
                            y: cy - 4f,
                            width: 8f,
                            height: 8f
                        ),
                        color: fg,
                        radius: 1f,
                        width: 1.4f
                    );
                    break;
                default: // minimize
                    paint.AddRect(
                        bounds: new Rect(
                            x: cx - 4.5f,
                            y: cy + 2.5f,
                            width: 9f,
                            height: 1.6f
                        ),
                        color: fg
                    );
                    break;
            }
        }

        public override void OnPointerMove(Offset point)
        {
            if (_hovered) return;
            _hovered = true;
            MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            if (!_hovered && !_pressed) return;
            _hovered = false;
            _pressed = false;
            MarkNeedsPaint();
        }

        public override void OnPointerDown(Offset point)
        {
            _pressed = true;
            MarkNeedsPaint();
        }

        public override void OnPointerUp(Offset point)
        {
            bool inside = _pressed && Bounds.Contains(px: point.X, py: point.Y);
            _pressed = false;
            MarkNeedsPaint();
            if (!inside) return;
            switch (kind)
            {
                case AdwWindowButton.Close:
                    app.RequestClose();
                    break;
                case AdwWindowButton.Maximize:
                    app.Engine.WindowChromeToggleMaximize(app.WindowId);
                    break;
                default:
                    app.Engine.WindowChromeMinimize(app.WindowId);
                    break;
            }
        }

        public override MouseCursor? GetCursor(Offset point) => MouseCursor.Pointer;
    }
}

/// <summary>
///     Marks its child's area as a draggable titlebar region for Adwaita CSD windows whose
///     chrome strip is suppressed (see <c>App.CsdDragSurfaces</c>): empty space moves the
///     window, interactive children keep working. Wrap your headerbar containers in this when
///     composing custom chrome; <see cref="AdwHeaderBar" /> does it automatically.
/// </summary>
public sealed class AdwDragArea : ComposedWidget
{
    private readonly Widget _child;

    public AdwDragArea(Widget child) => _child = child;

    protected override Widget Build(BuildContext context) => _child;

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner: owner, parent: parent);
        if (!owner.CsdDragSurfaces.Contains(this)) owner.CsdDragSurfaces.Add(this);
    }

    public override void Detach()
    {
        Owner?.CsdDragSurfaces.Remove(this);
        base.Detach();
    }
}
