using Zigote.Core;

namespace Zigote.UI.Widgets.Overlays;

/// <summary>Preferred side to place a floating surface relative to its anchor.</summary>
public enum OverlaySide
{
    Below,
    Above,
    Right,
    Left,
}

/// <summary>
///     Shared placement maths for floating surfaces — menus, tooltips, popovers, dropdowns, snackbars.
///     Centralises anchored positioning and screen-edge clamping so every overlay stays on-screen and
///     flips consistently instead of each widget re-deriving the same arithmetic.
/// </summary>
public static class OverlayPositioning
{
    /// <summary>
    ///     Shift <paramref name="rect" /> the minimum amount needed to keep it within
    ///     <paramref name="screen" /> (leaving <paramref name="margin" /> at the edges).
    ///     <paramref name="safe" /> adds the device's safe-area insets to those edges: overlays are
    ///     laid out against the whole window, outside any <c>SafeArea</c>, so without it a menu row
    ///     can land under a notch or the home indicator where it cannot be tapped. Zero on desktop.
    /// </summary>
    public static Rect Clamp(Rect rect, Size screen, float margin = 8f, EdgeInsets safe = default)
    {
        float x = rect.X;
        float y = rect.Y;

        float left = safe.Left + margin;
        float top = safe.Top + margin;
        float right = screen.Width - safe.Right - margin;
        float bottom = screen.Height - safe.Bottom - margin;

        if (x + rect.Width > right) x = right - rect.Width;
        if (y + rect.Height > bottom) y = bottom - rect.Height;
        if (x < left) x = left;
        if (y < top) y = top;

        return new Rect(
            x: x,
            y: y,
            width: rect.Width,
            height: rect.Height
        );
    }

    /// <summary>
    ///     Place a surface of <paramref name="size" /> next to <paramref name="anchor" /> on the
    ///     preferred <paramref name="side" />, flipping to the opposite side when it would overflow,
    ///     then clamping onto the screen. Returns the surface's screen rect.
    /// </summary>
    public static Rect Anchored(Rect anchor, Size size, Size screen,
        OverlaySide side = OverlaySide.Below, float gap = 4f, float margin = 8f,
        EdgeInsets safe = default)
    {
        // Flip against the same usable box Clamp enforces, so a surface never "fits" on a side
        // that clamping would then drag back over its anchor.
        float left = safe.Left + margin;
        float top = safe.Top + margin;
        float right = screen.Width - safe.Right - margin;
        float bottom = screen.Height - safe.Bottom - margin;

        var resolved = side switch {
            OverlaySide.Below when anchor.Bottom + gap + size.Height > bottom
                                   && anchor.Y - gap - size.Height >= top => OverlaySide.Above,
            OverlaySide.Above when anchor.Y - gap - size.Height < top
                                   && anchor.Bottom + gap + size.Height <= bottom =>
                OverlaySide.Below,
            OverlaySide.Right when anchor.Right + gap + size.Width > right
                                   && anchor.X - gap - size.Width >= left => OverlaySide.Left,
            OverlaySide.Left when anchor.X - gap - size.Width < left
                                  && anchor.Right + gap + size.Width <= right =>
                OverlaySide.Right,
            _ => side,
        };

        (float x, float y) = resolved switch {
            OverlaySide.Below => (anchor.X, anchor.Bottom + gap),
            OverlaySide.Above => (anchor.X, anchor.Y - gap - size.Height),
            OverlaySide.Right => (anchor.Right + gap, anchor.Y),
            OverlaySide.Left => (anchor.X - gap - size.Width, anchor.Y),
            _ => (anchor.X, anchor.Bottom + gap),
        };

        return Clamp(
            rect: new Rect(
                x: x,
                y: y,
                width: size.Width,
                height: size.Height
            ),
            screen: screen,
            margin: margin,
            safe: safe
        );
    }
}
