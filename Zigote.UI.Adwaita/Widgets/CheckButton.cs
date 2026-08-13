using Zigote.UI.Material;
using Zigote.UI.Semantics;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwCheckButton — the GNOME check button: an 18px rounded box (translucent fill + hairline
///     border unchecked, accent fill + white tick checked) with a body-size label; the whole row is
///     clickable. Disabled drops the row to <see cref="AdwStyle.DisabledOpacity" />.
/// </summary>
public class AdwCheckButton : ComposedWidget
{
    private readonly DecoratedBox _box = new();
    private readonly CheckGlyph _check = new() { GlyphSize = AdwMetrics.CheckSize };

    // RadioDotGlyph's dot is GlyphSize × 0.56 — 14.3 yields the spec's 8px dot inside the 18px ring.
    private readonly RadioDotGlyph _dot = new() { GlyphSize = 14.3f };
    private readonly Opacity _fade;
    private readonly FillTransition _fill;
    private readonly Pressable _root;
    private readonly Row _row;
    private readonly Label _text = new(text: "", style: AdwTypography.Body);

    private bool _enabled = true;
    private string _label;
    private ThemeData _theme = ThemeData.Dark;
    private bool _value;

    public AdwCheckButton(string label = "", bool value = false, Action<bool>? onChanged = null)
    {
        _label = label;
        _value = value;
        OnChanged = onChanged;

        _fill = new FillTransition(c =>
            {
                _box.Fill = c;
                _box.MarkNeedsPaint();
            }
        );
        // checkbutton { border-spacing: 4px } — the 20px indicator already carries 3px of its own
        // padding, so a wider gap detaches the label from its check.
        _row = new Row(spacing: 4f, mainAxisSize: MainAxisSize.Min);
        _fade = new Opacity(1f) { Child = _row };
        _root = new Pressable {
            Child = _fade,
            FocusRadius = AdwMetrics.ControlRadius,
            OnStateChanged = ApplyColors,
            OnPressed = Activate,
        };
    }

    public string Label
    {
        get => _label;
        set => SetBuild(field: ref _label, value: value);
    }

    public bool Value
    {
        get => _value;
        set => SetBuild(field: ref _value, value: value);
    }

    public Action<bool>? OnChanged { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set => SetBuild(field: ref _enabled, value: value);
    }

    internal virtual bool IsRadio => false;

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is AdwCheckButton c)
        {
            Label = c.Label;
            Value = c.Value;
            OnChanged = c.OnChanged;
            Enabled = c.Enabled;
        }
    }

    public override int DebugStateHash() => HashCode.Combine(
        value1: Value,
        value2: Enabled,
        value3: base.DebugStateHash()
    );

    protected override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);

        // check { border-radius: $check_radius } / radio { border-radius: 100% }.
        _box.Radius = IsRadio ? AdwMetrics.CheckSize / 2f : AdwMetrics.CheckRadius;
        _box.Child = SizedBox.Square(
            size: AdwMetrics.CheckSize,
            child: new Center(IsRadio ? _dot : _check)
        );

        _text.Text = Label;
        _row.Children.Clear();
        _row.Children.Add(_box);
        if (Label.Length > 0) _row.Children.Add(_text);

        _fade.Value = Enabled ? 1f : AdwStyle.DisabledOpacity;
        _root.Enabled = Enabled;
        _root.Role = IsRadio ? SemanticsRole.RadioButton : SemanticsRole.Checkbox;
        _root.Checked = Value;
        _root.SemanticsLabel = Label.Length > 0 ? Label : null;

        ApplyColors();
        return _root;
    }

    private void Activate()
    {
        if (IsRadio)
        {
            if (Value) return; // a selected radio stays selected
            Value = true;
            OnChanged?.Invoke(true);
        }
        else
        {
            Value = !Value;
            OnChanged?.Invoke(Value);
        }
    }

    private void ApplyColors()
    {
        var p = AdwPalette.For(_theme);
        bool hovered = _root.Hovered && Enabled;
        bool pressed = _root.Pressed && Enabled;

        // Fill fades ~100ms; the ring flips instantly (a fading border reads as smear at 20px,
        // and Adwaita's checks switch fast anyway).
        if (Value)
        {
            _fill.Target(
                target: AdwStyle.Solid(
                    baseColor: _theme.Accent,
                    hovered: hovered,
                    pressed: pressed
                ),
                theme: _theme
            );
            _box.BorderColor = Color.Transparent;
        }
        else
        {
            // Unchecked is EMPTY with a 2px inset trough ring — `box-shadow: inset 0 0 0 2px
            // $trough_color` — except while pressed, where the ring gives way to a filled trough.
            _fill.Target(target: pressed ? p.TroughFillActive : Color.Transparent, theme: _theme);
            _box.BorderColor = pressed
                ? Color.Transparent
                : hovered
                    ? p.TroughFillHover
                    : p.TroughFill;
            _box.BorderWidth = AdwMetrics.CheckBorder;
        }

        _check.Color = p.AccentFg;
        _check.Visible = Value && !IsRadio;
        _dot.Color = p.AccentFg;
        _dot.Visible = Value && IsRadio;
        _text.Color = _theme.OnBackground;
        _box.MarkNeedsPaint();
    }
}

/// <summary>
///     AdwRadioButton — a circular <see cref="AdwCheckButton" /> with a white centre dot when
///     selected. Grouping is by convention: give each radio an <c>onChanged</c> that clears its
///     siblings' <see cref="AdwCheckButton.Value" /> (it only fires with <c>true</c>, on selection).
/// </summary>
public class AdwRadioButton : AdwCheckButton
{
    public AdwRadioButton(string label = "", bool value = false, Action<bool>? onChanged = null)
        : base(label: label, value: value, onChanged: onChanged) { }

    internal override bool IsRadio => true;
}
