using Zigote.UI.Host;

namespace Zigote.UI.Adwaita;

/// <summary>Which titlebar side a <see cref="AdwWindowControls" /> cluster renders.</summary>
public enum AdwControlsSide
{
    Start,
    End,
}

/// <summary>
///     AdwWindowControls — the GNOME window-frame buttons (close / minimize / maximize) for one
///     side of a headerbar, following the system's <c>button-layout</c> via
///     <see cref="GnomeDesktop" />. Renders nothing when the owning window has no Adwaita CSD
///     chrome (system decorations already show buttons) or when the layout puts no buttons on
///     this side — so it is always safe to mount.
/// </summary>
public sealed class AdwWindowControls : ComposedWidget
{
    public AdwWindowControls(AdwControlsSide side)
    {
        Side = side;
    }

    public AdwControlsSide Side { get; }

    protected override Widget Build(BuildContext context)
    {
        var app = Owner;
        if (app is null || app.ChromeStyle != Core.Engine.WindowChromeStyle.AdwaitaCsd)
            return new SizedBox(0f, 0f);

        // Frame buttons belong to the window, never to a floating sheet — a headerbar inside a
        // dialog (Preferences, About) must not grow its own ✕/─/□ cluster.
        for (var w = Parent; w is not null; w = w.Parent)
            if (w is AdwDialog)
                return new SizedBox(0f, 0f);

        var buttons = Side == AdwControlsSide.Start
            ? GnomeDesktop.LeftButtons
            : GnomeDesktop.RightButtons;
        if (buttons.Count == 0) return new SizedBox(0f, 0f);

        var theme = ThemeProvider.Of(context);
        var row = new Row(spacing: 6f, mainAxisSize: MainAxisSize.Min);
        foreach (var kind in buttons) row.Children.Add(new FrameButton(app, theme, kind));
        return row;
    }

    /// <summary>
    ///     One GNOME frame button: 24px circle with a drawn ✕ / low-bar / square glyph. A raw
    ///     widget rather than a Pressable so it stays OUT of the keyboard focus order — window
    ///     buttons never show a focus ring or steal the initial focus in GNOME.
    /// </summary>
    private sealed class FrameButton(App app, ThemeData theme, AdwWindowButton kind) : Widget
    {
        private const float Diameter = 24f;
        private bool _hovered;
        private bool _pressed;

        public override Size Measure(Constraints c)
        {
            return new Size(Diameter, Diameter);
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                Diameter,
                Diameter
            );
        }

        public override void Paint(PaintList paint)
        {
            paint.AddRect(
                Bounds,
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
                        Bounds,
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