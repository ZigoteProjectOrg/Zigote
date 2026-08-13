using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.TextShaping;

namespace Zigote.UI.Adwaita;

/// <summary>What an <see cref="AdwMenuItem" /> represents, and how the row is drawn.</summary>
public enum AdwMenuItemRole
{
    /// <summary>A plain command row.</summary>
    Normal,

    /// <summary>One option of a mutually exclusive group — leading radio indicator.</summary>
    Radio,

    /// <summary>An independent toggle — leading check indicator.</summary>
    Check,

    /// <summary>A dim caption naming the group below it. Never hoverable or activatable.</summary>
    Header,
}

/// <summary>One activatable row of an <see cref="AdwMenuButton" /> menu.</summary>
public sealed class AdwMenuItem
{
    public AdwMenuItem(string label, Action? onActivated = null)
    {
        Label = label;
        OnActivated = onActivated;
    }

    public string Label { get; set; }

    /// <summary>Display-only shortcut label ("Ctrl+,"), right-aligned dim; no key handling.</summary>
    public string? Accel { get; set; }

    /// <summary>Disabled rows render at 50% opacity and ignore hover/activation.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Row kind — plain command, radio option, checkbox, or group caption.</summary>
    public AdwMenuItemRole Role { get; set; } = AdwMenuItemRole.Normal;

    /// <summary>
    ///     Whether the indicator is filled. Only meaningful for <see cref="AdwMenuItemRole.Radio" />
    ///     and <see cref="AdwMenuItemRole.Check" />.
    /// </summary>
    public bool Checked { get; set; }

    public Action? OnActivated { get; set; }

    /// <summary>
    ///     A group caption ("Temperature Unit"), the way GNOME's primary menus label a radio group.
    ///     Disabled by construction, so hover, keyboard navigation and activation skip it.
    /// </summary>
    public static AdwMenuItem Header(string label)
    {
        return new AdwMenuItem(label) {
            Role = AdwMenuItemRole.Header,
            Enabled = false,
        };
    }

    /// <summary>One option of a radio group.</summary>
    public static AdwMenuItem Radio(string label, bool selected, Action? onActivated = null)
    {
        return new AdwMenuItem(label: label, onActivated: onActivated) {
            Role = AdwMenuItemRole.Radio,
            Checked = selected,
        };
    }
}

/// <summary>
///     AdwMenuButton — the GNOME primary-menu (hamburger) button: a flat circular 34px headerbar
///     icon button that opens a popover menu of <see cref="AdwMenuItem" /> rows grouped into
///     hairline-separated <see cref="Sections" />, anchored below the button.
/// </summary>
public sealed class AdwMenuButton : ComposedWidget
{
    private string _iconName;

    public AdwMenuButton(string iconName = MaterialIcons.Menu) => _iconName = iconName;

    public string IconName
    {
        get => _iconName;
        set => this.Set(field: ref _iconName, value: value);
    }

    // No rebuild on either: both are read when the button opens the popover, never during Build.

    /// <summary>Menu rows; each outer list is a section split from the next by a hairline.</summary>
    public List<List<AdwMenuItem>> Sections { get; init; } = [];

    public float MenuWidth { get; set; } = 220f;

    protected override Widget Build(BuildContext context)
    {
        // .flat + .circular is exactly what AdwButton already builds (34px square, radius 17,
        // hover/press fade); Circular drops the label widget so "Menu" only reaches semantics.
        var button = new AdwButton("Menu") {
            IconName = IconName,
            Style = AdwButtonStyle.Flat,
            Circular = true,
        };
        button.OnPressed = () => OpenMenu(button.Bounds);
        return button;
    }

    private void OpenMenu(Rect anchor)
    {
        var app = App.Active;
        if (app is null) return;
        bool any = false;
        foreach (var s in Sections) any |= s.Count > 0;
        if (!any) return;
        new AdwMenuPopover(
            app: app,
            sections: Sections,
            anchor: anchor,
            minWidth: MenuWidth
        ).Show();
    }
}

