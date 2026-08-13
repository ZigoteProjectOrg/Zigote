using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using AppInstance = Zigote.UI.Host.App;

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

    // Resolved at Show(): with secondary OS windows, the window presenting the popover is the one
    // whose dispatch is running (App.Active), which may differ from the window active at construction.
    private AppInstance? _host;
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

    /// <summary>Show this popover as an overlay.</summary>
    public void Show()
    {
        _host = AppInstance.Active ?? _app;
        _host.PushOverlay(this);
    }

    /// <summary>Remove this popover from the overlay stack.</summary>
    public void Dismiss() => (_host ?? _app).PopOverlay(this);

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);

        // Capture the whole window so click-outside dismissal works.
        _screen = new Size(width: c.MaxWidth, height: c.MaxHeight);
        // Overlays sit outside any SafeArea, so the device insets have to be honoured here.
        _safe = MediaQuery.Of(BuildContext.Current).Padding;

        // Leave room for the inset, the arrow, and the screen margins.
        float maxW = MathF.Max(
            x: 0f,
            y: _screen.Width - _safe.Horizontal - (Margin * 2f) - (ContentInset * 2f)
        );
        float maxH = MathF.Max(
            x: 0f,
            y: _screen.Height - _safe.Vertical - (Margin * 2f) - (ContentInset * 2f) - ArrowDepth
        );
        _childSize = _child.Measure(new Constraints(maxWidth: maxW, maxHeight: maxH));

        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _screen.Width,
            height: _screen.Height
        );

        var surfaceSize = new Size(
            width: _childSize.Width + (ContentInset * 2f),
            height: _childSize.Height + (ContentInset * 2f)
        );

        // Anchor relative to the overlay origin so positioning matches the painted coordinate space.
        var anchor = new Rect(
            x: Anchor.X + origin.X,
            y: Anchor.Y + origin.Y,
            width: Anchor.Width,
            height: Anchor.Height
        );

        _surface = OverlayPositioning.Anchored(
            anchor: anchor,
            size: surfaceSize,
            screen: _screen,
            side: PreferredSide,
            gap: Gap,
            safe: _safe
        );

        _resolvedSide = ResolveSide(anchor: anchor, surface: _surface);

        _child.Layout(
            new Offset(
                x: _surface.X + ContentInset,
                y: _surface.Y + ContentInset
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
        float radius = Radii.Xl;

        paint.AddElevation(bounds: _surface, radius: radius, style: Elevation.Z2);
        paint.AddRect(bounds: _surface, color: _theme.Surface, radius: radius);
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
        float anchorCx = Anchor.X + Bounds.X + (Anchor.Width / 2f);
        float anchorCy = Anchor.Y + Bounds.Y + (Anchor.Height / 2f);

        const int steps = 7;
        for (int i = 0; i < steps; i++)
        {
            // t: 0 at the base (flush with the surface), 1 at the tip.
            float t = (i + 0.5f) / steps;
            float half = ArrowHalf * (1f - t);
            float span = MathF.Max(x: half * 2f, y: 1f);
            float thickness = (ArrowDepth / steps) + 1f;

            switch (_resolvedSide)
            {
                case OverlaySide.Below: // surface under anchor → arrow on top edge, tip points up
                {
                    float cx = ClampCenter(
                        c: anchorCx,
                        min: _surface.X + ArrowHalf,
                        max: _surface.Right - ArrowHalf
                    );
                    float y = _surface.Y - (ArrowDepth * t);
                    paint.AddRect(
                        bounds: new Rect(
                            x: cx - half,
                            y: y,
                            width: span,
                            height: thickness
                        ),
                        color: color
                    );
                    break;
                }
                case OverlaySide.Above
                    : // surface above anchor → arrow on bottom edge, tip points down
                {
                    float cx = ClampCenter(
                        c: anchorCx,
                        min: _surface.X + ArrowHalf,
                        max: _surface.Right - ArrowHalf
                    );
                    float y = _surface.Bottom + (ArrowDepth * t) - thickness;
                    paint.AddRect(
                        bounds: new Rect(
                            x: cx - half,
                            y: y,
                            width: span,
                            height: thickness
                        ),
                        color: color
                    );
                    break;
                }
                case OverlaySide.Right
                    : // surface right of anchor → arrow on left edge, tip points left
                {
                    float cy = ClampCenter(
                        c: anchorCy,
                        min: _surface.Y + ArrowHalf,
                        max: _surface.Bottom - ArrowHalf
                    );
                    float x = _surface.X - (ArrowDepth * t);
                    paint.AddRect(
                        bounds: new Rect(
                            x: x,
                            y: cy - half,
                            width: thickness,
                            height: span
                        ),
                        color: color
                    );
                    break;
                }
                case OverlaySide.Left
                    : // surface left of anchor → arrow on right edge, tip points right
                {
                    float cy = ClampCenter(
                        c: anchorCy,
                        min: _surface.Y + ArrowHalf,
                        max: _surface.Bottom - ArrowHalf
                    );
                    float x = _surface.Right + (ArrowDepth * t) - thickness;
                    paint.AddRect(
                        bounds: new Rect(
                            x: x,
                            y: cy - half,
                            width: thickness,
                            height: span
                        ),
                        color: color
                    );
                    break;
                }
            }
        }
    }

    private static float ClampCenter(float c, float min, float max)
    {
        if (max < min) return (min + max) / 2f;
        return MathF.Min(x: MathF.Max(x: c, y: min), y: max);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;

        // Inside the surface: let the child take the hit so its controls stay interactive.
        if (_surface.Contains(px: point.X, py: point.Y))
            return _child.HitTest(point) ?? this;

        // Outside the surface but over the screen-spanning overlay: capture so the
        // outside click routes to OnPointerDown and dismisses (mirrors ContextMenu).
        return this;
    }

    public override void OnPointerDown(Offset point)
    {
        if (!_surface.Contains(px: point.X, py: point.Y))
            Dismiss();
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_child);
}
