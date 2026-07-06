using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     Retained-widget building blocks for devtools panels — the widget-native replacements for the old
///     immediate-mode <c>DebugUi</c> primitives. Every panel composes these instead of hand-painting, so
///     hover/focus/state and the framework's layout all come for free. Panels keep references to the
///     ones they mutate (e.g. a <see cref="DevKeyValue" />'s <see cref="DevKeyValue.Value" />) and update
///     them in <see cref="IDevPanel.Refresh" />.
/// </summary>
public static class DevKit
{
    public const float CaptionSize = 11.5f;
    public const float RowHeight = 20f;
}

/// <summary>A section header: a short accent bar + a bold caption. Static once built.</summary>
public sealed class DevSectionHeader(string title) : StatelessWidget
{
    protected override Widget Build(BuildContext context)
    {
        var t = ThemeProvider.Of(context);
        return new Padding(
            EdgeInsets.Only(top: Spacing.Sm, bottom: Spacing.Xxs),
            new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                Children = {
                    new Padding(
                        EdgeInsets.Only(right: Spacing.Sm),
                        new SizedBox(3f, 12f, new DevFillBox(t.Primary, 1.5f))
                    ),
                    new Label(title, DevKit.CaptionSize + 1.5f, t.Primary)
                        { FontWeight = FontWeight.SemiBold, MaxLines = 1 },
                },
            }
        );
    }
}

/// <summary>A flat filled rounded rectangle that fills its box — the paint atom behind bars/pills.</summary>
public sealed class DevFillBox(Color color, float radius = 0f) : LeafWidget
{
    private Size _size;

    public Color Color { get; set; } = color;
    public float Radius { get; set; } = radius;

    public override Size Measure(Constraints c)
    {
        _size = new Size(
            float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth,
            float.IsFinite(c.MaxHeight) ? c.MaxHeight : c.MinHeight
        );
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);
    }

    public override void Paint(PaintList paint)
    {
        if (Color.A > 0f && Bounds is { Width: > 0f, Height: > 0f })
            paint.AddRect(Bounds, Color, Radius);
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(Color, Radius, Bounds.Width);
    }
}

/// <summary>
///     A key → value row: a muted key on the left, a value (monospace by default) on the right. The
///     <see cref="Value" /> and <see cref="ValueColor" /> are live-mutable so panels update them each
///     frame without a rebuild.
/// </summary>
public sealed class DevKeyValue : StatelessWidget
{
    private readonly string _key;
    private readonly Label _valueLabel;

    public DevKeyValue(string key, string value = "", Color? valueColor = null, bool mono = true)
    {
        _key = key;
        _valueLabel = new Label(value, DevKit.CaptionSize) {
            MaxLines = 1,
            Overflow = TextOverflow.Ellipsis,
            Align = TextAlign.Right,
            FontFamily = mono ? "code" : null,
            Color = valueColor,
        };
    }

    public string Value
    {
        get => _valueLabel.Text;
        set => _valueLabel.Text = value;
    }

    public Color? ValueColor
    {
        get => _valueLabel.Color;
        set => _valueLabel.Color = value;
    }

    protected override Widget Build(BuildContext context)
    {
        var t = ThemeProvider.Of(context);
        return new SizedBox(height: DevKit.RowHeight, child: new Row(
            crossAxisAlignment: CrossAxisAlignment.Center) {
            Children = {
                new Label(_key, DevKit.CaptionSize, t.Hint) { MaxLines = 1 },
                new Spacer(),
                new Flexible(_valueLabel, fit: FlexFit.Loose),
            },
        });
    }
}

/// <summary>A single muted caption line (hints / notes). <see cref="Text" /> is live-mutable.</summary>
public sealed class DevNote : StatelessWidget
{
    private readonly Label _label;

    public DevNote(string text, Color? color = null)
    {
        _label = new Label(text, DevKit.CaptionSize) { Color = color };
    }

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    public Color? Color
    {
        get => _label.Color;
        set => _label.Color = value;
    }

    protected override Widget Build(BuildContext context)
    {
        _label.Color ??= ThemeProvider.Of(context).Hint;
        return new Padding(EdgeInsets.Symmetric(0f, 2f), _label);
    }
}