/// <summary>
///     The menu popover behind <see cref="AdwMenuButton" /> — <see cref="AdwPopoverBase" />'s
///     overlay skeleton hosting label+accel rows with section separators, group captions, a
///     rounded row highlight and Up/Down/Enter keyboard navigation that skips separators,
///     captions and disabled rows.
/// </summary>
internal sealed class AdwMenuPopover : AdwPopoverBase
{
    private const float Pad = 6f; // vertical padding + row inset from the card edge
    private const float TextPad = 12f; // row horizontal text padding from the card edge
    private const float AccelGap = 24f; // min gap between label and accel
    private const float AccelFs = 12.5f;
    private const float SepMargin = 6f;
    private const float SepH = (SepMargin * 2f) + 1f;
    private const float HeaderH = 26f; // group caption row
    private const float HeaderFs = 12f;
    private const float IndicatorSize = 16f;
    private const float IndicatorGap = 8f;
    private const float IndentW = IndicatorSize + IndicatorGap;

    // Flattened sections: a null entry is a separator hairline.
    private readonly List<AdwMenuItem?> _entries = [];
    private readonly bool _hasIndicators;
    private readonly float _minWidth;

    private float[] _entryH = [];
    private float[] _entryY = [];

    public AdwMenuPopover(App app, List<List<AdwMenuItem>> sections, Rect anchor, float minWidth)
        : base(app: app, anchor: anchor)
    {
        _minWidth = minWidth;
        foreach (var section in sections)
        {
            if (section.Count == 0) continue;
            if (_entries.Count > 0) _entries.Add(null);
            _entries.AddRange(section);
        }

        // GNOME indents every label in a menu that has any indicator, so radio dots and plain
        // commands share one text column instead of zig-zagging.
        foreach (var item in _entries)
        {
            if (item?.Role is AdwMenuItemRole.Radio or AdwMenuItemRole.Check)
                _hasIndicators = true;
        }
    }

    public override Size Measure(Constraints c)
    {
        Theme = ThemeProvider.Of(BuildContext.Current);
        Screen = new Size(width: c.MaxWidth, height: c.MaxHeight);
        RowH = MediaQuery.Of(BuildContext.Current).SizeClass == WindowSizeClass.Compact
            ? MathF.Max(x: AdwMetrics.MenuRowHeight, y: ControlMetrics.MinTouchTarget)
            : AdwMetrics.MenuRowHeight;

        float fs = Theme.FontSizeBody;
        float indent = _hasIndicators ? IndentW : 0f;
        float widest = 0f;
        if (_entryY.Length != _entries.Count) _entryY = new float[_entries.Count];
        if (_entryH.Length != _entries.Count) _entryH = new float[_entries.Count];
        float y = Pad;
        for (int i = 0; i < _entries.Count; i++)
        {
            _entryY[i] = y;
            var item = _entries[i];
            if (item is null)
            {
                _entryH[i] = SepH;
                y += SepH;
                continue;
            }

            bool header = item.Role == AdwMenuItemRole.Header;
            _entryH[i] = header ? HeaderH : RowH;
            y += _entryH[i];
            float w = indent + TextMeasure.Width(
                text: item.Label,
                fontSize: header ? HeaderFs : fs
            );
            if (item.Accel is { Length: > 0 } accel)
                w += AccelGap + TextMeasure.Width(text: accel, fontSize: AccelFs);
            widest = MathF.Max(x: widest, y: w);
        }

        PopupW = MathF.Max(x: _minWidth, y: widest + (TextPad * 2f));
        PopupW = MathF.Min(x: PopupW, y: MathF.Max(x: 120f, y: Screen.Width - Spacing.Lg));
        // ponytail: no scrolling — height clamps to the screen; add AdwPopover's scroll
        // machinery if a menu ever outgrows the window.
        PopupH = MathF.Min(x: y + Pad, y: Screen.Height - 16f);

        return Screen;
    }

