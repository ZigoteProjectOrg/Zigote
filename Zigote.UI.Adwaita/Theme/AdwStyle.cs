namespace Zigote.UI.Adwaita;

/// <summary>Adwaita button appearance classes (.suggested-action, .destructive-action, .flat, …).</summary>
public enum AdwButtonStyle
{
    /// <summary>The neutral translucent fill.</summary>
    Regular,

    /// <summary>.suggested-action — solid accent.</summary>
    Suggested,

    /// <summary>.destructive-action — solid red.</summary>
    Destructive,

    /// <summary>.flat — transparent, fills on hover. (Header bar and toolbar buttons.)</summary>
    Flat,
}

/// <summary>
///     Shared state → color resolution for Adwaita controls, mirroring the stylesheet's
///     hover/active/disabled rules. Disabled controls additionally drop to
///     <see cref="DisabledOpacity" /> whole-widget opacity, as Adwaita does.
/// </summary>
public static class AdwStyle
{
    public const float DisabledOpacity = 0.5f;

    /// <summary>Background fill for a button-like control in a given interaction state.</summary>
    public static Color ButtonFill(
        ThemeData theme,
        AdwButtonStyle style,
        bool hovered = false,
        bool pressed = false,
        bool enabled = true)
    {
        var p = AdwPalette.For(theme);
        switch (style)
        {
            case AdwButtonStyle.Suggested:
                return Solid(
                    theme.Accent,
                    hovered,
                    pressed,
                    enabled
                );
            case AdwButtonStyle.Destructive:
                return Solid(
                    theme.Danger,
                    hovered,
                    pressed,
                    enabled
                );
            case AdwButtonStyle.Flat:
                if (!enabled)
                    return Color.Rgba(
                        0,
                        0,
                        0,
                        0f
                    );
                if (pressed) return p.ButtonFillHover;
                if (hovered) return p.ButtonFill.WithAlpha(p.ButtonFill.A * 0.875f);
                return Color.Rgba(
                    0,
                    0,
                    0,
                    0f
                );
            default:
                if (!enabled) return p.ButtonFillDisabled;
                if (pressed) return p.ButtonFillActive;
                if (hovered) return p.ButtonFillHover;
                return p.ButtonFill;
        }
    }

    /// <summary>
    ///     Hover/press modulation for a solid-color (suggested/destructive) fill. Adwaita overlays
    ///     <c>alpha(currentColor, .1)</c> on hover and <c>.3</c> on press, and currentColor on a
    ///     solid accent fill is white — so both states brighten. (This used to darken on press,
    ///     which is the GTK3 behaviour, not Adwaita's.)
    /// </summary>
    public static Color Solid(Color baseColor, bool hovered, bool pressed, bool enabled = true)
    {
        if (!enabled) return baseColor;
        if (pressed) return baseColor.Lighten(0.3f);
        if (hovered) return baseColor.Lighten(0.1f);
        return baseColor;
    }

    /// <summary>Foreground for a button style (accent-fg on solid fills, window-fg otherwise).</summary>
    public static Color ButtonForeground(ThemeData theme, AdwButtonStyle style)
    {
        return style is AdwButtonStyle.Suggested or AdwButtonStyle.Destructive
            ? theme.OnPrimary
            : theme.OnBackground;
    }

    /// <summary>
    ///     Hover/press wash for activatable rows and list items. Adwaita uses the same neutral
    ///     ladder as buttons here (hover .07–.10, active .16), not a lighter one — this used to
    ///     return ThemeData.Fill4/Fill2 (.03/.06), which read as no feedback at all.
    /// </summary>
    public static Color RowFill(ThemeData theme, bool hovered, bool pressed)
    {
        var p = AdwPalette.For(theme);
        if (pressed) return p.ButtonFillActive;
        if (hovered) return p.ButtonFillHover;
        return Color.Transparent;
    }

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
        Func<bool>? enabled = null)
    {
        var fill = new FillTransition(c =>
            {
                box.Fill = c;
                box.MarkNeedsPaint();
            }
        );
        fill.Snap(
            ButtonFill(
                theme,
                style,
                false,
                false,
                enabled?.Invoke() ?? true
            )
        );
        pressable.OnStateChanged = () => fill.Target(
            ButtonFill(
                theme,
                style,
                pressable.Hovered,
                pressable.Pressed,
                enabled?.Invoke() ?? true
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
            new Constraints(minHeight: height),
            new Align(Alignment.Center, new Padding(EdgeInsets.Symmetric(paddingX), content)) {
                WidthFactor = 1f,
                HeightFactor = 1f,
            }
        );
    }

    /// <summary>
    ///     Assign a property backing field and rebuild if it actually changed. Without this, a
    ///     plain auto-property on a <see cref="StatelessWidget" /> silently does nothing after the
    ///     first build — the Build result is cached.
    /// </summary>
    internal static void Set<T>(this StatelessWidget widget, ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        widget.Invalidate();
    }
}