/// <summary>
///     A labelled horizontal meter: key + value on one line, a thin proportional bar beneath. Panels
///     mutate <see cref="Value" />, <see cref="Fraction" />, and <see cref="Color" /> each frame.
/// </summary>
public sealed class DevMeter : StatelessWidget
{
    private readonly DevFillBox _fill = new(Color.Transparent, 2f);
    private readonly string _key;
    private readonly Label _valueLabel;
    private FractionBox _fraction = null!;

    public DevMeter(string key, Color color)
    {
        _key = key;
        _valueLabel = new Label("", DevKit.CaptionSize) {
            MaxLines = 1,
            Align = TextAlign.Right,
            FontFamily = "code",
        };
        _fill.Color = color;
    }

    public string Value
    {
        get => _valueLabel.Text;
        set => _valueLabel.Text = value;
    }

    public float Fraction
    {
        get => _fraction.Fraction;
        set => _fraction.Fraction = value;
    }

    public Color Color
    {
        get => _fill.Color;
        set => _fill.Color = value;
    }

    protected override Widget Build(BuildContext context)
    {
        var t = ThemeProvider.Of(context);
        _fraction = new FractionBox(_fill, t.Fill3, 4f);
        return new Padding(EdgeInsets.Symmetric(0f, 2f), new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min) {
            Children = {
                new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        new Label(_key, DevKit.CaptionSize, t.Hint) { MaxLines = 1 },
                        new Spacer(),
                        new Flexible(_valueLabel, fit: FlexFit.Loose),
                    },
                },
                new SizedBox(height: 2f),
                _fraction,
            },
        });
    }

    /// <summary>A track rect with a proportional fill of <see cref="Fill" />; sizes to full width.</summary>
    private sealed class FractionBox(DevFillBox fill, Color track, float height) : RenderWidget
    {
        private readonly DevFillBox _track = new(track, 2f);
        private Size _size;

        public DevFillBox Fill { get; } = fill;
        public float Fraction { get; set; }

        public override Size Measure(Constraints c)
        {
            var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;
            _size = new Size(w, height);
            _track.Measure(Constraints.Tight(w, height));
            Fill.Measure(Constraints.Tight(w, height));
            return _size;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(origin.X, origin.Y, _size.Width, _size.Height);
            _track.Layout(origin);
            var fw = _size.Width * Math.Clamp(Fraction, 0f, 1f);
            Fill.Measure(Constraints.Tight(fw, _size.Height));
            Fill.Layout(origin);
        }

        public override void Paint(PaintList paint)
        {
            _track.Paint(paint);
            if (Fraction > 0f) Fill.Paint(paint);
        }

        public override IEnumerable<Widget> GetChildren()
        {
            return [_track, Fill];
        }
    }
}

/// <summary>A checkbox glyph leaf: a rounded box, accent-filled with a check when on.</summary>
public sealed class DevCheckGlyph : LeafWidget
{
    private const float BoxSize = 15f;
    private ThemeData _theme = ThemeData.Dark;

    public bool Checked { get; set; }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        return new Size(BoxSize, BoxSize);
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(origin.X, origin.Y, BoxSize, BoxSize);
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(Bounds, Checked ? _theme.Primary : _theme.Fill2, 4f);
        paint.AddBorder(Bounds, Checked ? _theme.Primary : _theme.Hint.WithAlpha(0.5f), 4f, 1f);
        if (Checked)
            paint.AddText("✓", Bounds.X + 2.5f, Bounds.Bottom - 3f, _theme.OnPrimary, 10.5f,
                fontWeight: FontWeight.Bold);
    }

    public override int DebugStateHash()
    {
        return Checked ? 1 : 0;
    }
}

/// <summary>A label + checkbox row. <see cref="OnChanged" /> fires with the new value when clicked.</summary>
public sealed class DevToggle : StatefulWidget
{
    private bool _value;

    public DevToggle(string label, bool value, Action<bool> onChanged, bool enabled = true)
    {
        Label = label;
        _value = value;
        OnChanged = onChanged;
        Enabled = enabled;
    }

    public string Label { get; }
    public Action<bool> OnChanged { get; }
    public bool Enabled { get; set; }

