using Zigote.Core.Events;
using Zigote.UI.Semantics;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwSlider — the GNOME scale: a 4px rounded trough (<see cref="ThemeData.Fill1" />) filled
///     with the accent left of a 20px white knob (hairline border + soft shadow). Drag, click to
///     seek, or step with the arrow keys. Disabled drops the whole control to
///     <see cref="AdwStyle.DisabledOpacity" />. Interaction code adapted from the Material slider.
/// </summary>
public sealed class AdwSlider : Widget
{
    private bool _dragging;
    private bool _enabled = true;
    private bool _hovered;
    private float _max;
    private float _min;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    /// <summary>Where the track starts along the slider's axis, and how long it is. Both are the X
    ///     axis horizontally and the Y axis vertically — everything else is shared.</summary>
    private float _trackStart;

    private float _trackLength;
    private float _value;

    public AdwSlider(float value = 0f, float min = 0f, float max = 1f,
        Action<float>? onChanged = null)
    {
        _value = value;
        _min = min;
        _max = max;
        OnChanged = onChanged;
    }

    public float Value
    {
        get => _value;
        set => SetPaint(ref _value, value);
    }

    public float Min
    {
        get => _min;
        set => SetPaint(ref _min, value);
    }

    public float Max
    {
        get => _max;
        set => SetPaint(ref _max, value);
    }

    public Action<float>? OnChanged { get; set; }

