using Zigote.UI.Semantics;

namespace Zigote.UI.Material;

/// <summary>
///     A flat, macOS-style radio button. Typically several Radio&lt;T&gt; widgets share the same
///     <see cref="GroupValue" /> and <see cref="OnChanged" /> callback. Composed from
///     <see cref="Pressable" /> over a circular <see cref="DecoratedBox" /> whose child is a
///     <see cref="RadioDotGlyph" /> (the centre dot, shown only when selected).
/// </summary>
public class Radio<T> : ComposedWidget where T : IEquatable<T>
{
    private readonly DecoratedBox _box = new();
    private readonly RadioDotGlyph _glyph = new();

    // Phone hit box: the dot keeps its 16pt look, centred in a finger-sized press area.
    private readonly SizedBox _touchBox = new(TouchMetrics.MinTarget, TouchMetrics.MinTarget);
    private readonly Pressable _root;
    private ThemeData _theme = ThemeData.Dark;

    private bool _enabled = true;
    private T _groupValue;
    private float _size = ControlMetrics.RadioSize;
    private T _value;

    public Radio(T value, T groupValue, Action<T>? onChanged = null)
    {
        _value = value;
        _groupValue = groupValue;
        OnChanged = onChanged;

        _box.Child = _glyph;
        _touchBox.Child = new Center(_box);
        _root = new Pressable {
            Child = _box,
            OnStateChanged = ApplyColors,
            OnPressed = Select,
        };
    }

    public T Value
    {
        get => _value;
        set
        {
            _value = value;
            MarkNeedsBuild();
        }
    }

    public T GroupValue
    {
        get => _groupValue;
        set => SetBuild(ref _groupValue, value);
    }

    public Action<T>? OnChanged { get; set; }

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

    public bool IsSelected => Value.Equals(GroupValue);

    public override void UpdateFrom(Widget newWidget)
    {
        // Keyed reconcile reuses this instance; copy the new config in (through the setters, which mark
        // for rebuild) so the reused radio reflects the current Value/GroupValue instead of stale state.
        if (newWidget is Radio<T> r)
        {
            Value = r.Value;
            GroupValue = r.GroupValue;
            OnChanged = r.OnChanged;
            Enabled = r.Enabled;
            Size = r.Size;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(IsSelected, Enabled, base.DebugStateHash());
    }

    protected override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);
        var d = Size;

        _glyph.GlyphSize = d;
        _box.Radius = d / 2f;
        _root.Child = TouchMetrics.IsCompact ? _touchBox : _box;
        _root.Enabled = Enabled;
        _root.FocusRadius = d / 2f;
        _root.Role = SemanticsRole.RadioButton;
        _root.Checked = IsSelected;

        ApplyColors();
        return _root;
    }

    private void Select()
    {
        if (IsSelected) return;
        OnChanged?.Invoke(Value);
    }

    private void ApplyColors()
    {
        var hovered = _root.Hovered;
        var pressed = _root.Pressed;

        if (!Enabled)
        {
            _box.Fill = _theme.Fill2;
            _box.BorderColor = StateStyle.Disabled(_theme.Separator);
            _glyph.Visible = IsSelected;
            if (IsSelected) _glyph.Color = StateStyle.Disabled(_theme.Primary);
        }
        else if (IsSelected)
        {
            var accent = StateStyle.Fill(_theme.Primary, hovered, pressed);
            _box.Fill = accent;
            _box.BorderColor = accent;
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
