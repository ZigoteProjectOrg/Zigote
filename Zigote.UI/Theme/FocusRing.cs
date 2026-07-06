using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Theme;

/// <summary>
///     One consistent keyboard focus ring for every control: a soft accent-coloured stroke a couple
///     of pixels outside the control's bounds, matching macOS's focus affordance. Controls call this
///     instead of hand-rolling their own focus border, so focus looks identical everywhere.
/// </summary>
public static class FocusRing
{
    /// <summary>
    ///     Paint the focus ring around <paramref name="bounds" /> at corner radius
    ///     <paramref name="radius" /> using the theme's focus tokens. No-ops while the active
    ///     window's focus was pointer-acquired (<see cref="Host.App.FocusRingVisible" /> — the
    ///     :focus-visible policy: a ring only for keyboard-driven focus), so controls call this
    ///     unconditionally when focused. App.Active is per-window during each window's paint
    ///     phase, so the policy is window-local.
    /// </summary>
    public static void AddFocusRing(this PaintList paint, Rect bounds, float radius,
        ThemeData theme)
    {
        if (Host.App.Active is { FocusRingVisible: false }) return;

        var o = theme.FocusRingOffset;
        var ring = new Rect(
            bounds.X - o,
            bounds.Y - o,
            bounds.Width + 2f * o,
            bounds.Height + 2f * o
        );
        paint.AddBorder(
            ring,
            theme.FocusRing,
            radius + o,
            theme.FocusRingWidth
        );
    }
}