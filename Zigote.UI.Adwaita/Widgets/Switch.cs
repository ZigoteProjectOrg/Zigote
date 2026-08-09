using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Semantics;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwSwitch — the GNOME toggle: a 48×26 capsule track (neutral fill off, accent on) with a
///     plain white knob that slides across. Disabled drops the whole control to
///     <see cref="AdwStyle.DisabledOpacity" />.
/// </summary>
public sealed class AdwSwitch : Widget
{
    private readonly AnimationController _anim;
    private bool _enabled = true;
    private bool _hovered;
    private bool _pressed;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private bool _value;

    public AdwSwitch(bool value = false, Action<bool>? onChanged = null)
    {
        _value = value;
        OnChanged = onChanged;
        _anim = new AnimationController(Motion.Fast, this) { Curve = Curves.EaseOut };
        _anim.OnTick += MarkNeedsPaint;
        // Jump to the initial position without animating.
        if (value) _anim.Complete();
        else _anim.Dismiss();
    }

    public bool Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            // External/controlled set: snap, don't animate (a user Toggle animates instead).
            if (value) _anim.Complete();
            else _anim.Dismiss();
            MarkNeedsPaint();
        }
    }

    public Action<bool>? OnChanged { get; set; }

    /// <summary>Disabled paints at <see cref="AdwStyle.DisabledOpacity" />, so flipping it repaints.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetPaint(ref _enabled, value);
    }

    /// <summary>Optional accessible name (the setting this switch toggles).</summary>
    public string? SemanticsLabel { get; set; }

    public override bool Focusable => Enabled;


    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        _anim.AttachTicker(this);
    }


    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Switch;
        config.Label = SemanticsLabel;
        config.Actions = SemanticsAction.Tap | SemanticsAction.Focus;
        config.AddFlag(SemanticsFlags.Checkable)
            .AddFlag(SemanticsFlags.Checked, Value)
            .AddFlag(SemanticsFlags.Focusable, Enabled)
            .AddFlag(SemanticsFlags.Focused, Focused)
            .AddFlag(SemanticsFlags.Disabled, !Enabled);
    }

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is AdwSwitch s)
        {
            Value = s.Value;
            OnChanged = s.OnChanged;
            Enabled = s.Enabled;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            Value,
            _anim.Progress.GetHashCode(),
            _hovered,
            _pressed,
            Focused,
            Enabled
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(new Size(AdwMetrics.SwitchWidth, AdwMetrics.SwitchHeight));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        if (!Enabled) paint.PushAlpha(AdwStyle.DisabledOpacity);

        var p = AdwPalette.For(_theme);
        var r = Bounds.Height / 2f;

        var track = _value
            ? AdwStyle.Solid(
                _theme.Accent,
                _hovered,
                _pressed,
                Enabled
            )
            : _pressed && Enabled
                ? p.ButtonFillActive
                : _hovered && Enabled
                    ? p.ButtonFillHover
                    : p.ButtonFill;
        paint.AddRect(Bounds, track, Radii.Capsule);

        // White knob, slides across the travel distance.
        var knobD = AdwMetrics.SwitchHeight - 6f;
        var knobR = knobD / 2f;
        var travel = Bounds.Width - Bounds.Height;
        var cx = Bounds.X + r + travel * _anim.Value;
        var cy = Bounds.Y + r;
        var knob = new Rect(
            cx - knobR,
            cy - knobR,
            knobD,
            knobD
        );
        paint.AddElevation(knob, Radii.Capsule, Elevation.Z1);
        paint.AddRect(knob, Color.White, Radii.Capsule);

        if (Focused && Enabled)
            paint.AddFocusRing(Bounds, Radii.Capsule, _theme);

        if (!Enabled) paint.PopAlpha();
    }

    private void Toggle()
    {
        if (!Enabled) return;
        // Set the field directly (not via Value, which snaps) and animate.
        _value = !_value;
        if (_value) _anim.Forward();
        else _anim.Reverse();
        MarkNeedsPaint();
        OnChanged?.Invoke(_value);
    }

    public override void OnPointerEnter()
    {
        if (_hovered) return;
        _hovered = true;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (!_hovered && !_pressed) return;
        _hovered = false;
        _pressed = false;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled || _pressed) return;
        _pressed = true;
        MarkNeedsPaint();
    }

    public override void OnPointerUp(Offset point)
    {
        if (_pressed && Enabled && Bounds.Contains(point.X, point.Y))
            Toggle();
        if (_pressed)
        {
            _pressed = false;
            MarkNeedsPaint();
        }
    }

    public override void OnPointerCancel()
    {
        if (!_pressed) return;
        _pressed = false;
        MarkNeedsPaint();
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (scancode is 44 or 40) // Space or Enter
        {
            _pressed = down;
            MarkNeedsPaint();
            if (!down) Toggle();
        }
    }
}