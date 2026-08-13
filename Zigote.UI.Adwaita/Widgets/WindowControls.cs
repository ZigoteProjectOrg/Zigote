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
    public AdwWindowControls(AdwControlsSide side)
    {
        Side = side;
    }

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
            app.ChromeStyle != Core.Engine.WindowChromeStyle.AdwaitaCsd)
            return false;

        for (var w = widget.Parent; w is not null; w = w.Parent)
            if (w is AdwDialog)
                return false;
        return true;
    }

    protected override Widget Build(BuildContext context)
    {
        var app = Owner;
        if (app is null) return new SizedBox(0f, 0f);

        for (var w = Parent; w is not null; w = w.Parent)
            if (w is AdwDialog)
                return new SizedBox(0f, 0f);

        // Unified chrome: the OS draws the window buttons ITSELF, over the app's content. Nothing
        // to draw here — but the start of the bar has to step aside for them, or the first packed
        // widget ends up underneath the lights.
        if (app.ChromeStyle == Core.Engine.WindowChromeStyle.MacUnified)
            return new SizedBox(
                Side == AdwControlsSide.Start ? TrafficLightReserve(app) : 0f,
                0f
            );

        if (app.ChromeStyle != Core.Engine.WindowChromeStyle.AdwaitaCsd)
            return new SizedBox(0f, 0f);

        // macOS CSD: the buttons are the traffic lights, and macOS puts them at the leading edge
        // whatever GNOME's button-layout says.
        if (OperatingSystem.IsMacOS())
            return Side == AdwControlsSide.Start
                ? new MacTrafficLights(app)
                : new SizedBox(0f, 0f);

        var buttons = Side == AdwControlsSide.Start
            ? GnomeDesktop.LeftButtons
            : GnomeDesktop.RightButtons;
        if (buttons.Count == 0) return new SizedBox(0f, 0f);

        var theme = ThemeProvider.Of(context);
        // `windowcontrols { border-spacing: 3px }` — the frame buttons sit closer together than
        // ordinary packed widgets do, which is what reads them as one cluster.
        var row = new Row(spacing: AdwMetrics.ToggleGroupPadding, mainAxisSize: MainAxisSize.Min);
        foreach (var kind in buttons) row.Children.Add(new FrameButton(app, theme, kind));
        return row;
    }

    /// <summary>
    ///     The spacer that clears the native traffic lights: the window's inset less the space the
    ///     headerbar already puts in front of the first packed widget (its own left padding, plus
    ///     the row gap after this spacer), so that widget lands exactly on the inset.
    /// </summary>
    private static float TrafficLightReserve(App app)
    {
        return MathF.Max(0f, app.TitleBarLeftInset - LeadIn);
    }

    /// <summary>
    ///     What the headerbar itself already puts in front of its first packed widget: its own side
    ///     padding, plus the row gap that follows this cluster. Both spacer widths below work
    ///     backwards from it, so the first real widget lands exactly on the window's titlebar inset.
    /// </summary>
    private const float LeadIn = AdwMetrics.HeaderBarPaddingX + AdwMetrics.HeaderBarPadding;

    /// <summary>
    ///     The macOS window buttons, drawn by the app: close · minimize · zoom as the three system
    ///     hues, dimmed to one grey while the window is not the key window. One widget rather than
    ///     three, because the cluster lights up and dims as a unit — and because
    ///     <see cref="HitTest" /> can then claim only the circles, leaving the space around them to
    ///     the headerbar's drag surface.
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
            Color.Rgb(255, 95, 87), // close
            Color.Rgb(254, 188, 46), // minimize
            Color.Rgb(40, 200, 64), // zoom
        ];

        private int _pressed = -1;
        private Size _size;
        private ThemeData _theme = ThemeData.Dark;

        public override Size Measure(Constraints c)
        {
            _theme = ThemeProvider.Of(BuildContext.Current);
            // Trailing room folded in so the NEXT widget starts on the titlebar inset: the bar
            // already contributes its padding before this cluster and a row gap after it.
            _size = new Size(
                MathF.Max(
                    Lead + Diameter * 3f + Gap * 2f,
                    App.MacTrafficLightInset - LeadIn
                ),
                Diameter
            );
            return _size;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                _size.Width,
                _size.Height
            );
        }

        public override void Paint(PaintList paint)
        {
            // Inactive windows grey their lights out — the strongest signal macOS gives that a
            // window is not the one taking keystrokes.
            var inactive = !app.WindowFocused;
            for (var i = 0; i < 3; i++)
            {
                var color = inactive ? _theme.Control : Hues[i];
                if (i == _pressed) color = Darken(color);
                paint.AddRect(LightRect(i), color, Diameter / 2f);
            }
        }

        private static Color Darken(Color c)
        {
            return new Color(c.R * 0.75f, c.G * 0.75f, c.B * 0.75f, c.A);
        }

        private Rect LightRect(int index)
        {
            return new Rect(
                Bounds.X + Lead + index * (Diameter + Gap),
                Bounds.Y + (Bounds.Height - Diameter) / 2f,
                Diameter,
                Diameter
            );
        }

        private int LightAt(Offset point)
        {
            for (var i = 0; i < 3; i++)
                if (LightRect(i).Contains(point.X, point.Y))
                    return i;
            return -1;
        }

        /// <summary>Only the circles are ours; the gaps stay part of the titlebar drag surface.</summary>
        public override Widget? HitTest(Offset point)
        {
            return LightAt(point) >= 0 ? this : null;
        }

        public override void OnPointerDown(Offset point)
        {
            _pressed = LightAt(point);
            if (_pressed >= 0) MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            if (_pressed < 0) return;
            _pressed = -1;
            MarkNeedsPaint();
        }

        public override void OnPointerUp(Offset point)
        {
            var hit = _pressed;
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

        public override MouseCursor? GetCursor(Offset point)
        {
            return MouseCursor.Pointer;
        }

        public override int DebugStateHash()
        {
            return HashCode.Combine(_pressed, app.WindowFocused);
        }
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

        public override Size Measure(Constraints c)
        {
            return new Size(Target, Target);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                Target,
                Target
            );
        }

        /// <summary>The drawn circle, centred in the (larger) hit target.</summary>
        private Rect Circle => new(
            Bounds.X + (Bounds.Width - Diameter) / 2f,
            Bounds.Y + (Bounds.Height - Diameter) / 2f,
            Diameter,
            Diameter
        );

        public override void Paint(PaintList paint)
        {
            var circle = Circle;
            paint.AddRect(
                circle,
                _pressed ? theme.ControlPressed : _hovered ? theme.ControlHover : theme.Control,
                Diameter / 2f
            );

            var fg = theme.OnBackground;
            var cx = Bounds.X + Bounds.Width / 2f;
            var cy = Bounds.Y + Bounds.Height / 2f;
            switch (kind)
            {
                case AdwWindowButton.Close:
                    Icons.Draw(
                        paint,
                        Icons.Close,
                        circle,
                        fg,
                        13f
                    );
                    break;
                case AdwWindowButton.Maximize:
                    paint.AddBorder(
                        new Rect(
                            cx - 4f,
                            cy - 4f,
                            8f,
                            8f
                        ),
                        fg,
                        1f,
                        1.4f
                    );
                    break;
                default: // minimize
                    paint.AddRect(
                        new Rect(
                            cx - 4.5f,
                            cy + 2.5f,
                            9f,
                            1.6f
                        ),
                        fg
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
            var inside = _pressed && Bounds.Contains(point.X, point.Y);
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

        public override MouseCursor? GetCursor(Offset point)
        {
            return MouseCursor.Pointer;
        }
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

    public AdwDragArea(Widget child)
    {
        _child = child;
    }

    protected override Widget Build(BuildContext context)
    {
        return _child;
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        if (!owner.CsdDragSurfaces.Contains(this)) owner.CsdDragSurfaces.Add(this);
    }

    public override void Detach()
    {
        Owner?.CsdDragSurfaces.Remove(this);
        base.Detach();
    }
}
