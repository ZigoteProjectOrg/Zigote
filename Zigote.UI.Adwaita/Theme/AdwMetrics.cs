namespace Zigote.UI.Adwaita;

/// <summary>
///     Adwaita control sizing and shape constants (px, matching the libadwaita 1.10 / GNOME 51
///     stylesheet). Every number here has a line in <c>src/stylesheet/</c> behind it; the comment
///     names the selector it comes from where that isn't obvious.
/// </summary>
public static class AdwMetrics
{
    // ── Shape ──────────────────────────────────────────────────────────────────
    /// <summary>Buttons, entries, most controls (<c>$button_radius</c>).</summary>
    public const float ControlRadius = 9f;

    /// <summary>Cards, boxed lists, tab thumbnails (<c>$card_radius</c>).</summary>
    public const float CardRadius = 12f;

    /// <summary>Menu items, nav-sidebar rows, popover menus (<c>$menu_radius</c>).</summary>
    public const float MenuRadius = 9f;

    /// <summary>
    ///     Popovers and dialogs — <c>$popover_radius</c> and <c>$dialog_radius</c>, both
    ///     <c>$menu_radius + 6</c> / <c>$button_radius + 6</c>.
    /// </summary>
    public const float PopoverRadius = 15f;

    /// <inheritdoc cref="PopoverRadius" />
    public const float DialogRadius = 15f;

    /// <summary>
    ///     Window / sheet corner radius — <c>--window-radius: $button_radius + 6</c>. GNOME 51
    ///     rounded this up from the 12px of earlier releases; anything else reads as "nearly Adwaita"
    ///     against real GNOME windows next to it.
    /// </summary>
    public const float WindowRadius = 15f;

    /// <summary>Alert dialogs — <c>$alert_radius</c>, rounder than an ordinary dialog.</summary>
    public const float AlertRadius = 18f;

    /// <summary>Check and radio indicators (<c>$check_radius</c>), and shortcut key caps.</summary>
    public const float CheckRadius = 6f;

    /// <summary>Pill buttons, toasts, switches, progress bars, indicator dots.</summary>
    public const float Pill = 9999f;

    // ── Heights ────────────────────────────────────────────────────────────────
    /// <summary>Button: <c>min-height: 24px</c> + <c>padding: 5px</c> top and bottom.</summary>
    public const float ButtonHeight = 34f;

    /// <summary>Entry: <c>min-height: 34px</c>.</summary>
    public const float EntryHeight = 34f;

    /// <summary>
    ///     The compact height every control that offers a <c>Compact</c> mode drops to. GNOME uses
    ///     it for buttons packed into a tight strip; a dense authoring surface (an inspector, a
    ///     property grid) uses it for its whole row vocabulary, so buttons, entries, drop-downs and
    ///     spin buttons must all land on the SAME number or the rows visibly jitter.
    /// </summary>
    public const float CompactControlHeight = 28f;

    /// <summary><c>headerbar { min-height: 47px }</c>.</summary>
    public const float HeaderBarHeight = 47f;

    /// <summary>
    ///     What a headerbar stands at when it IS the window's titlebar under client-side
    ///     decorations —
    ///     <c>
    ///         window:not(.ssd-frame) > headerbar.titlebar { min-height: 46px;
    ///         padding-bottom: 6px }
    ///     </c>
    ///     . One pixel shorter than an inline bar, because the window's own
    ///     1px outline supplies the missing edge; matching 47 there leaves a CSD window visibly
    ///     taller in the chrome than a real GNOME one beside it.
    /// </summary>
    public const float TitleBarHeight = 46f;

    /// <summary>
    ///     A headerbar's vertical padding, and the gap between the widgets packed into it. One
    ///     constant because <see cref="AdwWindowControls" /> works backwards from it to size the
    ///     macOS traffic-light spacer — if the two drifted apart, the first packed widget would
    ///     drift off the window's titlebar inset.
    /// </summary>
    public const float HeaderBarPadding = 6f;

