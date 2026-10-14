using System.Globalization;
using Zigote.Core.Events;
using Zigote.UI.Host;

namespace Zigote.UI.Adwaita;

/// <summary>
///     A numeric field for tools: GTK's spin button — one entry-filled box holding the value and
///     flat −/+ buttons divided from it by their own hairlines — with a drag-scrub grip on the
///     leading edge. Dragging the grip left/right nudges <see cref="Value" /> live; hold Shift for
///     fine (0.1×) precision. Unlike <see cref="AdwSpinButton" /> it is a float with a decimal
///     count and may be unbounded. Value stays within [<see cref="Min" />, <see cref="Max" />].
/// </summary>
public sealed class AdwNumberInput : Widget
{
    private readonly StepButton _btnDown;
    private readonly StepButton _btnUp;

    private readonly AdwTextField _field;
    private readonly ScrubGrip _grip;
    private readonly Row _row;
    private int _decimals = 2;
    private bool _editingText;
    private float _max = float.PositiveInfinity;
    private float _min = float.NegativeInfinity;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    private float _value;

    public AdwNumberInput(float value, float step = 1f,
        float min = float.NegativeInfinity, float max = float.PositiveInfinity)
    {
        Value = value;
        Step = step;
        _min = min;
        _max = max;

        _field = new AdwTextField {
            Text = Format(value),
            Height = AdwMetrics.EntryHeight,
            MinWidth = 40f,
            // This widget owns the box and the focus ring; the field renders text only.
            ShowBackground = false,
            OnChanged = s =>
            {
                _editingText = true;
                if (float.TryParse(
                        s: s,
                        style: NumberStyles.Float,
                        provider: CultureInfo.InvariantCulture,
                        result: out float v
                    ))
                    Commit(v);
            },
        };

        _btnUp = new StepButton(glyph: Icons.Add, onPressed: () => Commit(Value + Step));
        _btnDown = new StepButton(
            glyph: MaterialIcons.Remove,
            onPressed: () => Commit(Value - Step)
        );

        _grip = new ScrubGrip(this);

        _row = new Row {
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = {
                _grip,
                new Expanded(_field),
                _btnDown,
                _btnUp,
            },
        };
    }

    public float Value
    {
        get => _value;
        set
        {
            _value = value;
            // Reflect an external Value set on screen — but never clobber what the user is typing, and
            // guard _field which is still null while the ctor sets the initial Value.
            if (!_editingText && _field is not null)
            {
                _field.Text = Format(value);
                MarkNeedsPaint();
            }
        }
    }

    public float Min
    {
        get => _min;
        set
        {
            if (value == _min) return;
            _min = value;
            // If the new lower bound clamps the current value, pull it up and reflect it.
            float clamped = Math.Clamp(value: Value, min: _min, max: _max);
            if (clamped != Value) Value = clamped;
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
            // If the new upper bound clamps the current value, pull it down and reflect it.
            float clamped = Math.Clamp(value: Value, min: _min, max: _max);
            if (clamped != Value) Value = clamped;
            MarkNeedsPaint();
        }
    }

    public float Step { get; set; } = 1f;

    public int Decimals
    {
        get => _decimals;
        set
        {
            if (_decimals == value) return;
            _decimals = value;
            // Reformat the displayed text with the new precision.
            if (!_editingText) SyncText();
            MarkNeedsPaint();
        }
    }

    /// <summary>Multiplier on the drag-scrub speed (pixels → value). Default 1.</summary>
    public float ScrubSensitivity { get; set; } = 1f;

    public Action<float>? OnChanged { get; set; }

    /// <summary>
    ///     Fired when a drag-scrub begins (pointer-down on the grip). Use to open an undo
    ///     interaction.
    /// </summary>
    public Action? OnScrubStart { get; set; }

    /// <summary>Fired when a drag-scrub ends (pointer-up after a drag). Use to close the undo interaction.</summary>
    public Action? OnScrubEnd { get; set; }

