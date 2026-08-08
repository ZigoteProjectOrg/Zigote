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
public sealed class AdwToggleGroup : StatelessWidget
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
        var radius = Round ? AdwMetrics.Pill : AdwMetrics.ControlRadius;

        var boxes = new DecoratedBox[_toggles.Count];
        var pressables = new Pressable[_toggles.Count];

        var row = new Row(mainAxisSize: MainAxisSize.Min);
        for (var i = 0; i < _toggles.Count; i++)
        {
            if (i > 0)
                row.Children.Add(
                    new Container {
                        Width = 1f,
                        Height = AdwMetrics.ButtonHeight,
                        Background = theme.Separator,
                    }
                );

            var index = i;
            var toggle = _toggles[i];
            boxes[i] = new DecoratedBox {
                Child = AdwStyle.ButtonBody(
                    new AdwButtonContent(toggle.IconName, toggle.Label ?? "")
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
                pressables[i].SelectedState = i == Active;
                fills[i].Target(
                    i == Active
                        ? p.ButtonFillActive
                        : pressables[i].Pressed
                            ? p.ButtonFillHover
                            : pressables[i].Hovered
                                ? p.ButtonFill
                                : Color.Transparent
                );
            }
        };
        _applyColors();

        Widget group = new ClipRRect(radius) {
            Child = new DecoratedBox {
                Radius = radius,
                Fill = Flat ? Color.Transparent : p.ButtonFill,
                Child = row,
            },
        };
        return Enabled ? group : new Opacity(AdwStyle.DisabledOpacity, group);
    }
}