namespace Zigote.UI.Adwaita;

/// <summary>Adwaita button appearance classes (.suggested-action, .destructive-action, .flat, …).</summary>
public enum AdwButtonStyle
{
    /// <summary>The neutral translucent fill (<c>currentColor 10%</c>).</summary>
    Regular,

    /// <summary>.suggested-action — solid accent.</summary>
    Suggested,

    /// <summary>
    ///     .destructive-action — a red-tinted translucent fill under standalone-red text. Not a
    ///     solid red button: libadwaita moved this style off the solid fill, which now only
    ///     .suggested-action and .opaque use.
    /// </summary>
    Destructive,

    /// <summary>.flat — transparent, fills on hover. (Header bar and toolbar buttons.)</summary>
    Flat,

    /// <summary>.opaque — a solid neutral fill (window bg mixed 85% toward the foreground).</summary>
    Opaque,
}

/// <summary>
///     Shared state → color resolution for Adwaita controls, mirroring the 1.10 stylesheet's
///     hover/active/checked/disabled rules. Disabled controls keep their normal fill and drop to
///     <see cref="DisabledOpacity" /> whole-widget opacity, which is exactly what
///     <c>filter: Opacity(var(--disabled-opacity))</c> does.
/// </summary>
public static class AdwStyle
{
    /// <summary><c>--disabled-opacity</c>.</summary>
    public const float DisabledOpacity = 0.5f;

    /// <summary>
    ///     <c>$strong_disabled_opacity</c> — what a FLAT control dims to. Flat controls have no fill
    ///     to lose, so they need to fade further than a raised one to read as insensitive.
    /// </summary>
    public const float StrongDisabledOpacity = 0.3f;

    /// <summary><c>--dim-opacity</c> — the <c>.dimmed</c> class (subtitles, captions, marks).</summary>
    public const float DimOpacity = 0.55f;

    /// <summary>
    ///     <c>$dimmer_opacity</c> — fainter still: split-button separators, menu arrows, entry-row
    ///     indicators, menu accelerator labels.
    /// </summary>
    public const float DimmerOpacity = 0.3f;

    /// <summary><c>rgb(0 0 6)</c> — the near-black every Adwaita press overlay is made of.</summary>
    internal static readonly Color Ink = Color.Rgb(r: 0, g: 0, b: 6);

    /// <summary>Background fill for a button-like control in a given interaction state.</summary>
    public static Color ButtonFill(
        ThemeData theme,
        AdwButtonStyle style,
        bool hovered = false,
        bool pressed = false,
        bool enabled = true,
        bool @checked = false)
    {
        var p = AdwPalette.For(theme);
        switch (style)
        {
            case AdwButtonStyle.Suggested:
                // %opaque_button: the accent stays put and an overlay rides on top.
                return Solid(
                    baseColor: theme.Accent,
                    hovered: hovered,
                    pressed: pressed,
                    enabled: enabled,
                    @checked: @checked
                );

            case AdwButtonStyle.Opaque:
                return Solid(
                    baseColor: AdwPalette.Mix(a: p.WindowFg, b: p.WindowBg, t: 0.15f),
                    hovered: hovered,
                    pressed: pressed,
                    enabled: enabled,
                    @checked: @checked
                );

            case AdwButtonStyle.Destructive:
                // %destructive_button: currentColor is the standalone red, 15/20/35 (checked
                // 35/40/45) — a tint of the text colour, not a solid red slab.
                return Tint(
                    currentColor: p.Destructive,
                    percent: @checked
                        ? pressed ? 0.45f : hovered ? 0.40f : 0.35f
                        : pressed
                            ? 0.35f
                            : hovered
                                ? 0.20f
                                : 0.15f,
                    theme: theme
                );

            case AdwButtonStyle.Flat:
                if (@checked)
                {
                    return pressed ? p.SelectedFillActive :
                        hovered ? p.SelectedFillHover : p.SelectedFill;
                }

                if (pressed) return p.ActiveFill;
                if (hovered) return p.HoverFill;
                return Color.Transparent;

            default:
                if (@checked)
                {
                    return pressed ? p.ButtonFillCheckedActive :
                        hovered ? p.ButtonFillCheckedHover : p.ButtonFillChecked;
                }

                if (pressed) return p.ButtonFillActive;
                if (hovered) return p.ButtonFillHover;
                return p.ButtonFill;
        }
    }

