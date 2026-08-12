// The Label string property shadows the Label widget type inside this class, so reference the
// widget type through an alias.

using LabelWidget = Zigote.UI.Widgets.Controls.Label;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwToggleButton — an <see cref="AdwButton" />-looking button that latches: active shows the
///     pressed (ButtonFillActive) fill, or the solid accent when <see cref="Style" /> is
///     <see cref="AdwButtonStyle.Suggested" />.
/// </summary>
public sealed class AdwToggleButton : ComposedWidget
{
    private bool _active;
    private Action? _applyColors;
    private bool _enabled = true;
    private string _label;
    private AdwButtonStyle _style = AdwButtonStyle.Regular;

    public AdwToggleButton(string label = "", bool active = false, Action<bool>? onToggled = null)
    {
        _label = label;
        _active = active;
        OnToggled = onToggled;
    }

    public string Label
    {
        get => _label;
        set => this.Set(ref _label, value);
    }

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            _applyColors?.Invoke();
        }
    }

    public Action<bool>? OnToggled { get; set; }

    public AdwButtonStyle Style
    {
        get => _style;
        set => this.Set(ref _style, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => this.Set(ref _enabled, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        const float radius = AdwMetrics.ControlRadius;

        var label = new LabelWidget(Label, AdwTypography.Heading) { MaxLines = 1 };
        var box = new DecoratedBox {
            Radius = radius,
            Child = AdwStyle.ButtonBody(label),
        };
        var pressable = new Pressable {
            Child = box,
            Enabled = Enabled,
            FocusRadius = radius,
            SemanticsLabel = Label,
        };
        pressable.OnPressed = () =>
        {
            Active = !Active;
            OnToggled?.Invoke(Active);
        };
        pressable.OnStateChanged = () => _applyColors!.Invoke();

        var fill = new FillTransition(c =>
            {
                box.Fill = c;
                box.MarkNeedsPaint();
            }
        );
        _applyColors = () =>
        {
            // Also the accessible checked state: this runs on every Active write, so a
            // programmatic toggle (which never rebuilds) stays in sync for screen readers.
            pressable.Checked = Active;
            // `:checked` is its own rung of every ladder in _buttons.scss — 30/35/40% for a raised
            // button, the $selected_* steps for a flat one, a 15% ink overlay on a solid accent —
            // not "the pressed fill, latched", which is what this used to paint.
            label.Color = AdwStyle.ButtonForeground(theme, Style);
            var target = AdwStyle.ButtonFill(
                theme,
                Style,
                pressable.Hovered,
                pressable.Pressed,
                Enabled,
                Active
            );

            fill.Target(target); // first call (right below) snaps, later state changes fade
            box.MarkNeedsPaint();
        };
        _applyColors();

        // `.flat:disabled:not(:checked)` fades further than a raised button does.
        return Enabled
            ? pressable
            : new Opacity(
                Style is AdwButtonStyle.Flat && !Active
                    ? AdwStyle.StrongDisabledOpacity
                    : AdwStyle.DisabledOpacity,
                pressable
            );
    }
}