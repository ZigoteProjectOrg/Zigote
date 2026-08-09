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
    public const float EntryHeight = 34f;

    /// <summary>
    ///     The compact height every control that offers a <c>Compact</c> mode drops to. GNOME uses
    ///     it for buttons packed into a tight strip; a dense authoring surface (an inspector, a
    ///     property grid) uses it for its whole row vocabulary, so buttons, entries, drop-downs and
    ///     spin buttons must all land on the SAME number or the rows visibly jitter.
    /// </summary>
    public const float CompactControlHeight = 28f;
    public const float HeaderBarHeight = 47f;
    public const float RowMinHeight = 50f;
    public const float MenuRowHeight = 32f;

    // ── Controls ───────────────────────────────────────────────────────────────
    public const float SwitchWidth = 48f;
    public const float SwitchHeight = 26f;
    /// <summary>
    ///     Check/radio indicator box: libadwaita's <c>check</c> is a 14px content square with 3px
    ///     padding, so the drawn square is 20.
    /// </summary>
    public const float CheckSize = 20f;
    /// <summary>
    ///     Scale trough thickness. Adwaita's trough is a chunky 10px capsule, not the 4px hairline
    ///     a Material slider draws — with a 20px knob riding on it, a 4px track reads as a
    ///     different design language at a glance.
    /// </summary>
    public const float SliderTrack = 10f;
    public const float SliderKnob = 20f;
    /// <summary>Progress trough/fill thickness (<c>min-height: 8px</c>; the .osd variant is 2px).</summary>
    public const float ProgressBarHeight = 8f;
    public const float IconSize = 16f;

    // ── Layout ─────────────────────────────────────────────────────────────────
    /// <summary>AdwClamp default maximum content width (preferences pages, status pages).</summary>
    public const float ClampWidth = 600f;

    public const float SidebarWidth = 260f;
    /// <summary>
    ///     Side padding of a label-only button — libadwaita's <c>.text-button</c>.
    /// </summary>
    public const float ButtonPaddingX = 17f;

    /// <summary>
    ///     Side padding of a button carrying BOTH an icon and a label — libadwaita's
    ///     <c>.image-text-button</c>, which tightens to 9px because the icon already reads as
    ///     leading space. A text-button's 17px around an icon+label pair looks visibly loose.
    /// </summary>
    public const float ImageTextPaddingX = 9f;

    /// <summary>
    ///     A toggle group's inset around its toggles (libadwaita's <c>--group-padding</c>). Each
    ///     toggle's radius is this much smaller than the group's, so the two curves stay concentric.
    /// </summary>
    public const float ToggleGroupPadding = 3f;

    /// <summary>A <c>.round</c> toggle group's outer radius, before the group padding is taken off.</summary>
    public const float RoundToggleRadius = 17f;

    /// <summary>Side padding of a <c>.pill</c> button.</summary>
    public const float PillPaddingX = 32f;

    /// <summary>
    ///     A <c>.pill</c> button's height. Adwaita pills are not merely round-ended regular buttons:
    ///     the padding is 10px vertical against a regular button's 5px, so the pill stands 10px
    ///     taller.
    /// </summary>
    public const float PillHeight = 44f;
    public const float RowPaddingX = 12f;

    /// <summary>
    ///     Navigation-sidebar row: 36px tall, padded 8px (tighter than a boxed-list row's 12), with
    ///     a 2px gap to the next row. The gap is why the list's bottom padding is 2px shy of its top
    ///     — the last row already contributes it, and matching them over-pads the end of the list.
    /// </summary>
    public const float SidebarRowPaddingX = 8f;

    /// <inheritdoc cref="SidebarRowPaddingX" />
    public const float SidebarRowGap = 2f;
    public const float RowPaddingY = 8f;

    // ── Elevation ──────────────────────────────────────────────────────────────
    /// <summary>
    ///     The popover shadow, matching libadwaita's three-layer stack
    ///     (<c>0 0 0 1px rgb(0 0 0 / 5%), 0 1px 5px 1px rgb(0 0 0 / 9%),
    ///     0 2px 14px 3px rgb(0 0 0 / 5%)</c>) as the single soft shadow this renderer paints: the
    ///     hairline ring is drawn separately as a border, so what is left is a wide, low-opacity
    ///     lift. The shared <c>Elevation.Z2</c> token is a macOS-weight shadow — a third darker and
    ///     sitting twice as low — which reads as a drop shadow rather than GNOME's float.
    /// </summary>
    public static readonly ShadowStyle PopoverShadow = new(14f, 2f, 0.09f, 2f);
}
