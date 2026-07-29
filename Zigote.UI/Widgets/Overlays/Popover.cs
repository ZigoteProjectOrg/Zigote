using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using AppInstance = Zigote.UI.Host.App;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets.Overlays;

/// <summary>
///     A flat, macOS-style popover: a non-modal floating <see cref="ThemeData.Surface" /> surface
///     anchored to a rect, with a small triangular arrow pointing back at its anchor. Pushed onto the
///     overlay stack like <see cref="Controls.Dialog" /> / context menus — show with
///     <see cref="Show" />,
///     remove with <see cref="Dismiss" />. Clicking anywhere outside the surface dismisses it.
/// </summary>
public sealed class Popover : Widget
{
    /// <summary>Gap between the anchor and the surface — leaves room for the arrow.</summary>
    private const float Gap = 8f;

    /// <summary>Screen-edge margin used by <see cref="OverlayPositioning" />.</summary>
    private const float Margin = 8f;

    /// <summary>Inset padding between the surface edge and the child content.</summary>
    private const float ContentInset = Spacing.Md;

    /// <summary>Half-width of the arrow's base (the visible triangle spans 2× this).</summary>
    private const float ArrowHalf = 8f;

    /// <summary>How far the arrow tip protrudes from the surface toward the anchor.</summary>
    private const float ArrowDepth = 7f;

    private readonly AppInstance _app;

    private readonly Widget _child;

    private Size _childSize;
    private OverlaySide _resolvedSide = OverlaySide.Below;
    private EdgeInsets _safe;
    private Size _screen;
    private Rect _surface;
    private ThemeData _theme = ThemeData.Dark;

    public Popover(Widget child, Rect anchor)
    {
        _child = child;
        Anchor = anchor;
        _app = AppInstance.Active ?? throw new InvalidOperationException("No active App found.");
    }

    /// <summary>The screen-space rect the popover points at.</summary>
    public Rect Anchor { get; set; }

    /// <summary>Side of the anchor to place the surface on. Flips automatically when it would overflow.</summary>
    public OverlaySide PreferredSide { get; set; } = OverlaySide.Below;

    // Resolved at Show(): with secondary OS windows, the window presenting the popover is the one
    // whose dispatch is running (App.Active), which may differ from the window active at construction.
    private AppInstance? _host;

    /// <summary>Show this popover as an overlay.</summary>
    public void Show()
    {
        _host = AppInstance.Active ?? _app;
        _host.PushOverlay(this);
    }

    /// <summary>Remove this popover from the overlay stack.</summary>
    public void Dismiss()
    {
        (_host ?? _app).PopOverlay(this);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);

        // Capture the whole window so click-outside dismissal works.
        _screen = new Size(c.MaxWidth, c.MaxHeight);
        // Overlays sit outside any SafeArea, so the device insets have to be honoured here.
        _safe = MediaQuery.Of(BuildContext.Current).Padding;

