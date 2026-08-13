using Zigote.Core.Events;
using Zigote.UI.Semantics;

namespace Zigote.UI.Material;

/// <summary>
///     A flat, macOS-style horizontal slider: a thin capsule track (<see cref="ThemeData.Fill1" />)
///     with an
///     accent-filled portion left of a circular white thumb. Value is in [<see cref="Min" />,
///     <see cref="Max" />].
/// </summary>
public class Slider : Widget
{
    private bool _dragging;
    private float _height = ControlMetrics.RegularHeight;
    private bool _hovered;
    private float _max = 1f;
    private float _measureH;
    private float _measureW;
    private float _min;
    private ThemeData _theme = ThemeData.Dark;
    private float _trackLeft;
    private float _trackWidth;
    private float _value;

    /// <summary>
    ///     Named-argument constructor:
    ///     <c>new Slider(value: 0.5f, min: 0, max: 100, onChanged: (v) => …)</c>.
    /// </summary>
    public Slider(float value, float min = 0f, float max = 1f, Action<float>? onChanged = null)
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

    public float Height
    {
        get => _height;
        set => SetLayout(field: ref _height, value: value);
    }

    public bool Enabled { get; set; } = true;
    public override bool Focusable => true;

    /// <summary>
    ///     The slider owns Left/Right for value stepping, so the app must not repurpose them for
    ///     focus.
    /// </summary>
    public override bool HandlesDirectionalKeys => true;

    /// <summary>Optional accessible name (e.g. the parameter this slider controls).</summary>
    public string? SemanticsLabel { get; set; }

    private float ThumbR => ControlMetrics.SliderThumb / 2f;

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
        if (newWidget is Slider s)
        {
            Value = s.Value;
            Min = s.Min;
            Max = s.Max;
            OnChanged = s.OnChanged;
            Enabled = s.Enabled;
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
        // The whole widget is the scrub target, so on a phone the band — not the 4pt track it
        // draws inside it — has to be finger-sized. Paint centres off the measured height.
        _measureH = Math.Clamp(
            value: TouchMetrics.AtLeast(Height),
            min: c.MinHeight,
            max: c.MaxHeight
        );
        float rawW = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 200f;
        var sz = c.Constrain(new Size(width: rawW, height: _measureH));
        _measureW = sz.Width;
        _measureH = sz.Height;
        return sz;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _measureW,
            height: _measureH
        );
        _trackLeft = Bounds.X + ThumbR;
        _trackWidth = MathF.Max(x: 0f, y: _measureW - (ThumbR * 2f));
    }

    public override void Paint(PaintList paint)
    {
        float cy = Bounds.Y + (_measureH / 2f);
        float t = Max > Min ? Math.Clamp(value: (Value - Min) / (Max - Min), min: 0f, max: 1f) : 0f;
        float thumbX = _trackLeft + (t * _trackWidth);

        float trackH = ControlMetrics.SliderTrack;
        float trackY = cy - (trackH / 2f);
        float trackRadius = Radii.Capsule;

        // Background track — a clearly visible groove (Fill tokens are too faint for a 4px line).
        var trackBase = _theme.OnSurface.WithAlpha(0.18f);
        var trackFill = Enabled ? trackBase : StateStyle.Disabled(trackBase);
        paint.AddRect(
            bounds: new Rect(
                x: _trackLeft,
                y: trackY,
                width: _trackWidth,
                height: trackH
            ),
            color: trackFill,
            radius: trackRadius
        );

        // Accent-filled portion left of the thumb.
        float filledW = thumbX - _trackLeft;
        if (filledW > 0f)
        {
            var accent = Enabled ? _theme.Primary : StateStyle.Disabled(_theme.Primary);
            paint.AddRect(
                bounds: new Rect(
                    x: _trackLeft,
                    y: trackY,
                    width: filledW,
                    height: trackH
                ),
                color: accent,
                radius: trackRadius
            );
        }

        // Circular white thumb with a soft hairline shadow.
        var thumb = new Rect(
            x: thumbX - ThumbR,
            y: cy - ThumbR,
            width: ThumbR * 2f,
            height: ThumbR * 2f
        );
        if (Enabled) paint.AddElevation(bounds: thumb, radius: ThumbR, style: Elevation.Z1);
        var thumbColor = Enabled ? _theme.OnPrimary : StateStyle.Disabled(_theme.OnPrimary);
        paint.AddRect(bounds: thumb, color: thumbColor, radius: ThumbR);
        paint.AddBorder(bounds: thumb, color: _theme.Separator, radius: ThumbR);

        // Focus ring around the whole track.
        if (!Focused || !Enabled) return;
        var ringRect = new Rect(
            x: Bounds.X,
            y: trackY,
            width: _measureW,
            height: trackH
        );
        paint.AddFocusRing(bounds: ringRect, radius: trackRadius, theme: _theme);
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
        MarkNeedsPaint();
        UpdateValue(point.X);
    }

    public override void OnPointerMove(Offset point)
    {
        if (_dragging) UpdateValue(point.X);
    }

    /// <summary>
    ///     A finger on the track is adjusting the value, so a scrolling page cannot take the drag
    ///     away in either direction — the vertical half matters most, because that is the finger
    ///     that settles downward a little before setting off sideways.
    /// </summary>
    public override bool CanTouchDrag(bool vertical) => _dragging;

    public override void OnPointerUp(Offset point)
    {
        if (!_dragging) return;
        _dragging = false;
        MarkNeedsPaint();
    }

    /// <summary>
    ///     A pinch or an app-level takeover ended the press: stop scrubbing (and stop claiming the
    ///     gesture) rather than staying latched to a finger that is no longer being reported.
    /// </summary>
    public override void OnPointerCancel()
    {
        if (!_dragging) return;
        _dragging = false;
        MarkNeedsPaint();
    }

    public override MouseCursor? GetCursor(Offset point) => Enabled ? MouseCursor.Pointer : null;

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down || !Enabled) return;
        const uint scLeft = 80;
        const uint scRight = 79;

        float step = (Max - Min) / 20f; // 5% step
        switch (scancode)
        {
            case scLeft:
                UpdateValueBy(-step);
                break;
            case scRight:
                UpdateValueBy(step);
                break;
        }
    }

    private void UpdateValueBy(float delta)
    {
        float newVal = Math.Clamp(value: Value + delta, min: Min, max: Max);
        if (!(MathF.Abs(newVal - Value) > 0.0001f)) return;
        Value = newVal;
        MarkNeedsPaint();
        OnChanged?.Invoke(Value);
    }

    private void UpdateValue(float x)
    {
        float t = _trackWidth > 0f
            ? Math.Clamp(value: (x - _trackLeft) / _trackWidth, min: 0f, max: 1f)
            : 0f;
        float newVal = Min + (t * (Max - Min));
        if (!(MathF.Abs(newVal - Value) > 0.0001f)) return;
        Value = newVal;
        MarkNeedsPaint();
        OnChanged?.Invoke(Value);
    }
}
