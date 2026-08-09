using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Focus;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools;

/// <summary>
///     The in-app devtools overlay: a <see cref="DevToolsView" /> pushed as its own overlay while open.
///     <para>
///         Three presentations. Docked, it is a column on the right whose width the user drags
///         (<see cref="DevResizeHandle" />) and which the rest of the app shows through — everything
///         outside the column falls through to the app (see <see cref="HitTest" />). Fullscreen (the
///         header's expand button, or any phone-width screen, where a 408px column would be most of the
///         display anyway) it covers the host window. Torn off into its own OS window, this overlay is
///         not used at all — see <see cref="DevToolsController.OpenWindow" />.
///     </para>
///     Non-modal: it opts out of auto-focus (<see cref="INoAutoFocus" />) so opening it never steals
///     focus, and closes on Escape (<see cref="IDismissableOverlay" />).
/// </summary>
public sealed class DevToolsPanel : ComposedWidget, IDismissableOverlay, INoAutoFocus
{
    /// <summary>Default width of the docked column, before the user drags it.</summary>
    public const float PanelWidth = 408f;

    private readonly DevToolsController _controller;

    public DevToolsPanel(DevToolsController controller)
    {
        _controller = controller;
    }

    private bool _full;

    /// <summary>
    ///     Width the overlay actually occupies on the right — the (resizable) docked column, or the
    ///     whole window when fullscreen. Computed rather than captured at build time so a host-window
    ///     resize is reflected immediately. Read by <see cref="DevOverlayLayer" /> to keep the fps badge
    ///     clear of it.
    /// </summary>
    public float VisibleWidth => _full
        ? MathF.Max(Bounds.Width, _controller.DockWidth)
        : _controller.DockWidth;

    public bool RequestDismiss()
    {
        // Esc exits select-widget mode first; a second Esc closes the panel.
        if (_controller.InspectMode)
        {
            _controller.InspectMode = false;
            _controller.HoverHighlight = null;
            return true;
        }

        if (!_controller.PanelOpen) return false;
        _controller.TogglePanel();
        return true;
    }

    // Only the panel's own column is interactive; clicks elsewhere fall through to the app so the panel
    // never blocks the scene/UI behind it. (ComposedWidget.HitTest otherwise returns `this` on a
    // child-miss, which would swallow every click over the full-screen overlay.) The drag handle sits
    // just outside the column, so the interactive band is a little wider than the view.
    public override Widget? HitTest(Offset point)
    {
        if (point.X < Bounds.Right - VisibleWidth - DevResizeHandle.Width) return null;
        return base.HitTest(point);
    }

    // Built through an AdaptiveBuilder so a host-window resize across the phone breakpoint
    // actually re-picks the arm: the ambient MediaQuery is not an inherited widget here, so a
    // plain Build() would keep whatever layout it chose the first time.
    protected override Widget Build(BuildContext context)
    {
        return new AdaptiveBuilder(BuildArm, 0f);
    }

    private Widget BuildArm(BuildContext context, WindowSizeClass cls)
    {
        var mq = MediaQuery.Of(context);
        // A phone is always fullscreen: a docked column plus a drag handle is a pointer idea.
        var full = _controller.Fullscreen || cls == WindowSizeClass.Compact;
        var width = full ? mq.Width : _controller.DockWidth;
        _full = full;

        var view = new DevToolsView(
            _controller,
            full ? DevToolsChrome.Fullscreen : DevToolsChrome.Docked
        );

        var stack = new Stack {
            Children = {
                new Positioned(
                    view,
                    top: 0,
                    bottom: 0,
                    right: 0,
                    left: full ? 0 : null,
                    width: full ? null : width
                ),
            },
        };
        if (!full)
            stack.Children.Add(
                new Positioned(
                    new DevResizeHandle(_controller),
                    top: 0,
                    bottom: 0,
                    right: width,
                    width: DevResizeHandle.Width
                )
            );
        return stack;
    }
}

/// <summary>
///     The docked column's left edge: drag it to resize the panel. Paints as the Adwaita window-split
///     hairline, tinting to the accent while hovered or dragged, and carries a horizontal-resize cursor
///     so the affordance is discoverable without a visible grip.
/// </summary>
public sealed class DevResizeHandle(DevToolsController controller) : LeafWidget
{
    /// <summary>Grab band width — wider than the hairline it paints, so it is easy to hit.</summary>
    public const float Width = 6f;

    private bool _dragging;
    private bool _hovered;
    private float _startWidth;
    private float _startX;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = new Size(Width, float.IsFinite(c.MaxHeight) ? c.MaxHeight : c.MinHeight);
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
        var p = AdwPalette.For(_theme);
        var active = _dragging || _hovered;
        // The hairline sits on the column's edge; the rest of the band stays invisible.
        paint.AddRect(
            new Rect(
                Bounds.Right - 1f,
                Bounds.Y,
                1f,
                Bounds.Height
            ),
            active ? _theme.Accent : p.HeaderbarShade
        );
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

    public override MouseCursor? GetCursor(Offset point)
    {
        return MouseCursor.ResizeEW;
    }

    public override void OnPointerEnter()
    {
        _hovered = true;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        _hovered = false;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        _dragging = true;
        _startX = point.X;
        _startWidth = controller.DockWidth;
        MarkNeedsPaint();
    }

    public override void OnPointerMove(Offset point)
    {
        // Dragging left (a smaller x) widens the panel: it is anchored to the right edge.
        if (_dragging) controller.DockWidth = _startWidth + (_startX - point.X);
    }

    public override void OnPointerUp(Offset point)
    {
        _dragging = false;
        MarkNeedsPaint();
    }

    public override void OnPointerCancel()
    {
        _dragging = false;
        MarkNeedsPaint();
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(_dragging, _hovered, controller.DockWidth);
    }
}