    public bool Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            (InternalState as DevToggleState)?.SyncValue(value);
        }
    }

    protected override WidgetState CreateState()
    {
        return new DevToggleState();
    }

    private sealed class DevToggleState : WidgetState<DevToggle>
    {
        private readonly DevCheckGlyph _check = new();
        private readonly DecoratedBox _hover = new() { Radius = 4f };
        private readonly Label _label = new("", DevKit.CaptionSize) { MaxLines = 1 };
        private Pressable _root = null!;
        private ThemeData _theme = ThemeData.Dark;

        public void SyncValue(bool v)
        {
            _check.Checked = v;
            _root.MarkNeedsPaint();
        }

        public override void InitState()
        {
            _hover.Child = new Padding(
                EdgeInsets.Symmetric(Spacing.Xs, 2f),
                new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = { _label, new Spacer(), _check },
                }
            );
            _root = new Pressable {
                Child = _hover,
                FocusRadius = 4f,
                OnStateChanged = ApplyColors,
                OnPressed = Toggle,
            };
        }

        private void Toggle()
        {
            if (!Widget.Enabled) return;
            Widget.Value = !Widget.Value;
            _check.Checked = Widget.Value;
            Widget.OnChanged(Widget.Value);
            _root.MarkNeedsPaint();
        }

        public override Widget Build(BuildContext context)
        {
            _theme = ThemeProvider.Of(context);
            _label.Text = Widget.Label;
            _check.Checked = Widget.Value;
            _root.Enabled = Widget.Enabled;
            _root.Role = SemanticsRole.Checkbox;
            _root.SemanticsLabel = Widget.Label;
            _root.Checked = Widget.Value;
            ApplyColors();
            return new SizedBox(height: DevKit.RowHeight + 2f, child: _root);
        }

        private void ApplyColors()
        {
            _label.Color = Widget.Enabled ? _theme.OnSurface : _theme.Hint.WithAlpha(0.5f);
            _hover.Fill = _root.Hovered && Widget.Enabled
                ? _theme.ControlHover.WithAlpha(0.5f)
                : Color.Transparent;
        }
    }
}

/// <summary>A label + ◀ value ▶ stepper row. Chevrons fire <see cref="OnPrev" />/<see cref="OnNext" />.</summary>
public sealed class DevStepper : StatefulWidget
{
    private string _value;

    public DevStepper(string label, string value, Action onPrev, Action onNext)
    {
        Label = label;
        _value = value;
        OnPrev = onPrev;
        OnNext = onNext;
    }

    public string Label { get; }
    public Action OnPrev { get; }
    public Action OnNext { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            (InternalState as DevStepperState)?.SyncValue(value);
        }
    }

    protected override WidgetState CreateState()
    {
        return new DevStepperState();
    }

    private sealed class DevStepperState : WidgetState<DevStepper>
    {
        private readonly Label _value = new("", DevKit.CaptionSize) {
            MaxLines = 1,
            Align = TextAlign.Center,
            FontFamily = "code",
        };

        public void SyncValue(string v)
        {
            _value.Text = v;
        }

        public override Widget Build(BuildContext context)
        {
            var t = ThemeProvider.Of(context);
            _value.Text = Widget.Value;
            return new SizedBox(height: DevKit.RowHeight + 2f, child: new Row(
                crossAxisAlignment: CrossAxisAlignment.Center) {
                Children = {
                    new Label(Widget.Label, DevKit.CaptionSize, t.Hint) { MaxLines = 1 },
                    new Spacer(),
                    Chip("◀", Widget.OnPrev, t),
                    new SizedBox(width: 4f),
                    new SizedBox(width: 58f, child: _value),
                    new SizedBox(width: 4f),
                    Chip("▶", Widget.OnNext, t),
                },
            });
        }

        private static Pressable Chip(string glyph, Action onTap, ThemeData t)
        {
            var box = new DecoratedBox {
                Radius = 4f,
                Fill = t.Fill2,
                Child = new SizedBox(18f, 18f, new Center(
                    new Label(glyph, DevKit.CaptionSize, t.OnSurface))),
            };
            return new Pressable {
                Child = box,
                FocusRadius = 4f,
                OnPressed = onTap,
                OnStateChanged = () => box.Fill = Color.Transparent,
            }.WithHoverFill(box, t);
        }
    }
}

internal static class PressableChipExtensions
{
    // Small helper so the stepper chip recolours on hover without a bespoke state class.
    public static Pressable WithHoverFill(this Pressable p, DecoratedBox box, ThemeData t)
    {
        p.OnStateChanged = () => box.Fill = p.Hovered ? t.ControlHover : t.Fill2;
        return p;
    }
}
