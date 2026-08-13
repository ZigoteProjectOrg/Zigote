using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.UI.Semantics;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     A flat, macOS-style toggle switch. Capsule track tinted by the accent when on, a neutral fill
///     when off, with a white knob that animates between ends via a self-owned AnimationController.
/// </summary>
public class Switch : Widget
{
    private readonly AnimationController _anim;
    private bool _hovered;
    private bool _pressed;
    private float _hitPadX, _hitPadY;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;
    private bool _value;
    private float _trackW = ControlMetrics.SwitchWidth;
    private float _trackH = ControlMetrics.SwitchHeight;

    public Switch(bool value, Action<bool>? onChanged = null)
    {
        _value = value;
        OnChanged = onChanged;
        _anim = new AnimationController(0.15f, this) { Curve = Curves.EaseOut };
        _anim.OnTick += MarkNeedsPaint;
        // Jump to initial position without animating.
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
            // Snap the knob to match — an external/controlled set is a config update, not a user
            // toggle. (Toggle() sets the field directly and animates, so a controlled re-render that
            // echoes the new value hits the early-return above and doesn't cut its animation short.)
            if (value) _anim.Complete();
            else _anim.Dismiss();
            MarkNeedsPaint();
        }
    }

    public Action<bool>? OnChanged { get; set; }
    public bool Enabled { get; set; } = true;

    public float TrackW
    {
        get => _trackW;
        set => SetLayout(ref _trackW, value);
    }

    public float TrackH
    {
        get => _trackH;
        set => SetLayout(ref _trackH, value);
    }

    public override bool Focusable => true;

    /// <summary>Optional accessible name (e.g. the setting this switch toggles).</summary>
    public string? SemanticsLabel { get; set; }


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

    // Mount-scoped: the ticker CreateTicker hands out is disposed on unmount, so a
    // re-attach rebinds instead of leaking one per attach cascade.
    protected override void OnMount()
    {
        // Detach disposed the ticker; rebind so the knob still animates after a detach→re-attach cycle
        // (overlay push/pop, Root swap, keyed list remove/re-add).
        _anim.AttachTicker(this);
    }


    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is Switch s)
        {
            Value = s.Value; // the setter snaps the knob if the value changed (config update)
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
            Focused
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(new Size(TrackW, TrackH));
        // The 38×22 capsule is half a finger target. Grow the hit rect around the unchanged
        // track instead of the track itself, so the switch looks identical on every platform.
        _hitPadX = MathF.Max(0f, (TouchMetrics.AtLeast(_size.Width) - _size.Width) / 2f);
        _hitPadY = MathF.Max(0f, (TouchMetrics.AtLeast(_size.Height) - _size.Height) / 2f);
        return _size;
    }

    private Rect HitRect => new(
        Bounds.X - _hitPadX,
        Bounds.Y - _hitPadY,
        Bounds.Width + _hitPadX * 2f,
        Bounds.Height + _hitPadY * 2f
    );

    public override Widget? HitTest(Offset point)
    {
        return HitRect.Contains(point.X, point.Y) ? this : null;
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
        var r = TrackH / 2f;

        // Capsule track: accent when on, neutral fill when off.
        var track = Value ? _theme.Primary : _theme.Fill1;
        if (Enabled) track = StateStyle.Fill(track, _hovered, _pressed);
        else track = StateStyle.Disabled(track);
        paint.AddRect(Bounds, track, Radii.Capsule);

        // White knob, inset from the track edges, slides across the travel distance.
        var inset = Spacing.Xxs;
        var thumbR = r - inset;
        var travel = TrackW - TrackH;
        var thumbCx = Bounds.X + r + travel * _anim.Value;
        var thumbCy = Bounds.Y + r;
        var knob = Enabled ? new Color(1f, 1f, 1f) : StateStyle.Disabled(new Color(1f, 1f, 1f));
        var thumbRect = new Rect(
            thumbCx - thumbR,
            thumbCy - thumbR,
            thumbR * 2f,
            thumbR * 2f
        );
        paint.AddElevation(thumbRect, Radii.Capsule, Elevation.Z1);
        paint.AddRect(thumbRect, knob, Radii.Capsule);

        if (Focused && Enabled)
            paint.AddFocusRing(Bounds, Radii.Capsule, _theme);
    }

    private void Toggle()
    {
        if (!Enabled) return;
        // Set the field directly (not via Value, which would snap) and animate to the new position.
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
        if (_pressed && Enabled && HitRect.Contains(point.X, point.Y))
            Toggle();
        if (_pressed)
        {
            _pressed = false;
            MarkNeedsPaint();
        }
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
