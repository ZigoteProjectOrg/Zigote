using Zigote.UI.Semantics;

namespace Zigote.UI.Material;

/// <summary>
///     A flat, macOS-style checkbox: a rounded square that fills with the accent and shows a white
///     check
///     mark when ticked. Composed from <see cref="Pressable" /> over a <see cref="DecoratedBox" /> box
///     whose child is a <see cref="CheckGlyph" /> (the tick). Sizing, colour and shape come from
///     tokens.
/// </summary>
public class Checkbox : ComposedWidget
{
    private readonly DecoratedBox _box = new() { Radius = Radii.Xs };
    private readonly CheckGlyph _glyph = new();

    // Phone hit box: the tick keeps its 16pt look, centred in a finger-sized press area.
    private readonly SizedBox _touchBox = new(TouchMetrics.MinTarget, TouchMetrics.MinTarget);
    private readonly Pressable _root;
    private ThemeData _theme = ThemeData.Dark;

    private bool _value;
    private bool _enabled = true;
    private float _size = ControlMetrics.CheckboxSize;

    /// <summary>Named-argument constructor: <c>new Checkbox(value: true, onChanged: (v) => …)</c>.</summary>
    public Checkbox(bool value, Action<bool>? onChanged = null)
    {
        _value = value;
        OnChanged = onChanged;

        _box.Child = _glyph;
        _touchBox.Child = new Center(_box);
        _root = new Pressable {
            Child = _box,
            FocusRadius = Radii.Xs,
            OnStateChanged = ApplyColors,
            OnPressed = Toggle,
        };
    }

    public bool Value
    {
        get => _value;
        set => SetBuild(ref _value, value);
    }

    [Obsolete("Renamed — use Value.")]
    public bool Checked
    {
        get => Value;
        set => Value = value;
    }

    public Action<bool>? OnChanged { get; set; }

    public float Size
    {
        get => _size;
        set
        {
            if (Math.Abs(_size - value) < 0.01f) return;
            _size = value;
            MarkNeedsBuild();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetBuild(ref _enabled, value);
    }

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is Checkbox c)
        {
            Value = c.Value;
            OnChanged = c.OnChanged;
            Size = c.Size;
            Enabled = c.Enabled;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(Value, Enabled, base.DebugStateHash());
    }

    protected override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);

        _glyph.GlyphSize = Size;
        _root.Child = TouchMetrics.IsCompact ? _touchBox : _box;
        _root.Enabled = Enabled;
        _root.Role = SemanticsRole.Checkbox;
        _root.Checked = Value;

        ApplyColors();
        return _root;
    }

    private void Toggle()
    {
        Value = !Value;
        OnChanged?.Invoke(Value);
    }

    private void ApplyColors()
    {
        var hovered = _root.Hovered;
        var pressed = _root.Pressed;

        if (!Enabled)
        {
            if (Value)
            {
                _box.Fill = StateStyle.Disabled(_theme.Primary);
                _box.BorderColor = Color.Transparent;
                _glyph.Color = StateStyle.Disabled(_theme.OnPrimary);
                _glyph.Visible = true;
            }
            else
            {
                _box.Fill = _theme.Fill2;
                _box.BorderColor = _theme.Separator;
                _glyph.Visible = false;
            }
        }
        else if (Value)
        {
            _box.Fill = StateStyle.Fill(_theme.Primary, hovered, pressed);
            _box.BorderColor = Color.Transparent;
            _glyph.Color = _theme.OnPrimary;
            _glyph.Visible = true;
        }
        else
        {
            _box.Fill = pressed ? _theme.Fill1 : hovered ? _theme.Fill2 : _theme.Surface;
            _box.BorderColor = hovered ? _theme.Primary : _theme.Separator;
            _glyph.Visible = false;
        }
    }
}