        // Leave room for the inset, the arrow, and the screen margins.
        var maxW = MathF.Max(0f, _screen.Width - _safe.Horizontal - Margin * 2f - ContentInset * 2f);
        var maxH = MathF.Max(
            0f,
            _screen.Height - _safe.Vertical - Margin * 2f - ContentInset * 2f - ArrowDepth
        );
        _childSize = _child.Measure(new Constraints(maxWidth: maxW, maxHeight: maxH));

        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _screen.Width,
            _screen.Height
        );

        var surfaceSize = new Size(
            _childSize.Width + ContentInset * 2f,
            _childSize.Height + ContentInset * 2f
        );

        // Anchor relative to the overlay origin so positioning matches the painted coordinate space.
        var anchor = new Rect(
            Anchor.X + origin.X,
            Anchor.Y + origin.Y,
            Anchor.Width,
            Anchor.Height
        );

        _surface = OverlayPositioning.Anchored(
            anchor,
            surfaceSize,
            _screen,
            PreferredSide,
            Gap,
            safe: _safe
        );

        _resolvedSide = ResolveSide(anchor, _surface);

        _child.Layout(
            new Offset(
                _surface.X + ContentInset,
                _surface.Y + ContentInset
            )
        );
    }

    /// <summary>Infer which side the surface actually landed on after flipping/clamping.</summary>
    private static OverlaySide ResolveSide(Rect anchor, Rect surface)
    {
        if (surface.Y >= anchor.Bottom) return OverlaySide.Below;
        if (surface.Bottom <= anchor.Y) return OverlaySide.Above;
        if (surface.X >= anchor.Right) return OverlaySide.Right;
        if (surface.Right <= anchor.X) return OverlaySide.Left;
        return OverlaySide.Below;
    }

    public override void Paint(PaintList paint)
    {
        var radius = Radii.Xl;

        paint.AddElevation(_surface, radius, Elevation.Z2);
        paint.AddRect(_surface, _theme.Surface, radius);
        PaintArrow(paint);

        _child.Paint(paint);
    }

    /// <summary>
    ///     Draws a small triangle on the edge facing the anchor, built from thin
    ///     <see cref="PaintList.AddRect" />
    ///     dabs that narrow toward the tip — a flat, anti-alias-free arrow that reads as a pointer.
    /// </summary>
    private void PaintArrow(PaintList paint)
    {
        var color = _theme.Surface;

        // Centre of the arrow along the shared edge, clamped to stay within the surface corners.
        var anchorCx = Anchor.X + Bounds.X + Anchor.Width / 2f;
        var anchorCy = Anchor.Y + Bounds.Y + Anchor.Height / 2f;

        const int steps = 7;
        for (var i = 0; i < steps; i++)
        {
            // t: 0 at the base (flush with the surface), 1 at the tip.
            var t = (i + 0.5f) / steps;
            var half = ArrowHalf * (1f - t);
            var span = MathF.Max(half * 2f, 1f);
            var thickness = ArrowDepth / steps + 1f;

            switch (_resolvedSide)
            {
                case OverlaySide.Below: // surface under anchor → arrow on top edge, tip points up
                {
                    var cx = ClampCenter(
                        anchorCx,
                        _surface.X + ArrowHalf,
                        _surface.Right - ArrowHalf
                    );
                    var y = _surface.Y - ArrowDepth * t;
                    paint.AddRect(
                        new Rect(
                            cx - half,
                            y,
                            span,
                            thickness
                        ),
                        color
                    );
                    break;
                }
                case OverlaySide.Above
                    : // surface above anchor → arrow on bottom edge, tip points down
                {
                    var cx = ClampCenter(
                        anchorCx,
                        _surface.X + ArrowHalf,
                        _surface.Right - ArrowHalf
                    );
                    var y = _surface.Bottom + ArrowDepth * t - thickness;
                    paint.AddRect(
                        new Rect(
                            cx - half,
                            y,
                            span,
                            thickness
                        ),
                        color
                    );
                    break;
                }
                case OverlaySide.Right
                    : // surface right of anchor → arrow on left edge, tip points left
                {
                    var cy = ClampCenter(
                        anchorCy,
                        _surface.Y + ArrowHalf,
                        _surface.Bottom - ArrowHalf
                    );
                    var x = _surface.X - ArrowDepth * t;
                    paint.AddRect(
                        new Rect(
                            x,
                            cy - half,
                            thickness,
                            span
                        ),
                        color
                    );
                    break;
                }
                case OverlaySide.Left
                    : // surface left of anchor → arrow on right edge, tip points right
                {
                    var cy = ClampCenter(
                        anchorCy,
                        _surface.Y + ArrowHalf,
                        _surface.Bottom - ArrowHalf
                    );
                    var x = _surface.Right + ArrowDepth * t - thickness;
                    paint.AddRect(
                        new Rect(
                            x,
                            cy - half,
                            thickness,
                            span
                        ),
                        color
                    );
                    break;
                }
            }
        }
    }

    private static float ClampCenter(float c, float min, float max)
    {
        if (max < min) return (min + max) / 2f;
        return MathF.Min(MathF.Max(c, min), max);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;

        // Inside the surface: let the child take the hit so its controls stay interactive.
        if (_surface.Contains(point.X, point.Y))
            return _child.HitTest(point) ?? this;

        // Outside the surface but over the screen-spanning overlay: capture so the
        // outside click routes to OnPointerDown and dismisses (mirrors ContextMenu).
        return this;
    }

    public override void OnPointerDown(Offset point)
    {
        if (!_surface.Contains(point.X, point.Y))
            Dismiss();
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_child);
    }
}