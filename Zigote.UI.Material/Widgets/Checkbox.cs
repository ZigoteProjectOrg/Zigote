using Zigote.UI.Semantics;

namespace Zigote.UI.Material;

/// <summary>
///     A flat, macOS-style checkbox: a rounded square that fills with the accent and shows a white
///     check
///     mark when ticked. Composed from <see cref="Pressable" /> over a <see cref="DecoratedBox" /> box
///     whose child is a <see cref="CheckGlyph" /> (the tick). Sizing, colour and shape come from
///     tokens.
/// </summary>
public class Checkbox : StatefulWidget
{
    private bool _checked;
    private bool _enabled = true;
    private float _size = ControlMetrics.CheckboxSize;

    /// <summary>Named-argument constructor: <c>new Checkbox(value: true, onChanged: (v) => …)</c>.</summary>
    public Checkbox(bool value, Action<bool>? onChanged = null)
    {
        _checked = value;
        OnChanged = onChanged;
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            MarkNeedsBuild();
        }
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
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            MarkNeedsBuild();
        }
    }

    protected override WidgetState CreateState()
    {
        return new CheckboxState();
    }

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is Checkbox c)
        {
            Checked = c.Checked;
            OnChanged = c.OnChanged;
            Size = c.Size;
            Enabled = c.Enabled;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(Checked, Enabled, base.DebugStateHash());
    }
}

internal sealed class CheckboxState : WidgetState<Checkbox>
{
    private readonly DecoratedBox _box = new() { Radius = Radii.Xs };
    private readonly CheckGlyph _glyph = new();
    private Pressable _root = null!;
    private ThemeData _theme = ThemeData.Dark;

    public override void InitState()
    {
        _box.Child = _glyph;
        _root = new Pressable {
            Child = _box,
            FocusRadius = Radii.Xs,
            OnStateChanged = ApplyColors,
            OnPressed = Toggle,
        };
    }

    public override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);
        var w = Widget;

        _glyph.GlyphSize = w.Size;
        _root.Enabled = w.Enabled;
        _root.Role = SemanticsRole.Checkbox;
        _root.Checked = w.Checked;

        ApplyColors();
        return _root;
    }

    private void Toggle()
    {
        Widget.Checked = !Widget.Checked;
        Widget.OnChanged?.Invoke(Widget.Checked);
    }

    private void ApplyColors()
    {
        var w = Widget;
        var hovered = _root.Hovered;
        var pressed = _root.Pressed;

        if (!w.Enabled)
        {
            if (w.Checked)
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
        else if (w.Checked)
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