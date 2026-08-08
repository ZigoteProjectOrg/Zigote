using Zigote.UI.Material;
using Zigote.UI.Semantics;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwCheckButton — the GNOME check button: an 18px rounded box (translucent fill + hairline
///     border unchecked, accent fill + white tick checked) with a body-size label; the whole row is
///     clickable. Disabled drops the row to <see cref="AdwStyle.DisabledOpacity" />.
/// </summary>
public class AdwCheckButton : StatefulWidget
{
    private bool _enabled = true;
    private string _label;
    private bool _value;

    public AdwCheckButton(string label = "", bool value = false, Action<bool>? onChanged = null)
    {
        _label = label;
        _value = value;
        OnChanged = onChanged;
    }

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            MarkNeedsBuild();
        }
    }

    public bool Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            MarkNeedsBuild();
        }
    }

    public Action<bool>? OnChanged { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            MarkNeedsBuild();
        }
    }

    internal virtual bool IsRadio => false;

    protected override WidgetState CreateState()
    {
        return new AdwCheckButtonState();
    }

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

    public override int DebugStateHash()
    {
        return HashCode.Combine(Value, Enabled, base.DebugStateHash());
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
        : base(label, value, onChanged)
    {
    }

    internal override bool IsRadio => true;
}

internal sealed class AdwCheckButtonState : WidgetState<AdwCheckButton>
{
    private readonly DecoratedBox _box = new();
    private readonly CheckGlyph _check = new() { GlyphSize = AdwMetrics.CheckSize };

    // RadioDotGlyph's dot is GlyphSize × 0.56 — 14.3 yields the spec's 8px dot inside the 18px ring.
    private readonly RadioDotGlyph _dot = new() { GlyphSize = 14.3f };
    private readonly Label _text = new("", AdwTypography.Body);
    private Opacity _fade = null!;
    private FillTransition _fill = null!;
    private Pressable _root = null!;
    private Row _row = null!;
    private ThemeData _theme = ThemeData.Dark;

    public override void InitState()
    {
        _fill = new FillTransition(c =>
            {
                _box.Fill = c;
                _box.MarkNeedsPaint();
            }
        );
        _row = new Row(spacing: 8f, mainAxisSize: MainAxisSize.Min);
        _fade = new Opacity(1f) { Child = _row };
        _root = new Pressable {
            Child = _fade,
            FocusRadius = AdwMetrics.ControlRadius,
            OnStateChanged = ApplyColors,
            OnPressed = Activate,
        };
    }

    public override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);
        var w = Widget;

        _box.Radius = w.IsRadio ? AdwMetrics.CheckSize / 2f : 5f;
        _box.Child = SizedBox.Square(
            AdwMetrics.CheckSize,
            new Center(w.IsRadio ? _dot : _check)
        );

        _text.Text = w.Label;
        _row.Children.Clear();
        _row.Children.Add(_box);
        if (w.Label.Length > 0) _row.Children.Add(_text);

        _fade.Value = w.Enabled ? 1f : AdwStyle.DisabledOpacity;
        _root.Enabled = w.Enabled;
        _root.Role = w.IsRadio ? SemanticsRole.RadioButton : SemanticsRole.Checkbox;
        _root.Checked = w.Value;
        _root.SemanticsLabel = w.Label.Length > 0 ? w.Label : null;

        ApplyColors();
        return _root;
    }

    private void Activate()
    {
        var w = Widget;
        if (w.IsRadio)
        {
            if (w.Value) return; // a selected radio stays selected
            w.Value = true;
            w.OnChanged?.Invoke(true);
        }
        else
        {
            w.Value = !w.Value;
            w.OnChanged?.Invoke(w.Value);
        }
    }

    private void ApplyColors()
    {
        var w = Widget;
        var p = AdwPalette.For(_theme);
        var hovered = _root.Hovered && w.Enabled;
        var pressed = _root.Pressed && w.Enabled;

        // Fill fades ~100ms; the hairline border flips instantly (a fading border reads as smear
        // at 18px, and Adwaita's checks switch fast anyway).
        if (w.Value)
        {
            _fill.Target(AdwStyle.Solid(_theme.Accent, hovered, pressed));
            _box.BorderColor = Color.Transparent;
        }
        else
        {
            _fill.Target(pressed ? _theme.Fill1 : hovered ? _theme.Fill2 : _theme.Fill3);
            _box.BorderColor = p.Border;
        }

        _check.Color = Color.White;
        _check.Visible = w.Value && !w.IsRadio;
        _dot.Color = Color.White;
        _dot.Visible = w.Value && w.IsRadio;
        _text.Color = _theme.OnBackground;
        _box.MarkNeedsPaint();
    }
}