    protected override void PaintRows(PaintList paint, Rect mr)
    {
        var p = AdwPalette.For(Theme);
        float fs = Theme.FontSizeBody;
        float indent = _hasIndicators ? IndentW : 0f;
        paint.AddClipStart(mr);
        for (int i = 0; i < _entries.Count; i++)
        {
            var item = _entries[i];
            float rowY = mr.Y + _entryY[i];
            if (item is null)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: mr.X,
                        y: rowY + SepMargin,
                        width: mr.Width,
                        height: 1f
                    ),
                    color: Theme.Separator
                );
                continue;
            }

            float rowH = _entryH[i];

            // A group caption: dim, smaller, no highlight — it names the rows under it.
            if (item.Role == AdwMenuItemRole.Header)
            {
                paint.AddText(
                    text: item.Label,
                    baselineX: mr.X + TextPad,
                    baselineY: rowY + ((rowH - HeaderFs) / 2f) + (HeaderFs * 0.9f),
                    color: p.DimLabel,
                    fontSize: HeaderFs
                );
                continue;
            }

            if (item.Enabled)
            {
                // `modelbutton { border-radius: $menu_radius }` with the $selected_* ladder —
                // a menu item highlights harder than a boxed-list row does, and rounds to 9px.
                var wash = AdwStyle.MenuRowFill(
                    theme: Theme,
                    hovered: i == Hovered,
                    pressed: i == PressedRow
                );
                if (wash.A > 0f)
                {
                    paint.AddRect(
                        bounds: new Rect(
                            x: mr.X + Pad,
                            y: rowY,
                            width: PopupW - (Pad * 2f),
                            height: rowH
                        ),
                        color: wash,
                        radius: AdwMetrics.MenuRadius
                    );
                }
            }

            float alpha = item.Enabled ? 1f : AdwStyle.DisabledOpacity;
            var fg = Theme.OnBackground.WithAlpha(Theme.OnBackground.A * alpha);

            // GNOME's menu indicators are absent until set: nothing at all for an unchecked row,
            // then a bare accent check (a small filled dot for a radio) — never an empty box or
            // ring, which is what the Material check_box_outline_blank glyph drew here.
            if (item.Checked && item.Role is AdwMenuItemRole.Radio or AdwMenuItemRole.Check)
            {
                bool radio = item.Role == AdwMenuItemRole.Radio;
                var accent = Theme.PrimaryDark;
                Icons.Draw(
                    paint: paint,
                    glyph: radio ? Icons.Dot : Icons.Check,
                    box: new Rect(
                        x: mr.X + TextPad,
                        y: rowY + ((rowH - IndicatorSize) / 2f),
                        width: IndicatorSize,
                        height: IndicatorSize
                    ),
                    color: accent.WithAlpha(accent.A * alpha),
                    size: radio ? 10f : IndicatorSize
                );
            }

            float baseline = rowY + ((rowH - fs) / 2f) + (fs * 0.8f);
            paint.AddText(
                text: item.Label,
                baselineX: mr.X + TextPad + indent,
                baselineY: baseline,
                color: fg,
                fontSize: fs
            );
            if (item.Accel is { Length: > 0 } accel)
            {
                paint.AddText(
                    text: accel,
                    baselineX: mr.Right - TextPad -
                               TextMeasure.Width(text: accel, fontSize: AccelFs),
                    baselineY: rowY + ((rowH - AccelFs) / 2f) + (AccelFs * 0.8f),
                    color: p.DimLabel.WithAlpha(p.DimLabel.A * alpha),
                    fontSize: AccelFs
                );
            }
        }

        paint.AddClipEnd();
    }

    protected override int RowAt(Rect mr, float y)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i] is { Enabled: true }
                && y >= mr.Y + _entryY[i] && y < mr.Y + _entryY[i] + _entryH[i])
                return i;
        }

        return -1;
    }

    protected override void Activate(int idx)
    {
        Dismiss();
        _entries[idx]?.OnActivated?.Invoke();
    }

    public override void OnPointerDown(Offset point)
    {
        var mr = PopupRect();
        if (!mr.Contains(px: point.X, py: point.Y))
        {
            Dismiss();
            return;
        }

        int idx = RowAt(mr: mr, y: point.Y);
        if (idx < 0) return;
        PressedRow = Hovered = idx;
        MarkNeedsPaint();
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;
        switch ((KeyCode)scancode)
        {
            case KeyCode.Down:
                MoveHighlight(+1);
                break;
            case KeyCode.Up:
                MoveHighlight(-1);
                break;
            case KeyCode.Enter or KeyCode.Space:
                if (Hovered >= 0 && _entries[Hovered] is { Enabled: true }) Activate(Hovered);
                break;
        }
    }

    private void MoveHighlight(int dir)
    {
        int n = _entries.Count;
        int i = Hovered;
        for (int step = 0; step < n; step++)
        {
            i = ((i < 0 ? dir > 0 ? -1 : 0 : i) + dir + n) % n;
            if (_entries[i] is not { Enabled: true }) continue;
            Hovered = i;
            MarkNeedsPaint();
            return;
        }
    }
}