    /// <summary>
    ///     Hover/press modulation for a solid-colour (suggested / opaque) fill. Adwaita paints an
    ///     overlay rather than recolouring: <c>currentColor 10%</c> on hover — currentColor on a
    ///     solid accent fill is the accent FOREGROUND, i.e. white, so it lightens — and
    ///     <c>rgb(0 0 6 / 20%)</c> on press, which darkens. Checked sits between the two at 15%
    ///     black.
    /// </summary>
    public static Color Solid(
        Color baseColor,
        bool hovered,
        bool pressed,
        bool enabled = true,
        bool @checked = false)
    {
        if (!enabled) return baseColor;
        if (pressed) return AdwPalette.Mix(a: Ink, b: baseColor, t: @checked ? 0.30f : 0.20f);
        if (@checked) return AdwPalette.Mix(a: Ink, b: baseColor, t: hovered ? 0.05f : 0.15f);
        if (hovered) return AdwPalette.Mix(a: Color.White, b: baseColor, t: 0.10f);
        return baseColor;
    }

    /// <summary>Foreground for a button style.</summary>
    public static Color ButtonForeground(ThemeData theme, AdwButtonStyle style)
    {
        var p = AdwPalette.For(theme);
        return style switch {
            AdwButtonStyle.Suggested => p.AccentFg,
            // .destructive-action is standalone-red TEXT on a red tint, not white on red.
            AdwButtonStyle.Destructive => p.Destructive,
            _ => theme.OnBackground,
        };
    }

    /// <summary>
    ///     Hover/press wash for a BOXED-LIST row (<c>%boxed_list_row</c>) and for
    ///     <c>.card.activatable</c> — the faintest ladder in the stylesheet, 3% and 8%, because the
    ///     row sits on a card that is already lifted off the page.
    ///     Use <see cref="MenuRowFill" /> for menu items and navigation-sidebar rows, and
    ///     <see cref="ViewRowFill" /> for rows inside a plain list view.
    /// </summary>
    public static Color RowFill(ThemeData theme, bool hovered, bool pressed)
    {
        var p = AdwPalette.For(theme);
        if (pressed) return p.CardActiveFill;
        if (hovered) return p.CardHoverFill;
        return Color.Transparent;
    }

    /// <summary>
    ///     Hover/press wash for a menu item or popover row. <c>modelbutton</c> highlights straight
    ///     to <c>$selected_color</c> — a menu row is either under the pointer or invisible, so it
    ///     skips the gentle first rung the other row ladders start on.
    /// </summary>
    public static Color MenuRowFill(ThemeData theme, bool hovered, bool pressed,
        bool selected = false)
    {
        var p = AdwPalette.For(theme);
        if (pressed) return p.SelectedFillActive;
        if (hovered || selected) return p.SelectedFill;
        return Color.Transparent;
    }

    /// <summary>
    ///     Hover/press/selected wash for a navigation-sidebar row, a tab, or a flat toggle —
    ///     <c>$hover_color</c> / <c>$active_color</c>, with the <c>$selected_*</c> ladder once the
    ///     row is the current one.
    /// </summary>
    public static Color SidebarRowFill(ThemeData theme, bool hovered, bool pressed,
        bool selected = false)
    {
        var p = AdwPalette.For(theme);
        if (selected)
            return pressed ? p.SelectedFillActive : hovered ? p.SelectedFillHover : p.SelectedFill;
        if (pressed) return p.ActiveFill;
        if (hovered) return p.HoverFill;
        return Color.Transparent;
    }

    /// <summary>
    ///     Hover/press/selected wash for a row inside a <c>.view</c> — list views, flow boxes, drop-
    ///     down lists. Selected rows tint with the accent (25/32/39%) rather than the neutral.
    /// </summary>
    public static Color ViewRowFill(ThemeData theme, bool hovered, bool pressed,
        bool selected = false)
    {
        var p = AdwPalette.For(theme);
        if (selected)
            return p.AccentBg.WithAlpha(pressed ? 0.39f : hovered ? 0.32f : 0.25f);
        if (pressed) return p.ViewActiveFill;
        if (hovered) return p.ViewHoverFill;
        return Color.Transparent;
    }