    /// <summary>
    ///     A headerbar's SIDE padding — <c>> windowhandle > box { padding: 6px 7px 7px 7px }</c>.
    ///     One more than the vertical, which is what keeps a 34px frame button off the window's
    ///     rounded corner.
    /// </summary>
    public const float HeaderBarPaddingX = 7f;

    /// <summary>
    ///     A window-control button's hit target:
    ///     <c>
    ///         windowcontrols > button { min-width: 24px;
    ///         padding: 5px }
    ///     </c>
    ///     , i.e. the full height of the bar's content box. The visible circle is
    ///     <see cref="FrameButtonCircle" /> — the padding is target, not decoration, and dropping it
    ///     leaves the close button a 24px square in a 34px bar.
    /// </summary>
    public const float FrameButtonSize = 34f;

    /// <summary>The drawn circle inside a frame button — a 16px glyph with <c>padding: 4px</c>.</summary>
    public const float FrameButtonCircle = 24f;

    /// <summary>Boxed-list row: <c>row > box.header { min-height: 50px }</c>.</summary>
    public const float RowMinHeight = 50f;

    /// <summary>Menu item / popover row: <c>modelbutton { min-height: 32px }</c>.</summary>
    public const float MenuRowHeight = 32f;

    /// <summary>Navigation-sidebar row: <c>.navigation-sidebar > row { min-height: 36px }</c>.</summary>
    public const float SidebarRowHeight = 36f;

    /// <summary>An <c>AdwButtonRow</c>'s content box: <c>row.button > box { min-height: 40px }</c>.</summary>
    public const float ButtonRowHeight = 40f;

    // ── Controls ───────────────────────────────────────────────────────────────
    public const float SwitchWidth = 48f;

    /// <summary>Switch: a 20px slider with 3px of trough padding around it.</summary>
    public const float SwitchHeight = 26f;

    /// <summary><c>switch { border-radius: 14px }</c> — not a full pill.</summary>
    public const float SwitchRadius = 14f;

    /// <summary>
    ///     Check/radio indicator box: libadwaita's <c>check</c> is a 14px content square with 3px
    ///     padding, so the drawn square is 20.
    /// </summary>
    public const float CheckSize = 20f;

    /// <summary>The 2px ring a check/radio draws when unchecked (<c>inset 0 0 0 2px</c>).</summary>
    public const float CheckBorder = 2f;

    /// <summary>
    ///     Scale trough thickness. Adwaita's trough is a chunky 10px capsule, not the 4px hairline
    ///     a Material slider draws — with a 20px knob riding on it, a 4px track reads as a
    ///     different design language at a glance.
    /// </summary>
    public const float SliderTrack = 10f;

    public const float SliderKnob = 20f;

    /// <summary><c>scale { padding: 12px }</c> — the touch margin around the trough.</summary>
    public const float SliderPadding = 12f;

    /// <summary>Progress trough/fill thickness (<c>min-height: 8px</c>; the .osd variant is 2px).</summary>
    public const float ProgressBarHeight = 8f;

    public const float IconSize = 16f;

    // ── Layout ─────────────────────────────────────────────────────────────────
    /// <summary>AdwClamp default maximum content width (preferences pages, status pages).</summary>
    public const float ClampWidth = 600f;

    public const float SidebarWidth = 260f;

    /// <summary>Side padding of a label-only button — libadwaita's <c>.text-button</c>.</summary>
    public const float ButtonPaddingX = 17f;

    /// <summary>
    ///     Side padding of a button carrying BOTH an icon and a label — libadwaita's
    ///     <c>.image-text-button</c>, which tightens to 9px because the icon already reads as
    ///     leading space. A text-button's 17px around an icon+label pair looks visibly loose.
    /// </summary>
    public const float ImageTextPaddingX = 9f;

    /// <summary>Gap between an icon and its label inside a button (<c>buttoncontent</c>).</summary>
    public const float ButtonContentSpacing = 6f;

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

