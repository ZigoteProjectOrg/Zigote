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
    /// </summary>
    public static Rect Clamp(Rect rect, Size screen, float margin = 8f)
    {
        var x = rect.X;
        var y = rect.Y;

        if (x + rect.Width > screen.Width - margin) x = screen.Width - margin - rect.Width;
        if (y + rect.Height > screen.Height - margin) y = screen.Height - margin - rect.Height;
        if (x < margin) x = margin;
        if (y < margin) y = margin;

        return new Rect(
            x,
            y,
            rect.Width,
            rect.Height
        );
    }

    /// <summary>
    ///     Place a surface of <paramref name="size" /> next to <paramref name="anchor" /> on the
    ///     preferred <paramref name="side" />, flipping to the opposite side when it would overflow,
    ///     then clamping onto the screen. Returns the surface's screen rect.
    /// </summary>
    public static Rect Anchored(Rect anchor, Size size, Size screen,
        OverlaySide side = OverlaySide.Below, float gap = 4f, float margin = 8f)
    {
        var resolved = side switch {
            OverlaySide.Below when anchor.Bottom + gap + size.Height > screen.Height - margin
                                   && anchor.Y - gap - size.Height >= margin => OverlaySide.Above,
            OverlaySide.Above when anchor.Y - gap - size.Height < margin
                                   && anchor.Bottom + gap + size.Height <= screen.Height - margin =>
                OverlaySide.Below,
            OverlaySide.Right when anchor.Right + gap + size.Width > screen.Width - margin
                                   && anchor.X - gap - size.Width >= margin => OverlaySide.Left,
            OverlaySide.Left when anchor.X - gap - size.Width < margin
                                  && anchor.Right + gap + size.Width <= screen.Width - margin =>
                OverlaySide.Right,
            _ => side,
        };

        var (x, y) = resolved switch {
            OverlaySide.Below => (anchor.X, anchor.Bottom + gap),
            OverlaySide.Above => (anchor.X, anchor.Y - gap - size.Height),
            OverlaySide.Right => (anchor.Right + gap, anchor.Y),
            OverlaySide.Left => (anchor.X - gap - size.Width, anchor.Y),
            _ => (anchor.X, anchor.Bottom + gap),
        };

        return Clamp(
            new Rect(
                x,
                y,
                size.Width,
                size.Height
            ),
            screen,
            margin
        );
    }
}