    /// <summary>
    ///     Switch / scale / check trough — <c>$trough_color</c> and its hover/active steps. Adwaita
    ///     brightens the trough on hover of the WHOLE control, not of the trough alone.
    /// </summary>
    public static Color TroughFill(ThemeData theme, bool hovered = false, bool pressed = false)
    {
        var p = AdwPalette.For(theme);
        if (pressed) return p.TroughFillActive;
        if (hovered) return p.TroughFillHover;
        return p.TroughFill;
    }

    /// <summary>
    ///     The knob of a switch or scale — <c>color-mix(in srgb, white 80%, var(--view-bg-color))</c>,
    ///     going fully white on hover. In light mode that is a near-white; in dark mode it is the
    ///     light grey that keeps the knob from glaring.
    /// </summary>
    public static Color SliderKnob(ThemeData theme, bool hot = false)
    {
        var p = AdwPalette.For(theme);
        return hot ? Color.White : AdwPalette.Mix(a: Color.White, b: p.ViewBg, t: 0.8f);
    }

    /// <summary>
    ///     <c>color-mix(in srgb, currentColor <paramref name="percent" />, transparent)</c> for an
    ///     arbitrary currentColor — the status tints (destructive buttons, error rows) where the
    ///     wash is a tint of the text colour rather than of the window foreground.
    /// </summary>
    public static Color Tint(Color currentColor, float percent, ThemeData theme) => AdwPalette.Wash(
        tint: currentColor,
        percent: percent,
        over: AdwPalette.For(theme).WindowBg
    );

    // ── Shared building blocks ────────────────────────────────────────────────

    /// <summary>
    ///     Fades <paramref name="box" />'s fill to match <paramref name="pressable" />'s hover/press
    ///     state, the ~100ms Adwaita transition. Replaces the hand-written
    ///     <c>OnStateChanged = () => { box.Fill = …; box.MarkNeedsPaint(); }</c> closure that every
    ///     button-like control used to carry — those snapped instead of fading.
    /// </summary>
    internal static void WireFill(
        this Pressable pressable,
        DecoratedBox box,
        ThemeData theme,
        AdwButtonStyle style = AdwButtonStyle.Flat,
        Func<bool>? enabled = null,
        Func<bool>? @checked = null)
    {
        var fill = new FillTransition(c =>
            {
                box.Fill = c;
                box.MarkNeedsPaint();
            }
        );
        fill.Snap(
            ButtonFill(
                theme: theme,
                style: style,
                hovered: false,
                pressed: false,
                enabled: enabled?.Invoke() ?? true,
                @checked: @checked?.Invoke() ?? false
            )
        );
        pressable.OnStateChanged = () => fill.Target(
            ButtonFill(
                theme: theme,
                style: style,
                hovered: pressable.Hovered,
                pressed: pressable.Pressed,
                enabled: enabled?.Invoke() ?? true,
                @checked: @checked?.Invoke() ?? false
            )
        );
    }

    /// <summary>
    ///     The standard Adwaita button interior: a minimum height, centred content and the
    ///     symmetric horizontal padding. Duplicated verbatim in Button, SplitButton, ToggleButton
    ///     and ToggleGroup before this existed.
    /// </summary>
    internal static Widget ButtonBody(
        Widget content,
        float height = AdwMetrics.ButtonHeight,
        float paddingX = AdwMetrics.ButtonPaddingX)
    {
        return new ConstrainedBox(
            constraints: new Constraints(minHeight: height),
            child: new Align(
                alignment: Alignment.Center,
                child: new Padding(padding: EdgeInsets.Symmetric(paddingX), child: content)
            ) {
                WidthFactor = 1f,
                HeightFactor = 1f,
            }
        );
    }

    /// <summary>
    ///     Assign a property backing field and rebuild if it actually changed. Without this, a
    ///     plain auto-property on a <see cref="ComposedWidget" /> silently does nothing after the
    ///     first build — the Build result is cached.
    /// </summary>
    internal static void Set<T>(this ComposedWidget widget, ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(x: field, y: value)) return;
        field = value;
        widget.Invalidate();
    }
}
