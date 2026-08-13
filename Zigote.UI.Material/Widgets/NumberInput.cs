using System.Globalization;
using Zigote.Core.Events;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>
///     Numeric spin-box: a flat text field flanked by compact square ▲/▼ step buttons, with a
///     Blender/Unity-style horizontal drag-scrub grip on the leading edge. Dragging the grip
///     left/right nudges <see cref="Value" /> live; hold Shift for fine (0.1×) precision.
///     Value stays within [<see cref="Min" />, <see cref="Max" />].
/// </summary>
public sealed class NumberInput : Widget
{
    private readonly Button _btnDown;
    private readonly Button _btnUp;

    private readonly TextField _field;
    private readonly ScrubGrip _grip;
    private readonly Row _row;
    private int _decimals = 2;
    private bool _editingText;
    private float _max = float.PositiveInfinity;
    private float _min = float.NegativeInfinity;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    private float _value;

    public NumberInput(float value, float step = 1f,
        float min = float.NegativeInfinity, float max = float.PositiveInfinity)
    {
        Value = value;
        Step = step;
        _min = min;
        _max = max;

        _field = new TextField {
            Text = Format(value),
            Height = ControlMetrics.RegularHeight,
            MinWidth = 60f,
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

        _btnUp = SmallButton(label: "+", onClick: () => Commit(Value + Step));
        _btnDown = SmallButton(label: "-", onClick: () => Commit(Value - Step));

        _grip = new ScrubGrip(this);

        _row = new Row {
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = {
                _grip,
                new SizedBox { Width = Spacing.Xxs },
                new Expanded(_field),
                new SizedBox { Width = Spacing.Xs },
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
        if (newWidget is NumberInput n)
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
        // On a phone the ± buttons are the only usable way to change the value (the scrub grip
        // measures itself away), so they get finger-sized padding instead of the dense 4×2.
        var pad = TouchMetrics.IsCompact
            ? EdgeInsets.Symmetric(horizontal: Spacing.Md, vertical: Spacing.Md)
            : EdgeInsets.Symmetric(horizontal: Spacing.Xs, vertical: Spacing.Xxs);
        _btnUp.Padding = pad;
        _btnDown.Padding = pad;
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

    public override void Paint(PaintList paint) => _row.Paint(paint);

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

    private static Button SmallButton(string label, Action onClick)
    {
        return new Button(label: label, onPressed: onClick) {
            Style = ButtonStyle.Outlined,
            Radius = Radii.Md,
            Padding = EdgeInsets.Symmetric(horizontal: Spacing.Xs, vertical: Spacing.Xxs),
            FontSize = ThemeData.Dark.FontSizeBody,
        };
    }

    /// <summary>
    ///     A narrow, full-height affordance painted with a vertical-dots / "⇔" hint. Dragging it
    ///     horizontally scrubs the owning <see cref="NumberInput" />'s value. It deliberately does
    ///     NOT take keyboard focus so the user can still click the text field to type.
    /// </summary>
    private sealed class ScrubGrip(NumberInput owner) : Widget
    {
        private const float GripWidth = 14f;

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
                    value: ControlMetrics.RegularHeight,
                    min: c.MinHeight,
                    max: c.MaxHeight
                )
                : ControlMetrics.RegularHeight;
            _measureH = h;
            // A 14pt drag strip whose only cue is a hover cursor is dead weight on a phone; collapse
            // it so the field and the ± buttons get the whole row.
            _measureW = TouchMetrics.IsCompact ? 0f : GripWidth;
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
            var color = _dragging || _hovered ? _theme.Label2 : _theme.Label3;
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

        /// <summary>A scrub in progress owns the gesture, the way a slider's does.</summary>
        public override bool CanTouchDrag(bool vertical) => _dragging;
    }
}