    /// <summary>A <c>.circular</c> button — <c>min-width/height: 34px</c>, no padding.</summary>
    public const float CircularSize = 34f;

    /// <summary>Boxed-list row padding — <c>.rich-list > row { padding: 8px 12px }</c>.</summary>
    public const float RowPaddingX = 12f;

    /// <inheritdoc cref="RowPaddingX" />
    public const float RowPaddingY = 8f;

    /// <summary>Gap between a row's prefixes, title box and suffixes (<c>border-spacing: 6px</c>).</summary>
    public const float RowSpacing = 6f;

    /// <summary>Gap between a row's title and its subtitle (<c>box.title { border-spacing: 3px }</c>).</summary>
    public const float RowTitleSpacing = 3f;

    /// <summary>
    ///     Navigation-sidebar row: 36px tall, padded 8px (tighter than a boxed-list row's 12), with
    ///     a 2px gap to the next row. The gap is why the list's bottom padding is 2px shy of its top
    ///     — the last row already contributes it, and matching them over-pads the end of the list.
    /// </summary>
    public const float SidebarRowPaddingX = 8f;

    /// <inheritdoc cref="SidebarRowPaddingX" />
    public const float SidebarRowGap = 2f;

    /// <summary>
    ///     Margin around menu items and sidebar rows (<c>$menu_margin</c>), and a popover menu's own
    ///     content padding.
    /// </summary>
    public const float MenuMargin = 6f;

    /// <summary>Inner side padding of a menu item (<c>$menu_padding</c>).</summary>
    public const float MenuPadding = 12f;

    /// <summary>A popover's content padding (<c>popover > contents { padding: 8px }</c>).</summary>
    public const float PopoverPadding = 8f;

    /// <summary>Toolbar / action bar padding and the gap between packed widgets.</summary>
    public const float ToolbarPadding = 6f;

    /// <summary>Preferences page: <c>margin: 24px 12px; border-spacing: 24px</c>.</summary>
    public const float PageMarginY = 24f;

    /// <inheritdoc cref="PageMarginY" />
    public const float PageMarginX = 12f;

    /// <inheritdoc cref="PageMarginY" />
    public const float PageSpacing = 24f;

    // ── Elevation ──────────────────────────────────────────────────────────────
    /// <summary>
    ///     The popover shadow, matching libadwaita's three-layer stack
    ///     (
    ///     <c>
    ///         0 0 0 1px rgb(0 0 0 / 5%), 0 1px 5px 1px rgb(0 0 0 / 9%),
    ///         0 2px 14px 3px rgb(0 0 0 / 5%)
    ///     </c>
    ///     ) as the single soft shadow this renderer paints: the
    ///     hairline ring is drawn separately as a border, so what is left is a wide, low-opacity
    ///     lift. The shared <c>Elevation.Z2</c> token is a macOS-weight shadow — a third darker and
    ///     sitting twice as low — which reads as a drop shadow rather than GNOME's float.
    /// </summary>
    public static readonly ShadowStyle PopoverShadow = new(
        Blur: 14f,
        OffsetY: 2f,
        Alpha: 0.09f,
        Spread: 2f
    );

    /// <summary>
    ///     The card shadow —
    ///     <c>
    ///         0 0 0 1px rgb(0 0 6 / 3%), 0 1px 3px 1px rgb(0 0 6 / 7%),
    ///         0 2px 6px 2px rgb(0 0 6 / 3%)
    ///     </c>
    ///     , collapsed to this renderer's one soft layer. Tighter
    ///     and fainter than <see cref="PopoverShadow" />: a card sits on the page, a popover floats
    ///     over it. Also what a checked toggle-group segment lifts with.
    /// </summary>
    public static readonly ShadowStyle CardShadow = new(
        Blur: 6f,
        OffsetY: 2f,
        Alpha: 0.07f,
        Spread: 1f
    );
}