    public override int DebugStateHash() => HashCode.Combine(
        value1: Value,
        value2: _grip.DebugStateHash()
    );

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is AdwNumberInput n)
        {
            Value = n.Value;
            Min = n.Min;
            Max = n.Max;
            Step = n.Step;
            Decimals = n.Decimals;
            ScrubSensitivity = n.ScrubSensitivity;
            OnChanged = n.OnChanged;
            OnScrubStart = n.OnScrubStart;
            OnScrubEnd = n.OnScrubEnd;
            // Reformat with the reconciled Value/Decimals (the Value setter above ran with the old
            // Decimals, and would skip while mid-edit).
            _editingText = false;
            SyncText();
        }
    }

    private void Commit(float v)
    {
        float clamped = Math.Clamp(value: v, min: Min, max: Max);
        if (MathF.Abs(clamped - Value) < 1e-9f && _editingText) return;
        Value = clamped;
        if (!_editingText) _field.Text = Format(clamped);
        _editingText = false;
        OnChanged?.Invoke(clamped);
    }

    /// <summary>Applies a raw value from the scrub grip: clamp, round to Decimals, fire OnChanged live.</summary>
    private void Scrub(float v)
    {
        float clamped = Math.Clamp(value: v, min: Min, max: Max);
        float rounded = MathF.Round(
            x: clamped,
            digits: Math.Clamp(value: Decimals, min: 0, max: 15)
        );
        if (MathF.Abs(rounded - Value) < 1e-9f) return;
        _editingText = false;
        Value = rounded;
        _field.Text = Format(rounded);
        OnChanged?.Invoke(rounded);
    }

    public void SyncText() => _field.Text = Format(Value);

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = _row.Measure(c);
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
        _row.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        // `spinbutton @extend %entry` — the whole control is one entry box; the buttons and the
        // grip are flat regions inside it, clipped to it.
        paint.AddRect(
            bounds: Bounds,
            color: AdwPalette.For(_theme).ButtonFill,
            radius: AdwMetrics.ControlRadius
        );
        paint.AddClipStart(Bounds);
        _row.Paint(paint);
        paint.AddClipEnd();

        if (_field.Focused)
        {
            paint.AddBorder(
                bounds: Bounds,
                color: _theme.FocusRing,
                radius: AdwMetrics.ControlRadius,
                width: _theme.FocusRingWidth
            );
        }
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _row.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren() => [_row];

    private string Format(float v)
    {
        return v.ToString(
            format: $"F{Decimals}",
            provider: CultureInfo.InvariantCulture
        );
    }

    /// <summary>
    ///     One of the spin button's flat −/+ buttons: a glyph over the box fill that washes on
    ///     hover and press, divided from the value by a 1px <c>currentColor 10%</c> border.
    /// </summary>
    private sealed class StepButton(string glyph, Action onPressed) : Widget
    {
        private bool _hovered;
        private bool _pressed;
        private Size _size;
        private ThemeData _theme = ThemeData.Dark;

        public override int DebugStateHash() =>
            HashCode.Combine(value1: _hovered, value2: _pressed);

        public override Size Measure(Constraints c)
        {
            _theme = ThemeProvider.Of(BuildContext.Current);
            // Square at the entry height; a finger target on a touch layout.
            float side = AdwTouchMetrics.IsCompact
                ? AdwTouchMetrics.MinTarget
                : AdwMetrics.EntryHeight;
            _size = c.Constrain(new Size(width: side, height: AdwMetrics.EntryHeight));
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
        }

        public override void Paint(PaintList paint)
        {
            var p = AdwPalette.For(_theme);
            if (_pressed || _hovered)
                paint.AddRect(bounds: Bounds, color: _pressed ? p.ActiveFill : p.HoverFill);
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X,
                    y: Bounds.Y,
                    width: 1f,
                    height: Bounds.Height
                ),
                color: p.ButtonFill
            );
            Icons.Draw(
                paint: paint,
                glyph: glyph,
                box: Bounds,
                color: _theme.OnBackground,
                size: AdwMetrics.IconSize
            );
        }

        public override void OnPointerEnter()
        {
            _hovered = true;
            MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            _hovered = false;
            _pressed = false;
            MarkNeedsPaint();
        }

        public override void OnPointerDown(Offset point)
        {
            _pressed = true;
            MarkNeedsPaint();
        }

        public override void OnPointerUp(Offset point)
        {
            if (!_pressed) return;
            _pressed = false;
            MarkNeedsPaint();
            if (Bounds.Contains(px: point.X, py: point.Y)) onPressed();
        }

        public override void OnPointerCancel()
        {
            _pressed = false;
            MarkNeedsPaint();
        }
    }

    /// <summary>
    ///     A narrow, full-height affordance painted with a vertical-dots / "⇔" hint. Dragging it
    ///     horizontally scrubs the owning <see cref="NumberInput" />'s value. It deliberately does
    ///     NOT take keyboard focus so the user can still click the text field to type.
    /// </summary>
    private sealed class ScrubGrip(AdwNumberInput owner) : Widget
    {
        private const float GripWidth = 16f;

        private bool _dragging;
        private bool _hovered;
        private float _lastX;
        private float _measureH;
        private float _measureW = GripWidth;
        private ThemeData _theme = ThemeData.Dark;

        public override bool Focusable => false;

        public override int DebugStateHash() =>
            HashCode.Combine(value1: _dragging, value2: _hovered);

        public override Size Measure(Constraints c)
        {
            _theme = ThemeProvider.Of(BuildContext.Current);
            float h = float.IsFinite(c.MaxHeight)
                ? Math.Clamp(
                    value: AdwMetrics.EntryHeight,
                    min: c.MinHeight,
                    max: c.MaxHeight
                )
                : AdwMetrics.EntryHeight;
            _measureH = h;
            // A 14pt drag strip whose only cue is a hover cursor is dead weight on a phone; collapse
            // it so the field and the ± buttons get the whole row.
            _measureW = AdwTouchMetrics.IsCompact ? 0f : GripWidth;
            return c.Constrain(new Size(width: _measureW, height: h));
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _measureW,
                height: _measureH
            );
        }

        public override void Paint(PaintList paint)
        {
            // Subtle dotted "grip" affordance — three vertical dots, brighter while dragging.
            var color = _theme.OnBackground.WithAlpha(
                _dragging || _hovered ? AdwStyle.DimOpacity : AdwStyle.DimOpacity * 0.5f
            );
            float cx = Bounds.X + (Bounds.Width / 2f);
            float cy = Bounds.Y + (Bounds.Height / 2f);
            const float dot = 1.5f;
            const float gap = 4f;
            for (int i = -1; i <= 1; i++)
            {
                float y = cy + (i * gap);
                paint.AddRect(
                    bounds: new Rect(
                        x: cx - (dot / 2f),
                        y: y - (dot / 2f),
                        width: dot,
                        height: dot
                    ),
                    color: color,
                    radius: dot / 2f
                );
            }
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
            owner.OnScrubStart?.Invoke();
            _dragging = true;
            _lastX = point.X;
            // Do NOT call RequestFocus — keep keyboard focus available for click-to-type.
            MarkNeedsPaint();
        }

        public override void OnPointerMove(Offset point)
        {
            if (!_dragging) return;
            float delta = point.X - _lastX;
            _lastX = point.X;
            if (delta == 0f) return;

            bool fine = App.Active?.CurrentModifiers.HasFlag(Modifiers.Shift) ?? false;
            float sensitivity = owner.ScrubSensitivity * (fine ? 0.1f : 1f);
            owner.Scrub(owner.Value + (delta * owner.Step * sensitivity));
        }

        public override void OnPointerUp(Offset point)
        {
            if (!_dragging) return;
            _dragging = false;
            owner.OnScrubEnd?.Invoke();
            MarkNeedsPaint();
        }

        /// <summary>The press was taken over (pinch, app background): end the scrub.</summary>
        public override void OnPointerCancel()
        {
            if (!_dragging) return;
            _dragging = false;
            owner.OnScrubEnd?.Invoke();
            MarkNeedsPaint();
        }

        public override MouseCursor? GetCursor(Offset point) => MouseCursor.ResizeEW;

        /// <summary>A scrub in progress owns the gesture, the way a slider's does.</summary>
        public override bool CanTouchDrag(bool vertical) => _dragging;
    }
}
