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

    private float _trackLength;

    /// <summary>
    ///     Where the track starts along the slider's axis, and how long it is. Both are the X
    ///     axis horizontally and the Y axis vertically — everything else is shared.
    /// </summary>
    private float _trackStart;

    private float _value;

    private bool _vertical;

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
        set => SetPaint(field: ref _value, value: value);
    }

    public float Min
    {
        get => _min;
        set => SetPaint(field: ref _min, value: value);
    }

    public float Max
    {
        get => _max;
        set => SetPaint(field: ref _max, value: value);
    }

    public Action<float>? OnChanged { get; set; }

    /// <summary>Disabled paints at <see cref="AdwStyle.DisabledOpacity" />, so flipping it repaints.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetPaint(field: ref _enabled, value: value);
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
        set => SetLayout(field: ref _vertical, value: value);
    }

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
        float pct = Max > Min ? (Value - Min) / (Max - Min) * 100f : 0f;
        config.Value = $"{pct:F0}%";
        config.Actions =
            SemanticsAction.Increase | SemanticsAction.Decrease | SemanticsAction.Focus;
        config.AddFlag(flag: SemanticsFlags.Focusable, on: Enabled)
            .AddFlag(flag: SemanticsFlags.Focused, on: Focused)
            .AddFlag(flag: SemanticsFlags.Disabled, on: !Enabled);
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
            value1: Value,
            value2: _dragging,
            value3: _hovered,
            value4: Enabled,
            value5: Focused
        );
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        if (Vertical)
        {
            float h = float.IsFinite(c.MaxHeight) ? c.MaxHeight : 200f;
            _size = c.Constrain(new Size(width: AdwMetrics.ButtonHeight, height: h));
            return _size;
        }

        float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 200f;
        _size = c.Constrain(new Size(width: w, height: AdwMetrics.ButtonHeight));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        float extent = Vertical ? _size.Height : _size.Width;
        _trackStart = (Vertical ? Bounds.Y : Bounds.X) + KnobR;
        _trackLength = MathF.Max(x: 0f, y: extent - (KnobR * 2f));
    }

    public override void Paint(PaintList paint)
    {
        if (!Enabled) paint.PushAlpha(AdwStyle.DisabledOpacity);

        var p = AdwPalette.For(_theme);
        float t = Max > Min ? Math.Clamp(value: (Value - Min) / (Max - Min), min: 0f, max: 1f) : 0f;
        float thickness = AdwMetrics.SliderTrack;

        // The centre line across the axis, and the knob's position along it — vertically the value
        // grows upward, which is the one thing a fader does not share with a horizontal scale.
        float cross = Vertical
            ? Bounds.X + (Bounds.Width / 2f)
            : Bounds.Y + (Bounds.Height / 2f);
        float knobPos = Vertical
            ? _trackStart + ((1f - t) * _trackLength)
            : _trackStart + (t * _trackLength);

        Rect Along(float start, float length)
        {
            return Vertical
                ? new Rect(
                    x: cross - (thickness / 2f),
                    y: start,
                    width: thickness,
                    height: length
                )
                : new Rect(
                    x: start,
                    y: cross - (thickness / 2f),
                    width: length,
                    height: thickness
                );
        }

        // %scale_trough — currentColor 15%, brightening to 20% while the whole scale is hot.
        paint.AddRect(
            bounds: Along(start: _trackStart, length: _trackLength),
            color: AdwStyle.TroughFill(theme: _theme, hovered: _hovered || _dragging),
            radius: Radii.Capsule
        );

        // Horizontally the fill runs from the start to the knob; vertically from the knob down to
        // the end, so a fader fills from the bottom.
        float filledStart = Vertical ? knobPos : _trackStart;
        float filled = Vertical ? _trackStart + _trackLength - knobPos : knobPos - _trackStart;
        if (filled > 0f)
        {
            paint.AddRect(
                bounds: Along(start: filledStart, length: filled),
                color: _theme.Accent,
                radius: Radii.Capsule
            );
        }

        var knob = new Rect(
            x: (Vertical ? cross : knobPos) - KnobR,
            y: (Vertical ? knobPos : cross) - KnobR,
            width: KnobR * 2f,
            height: KnobR * 2f
        );
        paint.AddElevation(bounds: knob, radius: KnobR, style: Elevation.Z1);
        // `> slider { background-color: $slider_color }` with a 1px rgb(0 0 6 / 10%) ring — the
        // knob is white-over-view-bg, not pure white, so it doesn't glare in dark mode.
        paint.AddRect(
            bounds: knob,
            color: AdwStyle.SliderKnob(theme: _theme, hot: _hovered || _dragging),
            radius: KnobR
        );
        paint.AddBorder(bounds: knob, color: AdwStyle.Ink.WithAlpha(0.1f), radius: KnobR);

        if (Focused && Enabled)
        {
            paint.AddFocusRing(
                bounds: Along(
                    start: Vertical ? Bounds.Y : Bounds.X,
                    length: Vertical ? _size.Height : _size.Width
                ),
                radius: Radii.Capsule,
                theme: _theme
            );
        }

        if (!Enabled) paint.PopAlpha();
    }

    public override MouseCursor? GetCursor(Offset point) => Enabled ? MouseCursor.Pointer : null;

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
    public override bool CanTouchDrag(bool vertical) => _dragging;

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
        float step = (Max - Min) / 20f; // 5% step
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
        float newVal = Math.Clamp(value: Value + delta, min: Min, max: Max);
        if (MathF.Abs(newVal - Value) <= 0.0001f) return;
        Value = newVal;
        OnChanged?.Invoke(Value);
    }

    private void UpdateValue(Offset point)
    {
        float along = Vertical ? point.Y : point.X;
        float t = _trackLength > 0f
            ? Math.Clamp(value: (along - _trackStart) / _trackLength, min: 0f, max: 1f)
            : 0f;
        if (Vertical) t = 1f - t;
        float newVal = Min + (t * (Max - Min));
        if (MathF.Abs(newVal - Value) <= 0.0001f) return;
        Value = newVal;
        OnChanged?.Invoke(Value);
    }
}
