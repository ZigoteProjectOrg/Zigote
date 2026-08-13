namespace Zigote.UI.Adwaita;

/// <summary>AdwToggle — one segment of an <see cref="AdwToggleGroup" />: a label, an icon, or both.</summary>
public sealed record AdwToggle(
    string? Label = null,
    string? IconName = null,
    string? Tooltip = null);

/// <summary>
///     AdwToggleGroup — the libadwaita 1.7 joined toggle group: one rounded container of segments
///     separated by 1px hairlines, exactly one active (ButtonFillActive); inactive segments are
///     transparent and fill on hover. <see cref="Flat" /> drops the group background,
///     <see cref="Round" /> makes it a pill.
/// </summary>
public sealed class AdwToggleGroup : ComposedWidget
{
    private readonly IReadOnlyList<AdwToggle> _toggles;
    private int _active;
    private Action? _applyColors;
    private bool _enabled = true;
    private bool _flat;
    private bool _round;

    public AdwToggleGroup(IReadOnlyList<AdwToggle> toggles, int active = 0,
        Action<int>? onActive = null)
    {
        _toggles = toggles;
        _active = active;
        OnActive = onActive;
    }

    public AdwToggleGroup(IReadOnlyList<string> labels, int active = 0,
        Action<int>? onActive = null)
        : this([.. labels.Select(l => new AdwToggle(l))], active, onActive)
    {
    }

    public int Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            _applyColors?.Invoke();
        }
    }

    public Action<int>? OnActive { get; set; }

    /// <summary>No group background — segments only.</summary>
    public bool Flat
    {
        get => _flat;
        set => this.Set(ref _flat, value);
    }

    /// <summary>Pill-shaped group instead of the 9px control radius.</summary>
    public bool Round
    {
        get => _round;
        set => this.Set(ref _round, value);
    }

    /// <summary>
    ///     Insensitive group: segments stop reacting and the whole control drops to 50% opacity,
    ///     the way Adwaita dims a disabled widget wholesale rather than restyling its parts.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => this.Set(ref _enabled, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        // libadwaita insets the toggles by --group-padding (3px) inside the group's own box and
        // shrinks their radius by the same amount, so the active toggle reads as a card sitting IN
        // the group rather than a segment cut out of it. Flat groups have no box, so no inset.
        var pad = Flat ? 0f : AdwMetrics.ToggleGroupPadding;
        var groupRadius = Round ? AdwMetrics.RoundToggleRadius : AdwMetrics.ControlRadius;
        var radius = MathF.Max(0f, groupRadius - pad);
        // `> toggle { min-height: calc(34px - var(---group-padding) * 2) }` — the toggles shrink so
        // the GROUP still stands 34px tall, the height of every other control on the row.
        var height = AdwMetrics.ButtonHeight - pad * 2f;
        // .text-button padding is 11px minus the group padding; .round widens it to 15px.
        var paddingX = MathF.Max(0f, (Round ? 15f : 11f) - pad);

        var boxes = new DecoratedBox[_toggles.Count];
        var contents = new AdwButtonContent[_toggles.Count];
        var pressables = new Pressable[_toggles.Count];
        var separators = new Container[_toggles.Count];

        var row = new Row(mainAxisSize: MainAxisSize.Min);
        for (var i = 0; i < _toggles.Count; i++)
        {
            if (i > 0)
            {
                // `> separator { margin: calc(6px - var(---group-padding)) 1px }` — 9px on a round
                // group. The separator either side of the checked toggle fades out (`.hidden`),
                // which is what stops the raised segment from being visually fenced in.
                separators[i] = new Container {
                    Width = 1f,
                    Height = MathF.Max(0f, height - ((Round ? 9f : 6f) - pad) * 2f),
                    Background = Flat ? Color.Transparent : p.Border,
                };
                row.Children.Add(separators[i]);
            }

            var index = i;
            var toggle = _toggles[i];
            contents[i] = new AdwButtonContent(toggle.IconName, toggle.Label ?? "");
            boxes[i] = new DecoratedBox {
                // The segment's OWN radius — the group box doesn't clip, so without this the
                // active and hover fills paint as square blocks inside a rounded group.
                Radius = radius,
                Child = AdwStyle.ButtonBody(
                    contents[i],
                    height,
                    toggle.Label is { Length: > 0 } ? paddingX : AdwMetrics.ToolbarPadding
                ),
            };
            pressables[i] = new Pressable {
                Child = boxes[i],
                FocusRadius = radius,
                SemanticsLabel = toggle.Label ?? toggle.Tooltip,
                Enabled = _enabled,
            };
            pressables[i].OnPressed = () =>
            {
                if (Active == index) return;
                Active = index;
                OnActive?.Invoke(index);
            };
            pressables[i].OnStateChanged = () => _applyColors!.Invoke();
            row.Children.Add(
                toggle.Tooltip is { } tip ? new Tooltip(tip, pressables[i]) : pressables[i]
            );
        }

        var fills = new FillTransition[boxes.Length];
        for (var i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            fills[i] = new FillTransition(c =>
                {
                    box.Fill = c;
                    box.MarkNeedsPaint();
                }
            );
        }

        _applyColors = () =>
        {
            for (var i = 0; i < boxes.Length; i++)
            {
                // Selection lives here, not in OnPressed: this runs for a programmatic Active
                // change too, which otherwise left the segments reporting the old selection.
                var active = i == Active;
                pressables[i].SelectedState = active;

                // The checked segment of a non-flat group is --active-toggle-bg-color: a raised
                // white (dark: white 20%) card with the card shadow under it, NOT a darker fill.
                // A flat group has no card to raise, so it keeps the neutral $selected_* ladder.
                boxes[i].Elevation = active && !Flat ? AdwMetrics.CardShadow : null;
                contents[i].Color = active && !Flat ? p.ActiveToggleFg : theme.OnBackground;

                fills[i].Target(
                    active
                        ? Flat
                            ? pressables[i].Pressed ? p.SelectedFillActive
                            : pressables[i].Hovered ? p.SelectedFillHover
                            : p.SelectedFill
                            : p.ActiveToggleBg
                        : pressables[i].Pressed ? p.ActiveFill
                        : pressables[i].Hovered ? p.HoverFill
                        : Color.Transparent
                );

                if (separators[i] is { } sep)
                    sep.Background = Flat || active || i - 1 == Active
                        ? Color.Transparent
                        : p.Border;
            }
        };
        _applyColors();

        Widget group = new DecoratedBox {
            Radius = groupRadius,
            Fill = Flat ? Color.Transparent : p.ButtonFill,
            Child = pad > 0f ? new Padding(EdgeInsets.All(pad), row) : row,
        };
        return Enabled ? group : new Opacity(AdwStyle.DisabledOpacity, group);
    }
}
