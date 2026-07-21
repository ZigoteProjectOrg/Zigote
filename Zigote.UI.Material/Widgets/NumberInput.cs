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
    private bool _editingText;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    private float _value;
    private float _min = float.NegativeInfinity;
    private float _max = float.PositiveInfinity;
    private float _step = 1f;
    private int _decimals = 2;

    public NumberInput(float value, float step = 1f,
        float min = float.NegativeInfinity, float max = float.PositiveInfinity)
    {
        Value = value;
        _step = step;
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
                        s,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var v
                    ))
                    Commit(v);
            },
        };

        _btnUp = SmallButton("+", () => Commit(Value + Step));
        _btnDown = SmallButton("-", () => Commit(Value - Step));

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
            var clamped = Math.Clamp(Value, _min, _max);
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
            var clamped = Math.Clamp(Value, _min, _max);
            if (clamped != Value) Value = clamped;
            MarkNeedsPaint();
        }
    }

    public float Step
    {
        get => _step;
        set => _step = value;
    }

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

    public override int DebugStateHash()
    {
        return HashCode.Combine(Value, _grip.DebugStateHash());
    }

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
        var clamped = Math.Clamp(v, Min, Max);
        if (MathF.Abs(clamped - Value) < 1e-9f && _editingText) return;
        Value = clamped;
        if (!_editingText) _field.Text = Format(clamped);
        _editingText = false;
        OnChanged?.Invoke(clamped);
    }

    /// <summary>Applies a raw value from the scrub grip: clamp, round to Decimals, fire OnChanged live.</summary>
    private void Scrub(float v)
    {
        var clamped = Math.Clamp(v, Min, Max);
        var rounded = MathF.Round(clamped, Math.Clamp(Decimals, 0, 15));
        if (MathF.Abs(rounded - Value) < 1e-9f) return;
        _editingText = false;
        Value = rounded;
        _field.Text = Format(rounded);
        OnChanged?.Invoke(rounded);
    }

    public void SyncText()
    {
        _field.Text = Format(Value);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = _row.Measure(c);
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
        _row.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _row.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _row.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_row];
    }

    private string Format(float v)
    {
        return v.ToString(
            $"F{Decimals}",
            CultureInfo.InvariantCulture
        );
    }

    private static Button SmallButton(string label, Action onClick)
    {
        return new Button(label, onClick) {
            Style = ButtonStyle.Outlined,
            Radius = Radii.Md,
            Padding = EdgeInsets.Symmetric(Spacing.Xs, Spacing.Xxs),
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
        private ThemeData _theme = ThemeData.Dark;

        public override bool Focusable => false;

        public override int DebugStateHash()
        {
            return HashCode.Combine(_dragging, _hovered);
        }

        public override Size Measure(Constraints c)
        {
            _theme = ThemeProvider.Of(BuildContext.Current);
            var h = float.IsFinite(c.MaxHeight)
                ? Math.Clamp(ControlMetrics.RegularHeight, c.MinHeight, c.MaxHeight)
                : ControlMetrics.RegularHeight;
            _measureH = h;
            return c.Constrain(new Size(GripWidth, h));
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                GripWidth,
                _measureH
            );
        }

        public override void Paint(PaintList paint)
        {
            // Subtle dotted "grip" affordance — three vertical dots, brighter while dragging.
            var color = _dragging || _hovered ? _theme.Label2 : _theme.Label3;
            var cx = Bounds.X + Bounds.Width / 2f;
            var cy = Bounds.Y + Bounds.Height / 2f;
            const float dot = 1.5f;
            const float gap = 4f;
            for (var i = -1; i <= 1; i++)
            {
                var y = cy + i * gap;
                paint.AddRect(
                    new Rect(
                        cx - dot / 2f,
                        y - dot / 2f,
                        dot,
                        dot
                    ),
                    color,
                    dot / 2f
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
            var delta = point.X - _lastX;
            _lastX = point.X;
            if (delta == 0f) return;

            var fine = App.Active?.CurrentModifiers.HasFlag(Modifiers.Shift) ?? false;
            var sensitivity = owner.ScrubSensitivity * (fine ? 0.1f : 1f);
            owner.Scrub(owner.Value + delta * owner.Step * sensitivity);
        }

        public override void OnPointerUp(Offset point)
        {
            if (!_dragging) return;
            _dragging = false;
            owner.OnScrubEnd?.Invoke();
            MarkNeedsPaint();
        }
    }
}