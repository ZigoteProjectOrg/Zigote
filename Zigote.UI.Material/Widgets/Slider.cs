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
    private bool _hovered;
    private float _measureH;
    private float _measureW;
    private ThemeData _theme = ThemeData.Dark;
    private float _trackLeft;
    private float _trackWidth;
    private float _value;
    private float _min;
    private float _max = 1f;
    private float _height = ControlMetrics.RegularHeight;

    /// <summary>
    ///     Named-argument constructor:
    ///     <c>new Slider(value: 0.5, min: 0, max: 100, onChanged: (v) => …)</c>.
    ///     <c>onChanged</c> receives a <c>double</c>.
    /// </summary>
    public Slider(double value, double min = 0, double max = 1, Action<double>? onChanged = null)
    {
        _value = (float)value;
        _min = (float)min;
        _max = (float)max;
        OnChanged = onChanged is null ? null : new Action<float>(f => onChanged(f));
    }

    public float Value
    {
        get => _value;
        set
        {
            if (value == _value) return;
            _value = value;
            MarkNeedsPaint();
        }
    }

    public float Min
    {
        get => _min;
        set
        {
            if (value == _min) return;
            _min = value;
            MarkNeedsPaint();
        }
    }

    public float Max
    {
        get => _max;
        set
        {
            if (value == _max) return;
            _max = value;
            MarkNeedsPaint();
        }
    }

    public Action<float>? OnChanged { get; set; }

    public float Height
    {
        get => _height;
        set
        {
            if (value == _height) return;
            _height = value;
            MarkNeedsLayout();
        }
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
        _measureH = Math.Clamp(Height, c.MinHeight, c.MaxHeight);
        var rawW = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 200f;
        var sz = c.Constrain(new Size(rawW, _measureH));
        _measureW = sz.Width;
        _measureH = sz.Height;
        return sz;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _measureW,
            _measureH
        );
        _trackLeft = Bounds.X + ThumbR;
        _trackWidth = MathF.Max(0f, _measureW - ThumbR * 2f);
    }

    public override void Paint(PaintList paint)
    {
        var cy = Bounds.Y + _measureH / 2f;
        var t = Max > Min ? Math.Clamp((Value - Min) / (Max - Min), 0f, 1f) : 0f;
        var thumbX = _trackLeft + t * _trackWidth;

        var trackH = ControlMetrics.SliderTrack;
        var trackY = cy - trackH / 2f;
        var trackRadius = Radii.Capsule;

        // Background track — a clearly visible groove (Fill tokens are too faint for a 4px line).
        var trackBase = _theme.OnSurface.WithAlpha(0.18f);
        var trackFill = Enabled ? trackBase : StateStyle.Disabled(trackBase);
        paint.AddRect(
            new Rect(
                _trackLeft,
                trackY,
                _trackWidth,
                trackH
            ),
            trackFill,
            trackRadius
        );

        // Accent-filled portion left of the thumb.
        var filledW = thumbX - _trackLeft;
        if (filledW > 0f)
        {
            var accent = Enabled ? _theme.Primary : StateStyle.Disabled(_theme.Primary);
            paint.AddRect(
                new Rect(
                    _trackLeft,
                    trackY,
                    filledW,
                    trackH
                ),
                accent,
                trackRadius
            );
        }

        // Circular white thumb with a soft hairline shadow.
        var thumb = new Rect(
            thumbX - ThumbR,
            cy - ThumbR,
            ThumbR * 2f,
            ThumbR * 2f
        );
        if (Enabled) paint.AddElevation(thumb, ThumbR, Elevation.Z1);
        var thumbColor = Enabled ? _theme.OnPrimary : StateStyle.Disabled(_theme.OnPrimary);
        paint.AddRect(thumb, thumbColor, ThumbR);
        paint.AddBorder(thumb, _theme.Separator, ThumbR);

        // Focus ring around the whole track.
        if (!Focused || !Enabled) return;
        var ringRect = new Rect(
            Bounds.X,
            trackY,
            _measureW,
            trackH
        );
        paint.AddFocusRing(ringRect, trackRadius, _theme);
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

    public override void OnPointerUp(Offset point)
    {
        if (!_dragging) return;
        _dragging = false;
        MarkNeedsPaint();
    }

    public override MouseCursor? GetCursor(Offset point)
    {
        return Enabled ? MouseCursor.Pointer : null;
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down || !Enabled) return;
        const uint scLeft = 80;
        const uint scRight = 79;

        var step = (Max - Min) / 20f; // 5% step
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
        var newVal = Math.Clamp(Value + delta, Min, Max);
        if (!(MathF.Abs(newVal - Value) > 0.0001f)) return;
        Value = newVal;
        MarkNeedsPaint();
        OnChanged?.Invoke(Value);
    }

    private void UpdateValue(float x)
    {
        var t = _trackWidth > 0f ? Math.Clamp((x - _trackLeft) / _trackWidth, 0f, 1f) : 0f;
        var newVal = Min + t * (Max - Min);
        if (!(MathF.Abs(newVal - Value) > 0.0001f)) return;
        Value = newVal;
        MarkNeedsPaint();
        OnChanged?.Invoke(Value);
    }
}