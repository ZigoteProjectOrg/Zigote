namespace Zigote.UI.Adwaita;

/// <summary>Adwaita control sizing and shape constants (px, matching libadwaita 1.9 defaults).</summary>
public static class AdwMetrics
{
    // ── Shape ──────────────────────────────────────────────────────────────────
    /// <summary>Buttons, entries, most controls (GNOME 47+ rounding).</summary>
    public const float ControlRadius = 9f;

    /// <summary>Cards, boxed lists, popover menus' items container.</summary>
    public const float CardRadius = 12f;

    /// <summary>Windows, dialogs, popovers.</summary>
    /// <summary>
    ///     Window / dialog / sheet corner radius. libadwaita's <c>$window_radius</c> is 12px;
    ///     anything rounder reads as "nearly Adwaita" against real GNOME windows next to it.
    /// </summary>
    public const float WindowRadius = 12f;

    /// <summary>Pill buttons, toasts, switches, ViewSwitcher pills.</summary>
    public const float Pill = 9999f;

    // ── Heights ────────────────────────────────────────────────────────────────
    public const float ButtonHeight = 34f;
    public const float CompactButtonHeight = 28f;
    public const float EntryHeight = 34f;
    public const float HeaderBarHeight = 47f;
    public const float RowMinHeight = 50f;
    public const float MenuRowHeight = 32f;

    // ── Controls ───────────────────────────────────────────────────────────────
    public const float SwitchWidth = 48f;
    public const float SwitchHeight = 26f;
    public const float CheckSize = 18f;
    public const float SliderTrack = 4f;
    public const float SliderKnob = 20f;
    public const float ProgressBarHeight = 4f;
    public const float IconSize = 16f;

    // ── Layout ─────────────────────────────────────────────────────────────────
    /// <summary>AdwClamp default maximum content width (preferences pages, status pages).</summary>
    public const float ClampWidth = 600f;

    public const float SidebarWidth = 260f;
    public const float ButtonPaddingX = 17f;
    public const float RowPaddingX = 12f;
    public const float RowPaddingY = 8f;
}