    /// <summary>Disabled paints at <see cref="AdwStyle.DisabledOpacity" />, so flipping it repaints.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetPaint(ref _enabled, value);
    }

    /// <summary>Optional accessible name (the parameter this slider controls).</summary>
    public string? SemanticsLabel { get; set; }

    /// <summary>
    ///     Runs bottom-to-top instead of left-to-right — the mixing-desk fader an equalizer or a
    ///     channel strip is drawn with. Everything else (styling, keys, semantics) is the same
    ///     control; only the axis it measures, paints and drags along changes.
    /// </summary>
    public bool Vertical
    {
        get => _vertical;
        set => SetLayout(ref _vertical, value);
    }

    private bool _vertical;

    public override bool Focusable => Enabled;

    /// <summary>
    ///     The slider owns all four arrows for value stepping — claiming directional keys takes
    ///     Up/Down away from focus traversal too, so <see cref="OnKey" /> must act on them or they
    ///     would be swallowed doing nothing.
    /// </summary>
    public override bool HandlesDirectionalKeys => true;

    private static float KnobR => AdwMetrics.SliderKnob / 2f;

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Slider;
        config.Label = SemanticsLabel;
        var pct = Max > Min ? (Value - Min) / (Max - Min) * 100f : 0f;
        config.Value = $"{pct:F0}%";
        config.Actions =
            SemanticsAction.Increase | SemanticsAction.Decrease | SemanticsAction.Focus;
        config.AddFlag(SemanticsFlags.Focusable, Enabled)
            .AddFlag(SemanticsFlags.Focused, Focused)
            .AddFlag(SemanticsFlags.Disabled, !Enabled);
    }

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is AdwSlider s)
        {
            Value = s.Value;
            Min = s.Min;
            Max = s.Max;
            OnChanged = s.OnChanged;
            Enabled = s.Enabled;
            Vertical = s.Vertical;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            Value,
            _dragging,
            _hovered,
            Enabled,
            Focused
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        if (Vertical)
        {
            var h = float.IsFinite(c.MaxHeight) ? c.MaxHeight : 200f;
            _size = c.Constrain(new Size(AdwMetrics.ButtonHeight, h));
            return _size;
        }

        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 200f;
        _size = c.Constrain(new Size(w, AdwMetrics.ButtonHeight));
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
        var extent = Vertical ? _size.Height : _size.Width;
        _trackStart = (Vertical ? Bounds.Y : Bounds.X) + KnobR;
        _trackLength = MathF.Max(0f, extent - KnobR * 2f);
    }

    public override void Paint(PaintList paint)
    {
        if (!Enabled) paint.PushAlpha(AdwStyle.DisabledOpacity);

        var p = AdwPalette.For(_theme);
        var t = Max > Min ? Math.Clamp((Value - Min) / (Max - Min), 0f, 1f) : 0f;
        var thickness = AdwMetrics.SliderTrack;

        // The centre line across the axis, and the knob's position along it — vertically the value
        // grows upward, which is the one thing a fader does not share with a horizontal scale.
        var cross = Vertical
            ? Bounds.X + Bounds.Width / 2f
            : Bounds.Y + Bounds.Height / 2f;
        var knobPos = Vertical
            ? _trackStart + (1f - t) * _trackLength
            : _trackStart + t * _trackLength;

        Rect Along(float start, float length)
        {
            return Vertical
                ? new Rect(
                    cross - thickness / 2f,
                    start,
                    thickness,
                    length
                )
                : new Rect(
                    start,
                    cross - thickness / 2f,
                    length,
                    thickness
                );
        }

        paint.AddRect(Along(_trackStart, _trackLength), _theme.Fill1, Radii.Capsule);

        // Horizontally the fill runs from the start to the knob; vertically from the knob down to
        // the end, so a fader fills from the bottom.
        var filledStart = Vertical ? knobPos : _trackStart;
        var filled = Vertical ? _trackStart + _trackLength - knobPos : knobPos - _trackStart;
        if (filled > 0f)
            paint.AddRect(Along(filledStart, filled), _theme.Accent, Radii.Capsule);

        var knob = new Rect(
            (Vertical ? cross : knobPos) - KnobR,
            (Vertical ? knobPos : cross) - KnobR,
            KnobR * 2f,
            KnobR * 2f
        );
        paint.AddElevation(knob, KnobR, Elevation.Z1);
        paint.AddRect(knob, Color.White, KnobR);
        paint.AddBorder(knob, p.Border, KnobR);

        if (Focused && Enabled)
            paint.AddFocusRing(
                Along(Vertical ? Bounds.Y : Bounds.X, Vertical ? _size.Height : _size.Width),
                Radii.Capsule,
                _theme
            );

        if (!Enabled) paint.PopAlpha();
    }

    public override MouseCursor? GetCursor(Offset point)
    {
        return Enabled ? MouseCursor.Pointer : null;
    }

    public override void OnPointerEnter()
    {
        if (_hovered) return;
        _hovered = true;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (!_hovered && !_dragging) return;
        _hovered = false;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        if (!Enabled) return;
        _dragging = true;
        UpdateValue(point); // click-to-seek
    }

    public override void OnPointerMove(Offset point)
    {
        if (_dragging) UpdateValue(point);
    }

    /// <summary>
    ///     A finger on the scale is adjusting it, so the page it sits in cannot take the drag away
    ///     in either direction — a fader is dragged along the very axis its scroll parent uses, and
    ///     a horizontal scale must survive a finger that settles downward before setting off.
    /// </summary>
    public override bool CanTouchDrag(bool vertical)
    {
        return _dragging;
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_dragging) return;
        _dragging = false;
        MarkNeedsPaint();
    }

    public override void OnPointerCancel()
    {
        if (!_dragging) return;
        _dragging = false;
        MarkNeedsPaint();
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down || !Enabled) return;
        var step = (Max - Min) / 20f; // 5% step
        switch ((KeyCode)scancode)
        {
            case KeyCode.Right or KeyCode.Up:
                UpdateValueBy(step);
                break;
            case KeyCode.Left or KeyCode.Down:
                UpdateValueBy(-step);
                break;
        }
    }

    private void UpdateValueBy(float delta)
    {
        var newVal = Math.Clamp(Value + delta, Min, Max);
        if (MathF.Abs(newVal - Value) <= 0.0001f) return;
        Value = newVal;
        OnChanged?.Invoke(Value);
    }

    private void UpdateValue(Offset point)
    {
        var along = Vertical ? point.Y : point.X;
        var t = _trackLength > 0f
            ? Math.Clamp((along - _trackStart) / _trackLength, 0f, 1f)
            : 0f;
        if (Vertical) t = 1f - t;
        var newVal = Min + t * (Max - Min);
        if (MathF.Abs(newVal - Value) <= 0.0001f) return;
        Value = newVal;
        OnChanged?.Invoke(Value);
    }
}