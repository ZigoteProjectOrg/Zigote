using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     Retained-widget building blocks for devtools panels, drawn in the Adwaita idiom: the GNOME type
///     ramp (<see cref="AdwTypography" />), the named-colour palette (<see cref="AdwPalette" />) and
///     the
///     libadwaita control metrics. Panels compose these instead of hand-painting, keep references to
///     the
///     ones they mutate (e.g. a <see cref="DevKeyValue" />'s <see cref="DevKeyValue.Value" />) and
///     update
///     them in <see cref="IDevPanel.Refresh" />.
/// </summary>
public static class DevKit
{
    /// <summary>Secondary/detail text size — Adwaita <c>.caption</c>.</summary>
    public const float CaptionSize = 12f;

    /// <summary>
    ///     Dense readout row height on a pointer screen. Half of Adwaita's 50px list row — devtools are
    ///     dense by nature — but not so short that the first and last rows of a 12px-radius card collide
    ///     with its rounded corners, which is what 28 did.
    /// </summary>
    public const float RowHeight = 36f;

    /// <summary>Below this the surface is a phone, not merely a narrow pane or a small window.</summary>
    private const float PhoneWidth = 400f;

    /// <summary>Horizontal inset of a readout row — matches the Adwaita boxed-list inset.</summary>
    public const float RowInset = AdwMetrics.RowPaddingX;

    // Twelve hues spread so neighbouring depths never read as the same colour — the same idea as the
    // overlay's repaint rainbow, reused for tree-depth guides.
    private static readonly Color[] Depths = [
        Color.Rgb(r: 0xE0, g: 0x6C, b: 0x75), Color.Rgb(r: 0xE5, g: 0xA5, b: 0x4B),
        Color.Rgb(r: 0xD8, g: 0xCF, b: 0x54),
        Color.Rgb(r: 0x98, g: 0xC3, b: 0x79), Color.Rgb(r: 0x56, g: 0xC2, b: 0x8E),
        Color.Rgb(r: 0x56, g: 0xB6, b: 0xC2),
        Color.Rgb(r: 0x61, g: 0xAF, b: 0xEF), Color.Rgb(r: 0x82, g: 0x8B, b: 0xF0),
        Color.Rgb(r: 0xB0, g: 0x77, b: 0xE8),
        Color.Rgb(r: 0xD4, g: 0x70, b: 0xD0), Color.Rgb(r: 0xEA, g: 0x6F, b: 0xA8),
        Color.Rgb(r: 0xC0, g: 0x92, b: 0x6B),
    ];

    /// <summary>
    ///     True when controls should be finger-sized: a touch pointer is driving, or the surface is
    ///     phone-sized. Deliberately not "the pane is narrow" — a 408px docked column and a 520px
    ///     torn-off devtools window are both narrow and both driven by a mouse, and they must keep the
    ///     same dense rhythm as each other.
    /// </summary>
    public static bool Compact =>
        App.PointerIsTouch || MediaQuery.Of(BuildContext.Current).Width < PhoneWidth;

    /// <summary>Dense row height on a pointer screen, a finger-sized target on a phone.</summary>
    public static float Row => Compact ? ControlMetrics.MinTouchTarget : RowHeight;

    /// <summary>The guide colour for a tree depth — cycles every 12 levels.</summary>
    public static Color DepthColor(int depth) =>
        Depths[((depth % Depths.Length) + Depths.Length) % Depths.Length];
}

/// <summary>A boxed-list group heading: Adwaita <c>.heading</c> over the rows it introduces.</summary>
public sealed class DevSectionHeader(string title) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var t = ThemeProvider.Of(context);
        return new Padding(
            padding: EdgeInsets.Only(top: Spacing.Lg, bottom: Spacing.Sm),
            child: new Label(text: title, style: AdwTypography.Heading, color: t.OnBackground) {
                MaxLines = 1,
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
            width: float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth,
            height: float.IsFinite(c.MaxHeight) ? c.MaxHeight : c.MinHeight
        );
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
        if (Color.A > 0f && Bounds is { Width: > 0f, Height: > 0f })
            paint.AddRect(bounds: Bounds, color: Color, radius: Radius);
    }

    public override int DebugStateHash() => HashCode.Combine(
        value1: Color,
        value2: Radius,
        value3: Bounds.Width
    );
}

/// <summary>
///     A key → value readout row, laid out like an <see cref="AdwActionRow" /> but at devtools
///     density:
///     the key in body text on the left, a monospace value on the right. The <see cref="Value" /> and
///     <see cref="ValueColor" /> are live-mutable so panels update them each frame without a rebuild.
/// </summary>
public sealed class DevKeyValue : ComposedWidget
{
    private readonly string _key;
    private readonly Label _valueLabel;

    public DevKeyValue(string key, string value = "", Color? valueColor = null, bool mono = true)
    {
        _key = key;
        _valueLabel =
            new Label(text: value, style: mono ? AdwTypography.Monospace : AdwTypography.Caption) {
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
                Align = TextAlign.Right,
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
        _valueLabel.Color ??= t.OnSurface;
        return new SizedBox(
            height: DevKit.Row,
            child: new Padding(
                padding: EdgeInsets.Symmetric(DevKit.RowInset),
                child: new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        // Key at its natural width, value takes everything left and ellipsizes if it
                        // still does not fit. Sharing the row between two Flexibles and a Spacer (as
                        // this did) gave the value a third of the row, which clipped every long
                        // readout — flex children split what is left AFTER the fixed ones.
                        new Label(
                            text: _key,
                            style: AdwTypography.Caption,
                            color: t.TextSecondary
                        ) {
                            MaxLines = 1,
                            Overflow = TextOverflow.Ellipsis,
                        },
                        new SizedBox(Spacing.Sm),
                        new Expanded(_valueLabel),
                    },
                }
            )
        );
    }
}

/// <summary>
///     A dim caption line (hints / notes), Adwaita <c>.caption</c>. <see cref="Text" /> is
///     live-mutable.
/// </summary>
public sealed class DevNote : ComposedWidget
{
    private readonly Label _label;

    public DevNote(string text, Color? color = null) =>
        _label = new Label(text: text, style: AdwTypography.Caption) { Color = color };

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
        _label.Color ??= AdwPalette.For(ThemeProvider.Of(context)).DimLabel;
        return new Padding(
            padding: EdgeInsets.Symmetric(horizontal: DevKit.RowInset, vertical: Spacing.Sm),
            child: _label
        );
    }
}

/// <summary>
///     A labelled meter: key + value on one line over an Adwaita progress bar. Panels mutate
///     <see cref="Value" />, <see cref="Fraction" /> and <see cref="Color" /> each frame.
/// </summary>
public sealed class DevMeter : ComposedWidget
{
    private readonly DevFillBox _fill = new(color: Color.Transparent, radius: AdwMetrics.Pill);
    private readonly string _key;
    private readonly Label _valueLabel;
    private FractionBox _fraction = null!;

    public DevMeter(string key, Color color)
    {
        _key = key;
        _valueLabel = new Label(text: "", style: AdwTypography.Monospace) {
            MaxLines = 1,
            Overflow = TextOverflow.Ellipsis,
            Align = TextAlign.Right,
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
        var p = AdwPalette.For(t);
        _valueLabel.Color ??= t.OnSurface;
        _fraction = new FractionBox(
            fill: _fill,
            track: p.ButtonFill,
            height: AdwMetrics.ProgressBarHeight
        );
        return new Padding(
            padding: EdgeInsets.Symmetric(horizontal: DevKit.RowInset, vertical: Spacing.Sm),
            child: new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                        Children = {
                            new Label(text: _key, style: AdwTypography.Caption, color: p.DimLabel) {
                                MaxLines = 1,
                                Overflow = TextOverflow.Ellipsis,
                            },
                            new SizedBox(Spacing.Sm),
                            new Expanded(_valueLabel),
                        },
                    },
                    new SizedBox(height: Spacing.Xs),
                    _fraction,
                },
            }
        );
    }

    /// <summary>A track rect with a proportional fill of <see cref="Fill" />; sizes to full width.</summary>
    private sealed class FractionBox(DevFillBox fill, Color track, float height) : Widget
    {
        private readonly DevFillBox _track = new(color: track, radius: AdwMetrics.Pill);
        private Size _size;

        public DevFillBox Fill { get; } = fill;
        public float Fraction { get; set; }

        public override Size Measure(Constraints c)
        {
            float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : c.MinWidth;
            _size = new Size(width: w, height: height);
            _track.Measure(Constraints.Tight(width: w, height: height));
            Fill.Measure(Constraints.Tight(width: w, height: height));
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
            _track.Layout(origin);
            float fw = _size.Width * Math.Clamp(value: Fraction, min: 0f, max: 1f);
            Fill.Measure(Constraints.Tight(width: fw, height: _size.Height));
            Fill.Layout(origin);
        }

        public override void Paint(PaintList paint)
        {
            _track.Paint(paint);
            if (Fraction > 0f) Fill.Paint(paint);
        }

        public override IEnumerable<Widget> GetChildren() => [_track, Fill];
    }
}

/// <summary>
///     A label + switch row — an <see cref="AdwSwitchRow" />, so the whole row is the (finger-sized)
///     target and the state reaches a screen reader. <see cref="OnChanged" /> fires with the new
///     value.
/// </summary>
public sealed class DevToggle : ComposedWidget
{
    private readonly AdwSwitchRow _row;

    public DevToggle(string label, bool value, Action<bool> onChanged, bool enabled = true)
    {
        Label = label;
        OnChanged = onChanged;
        _row = new AdwSwitchRow(title: label, value: value, onChanged: onChanged) {
            Enabled = enabled,
        };
    }

    public string Label { get; }
    public Action<bool> OnChanged { get; }

    public bool Enabled
    {
        get => _row.Enabled;
        set => _row.Enabled = value;
    }

    public bool Value
    {
        get => _row.Value;
        set => _row.Value = value;
    }

    protected override Widget Build(BuildContext context) => _row;
}

/// <summary>
///     A label + ◀ value ▶ stepper row for values that cycle rather than count (enum debug views,
///     variable presets). Chevrons fire <see cref="OnPrev" />/<see cref="OnNext" />.
/// </summary>
public sealed class DevStepper : ComposedWidget
{
    private readonly Label _valueLabel = new(text: "", style: AdwTypography.Monospace) {
        MaxLines = 1,
        Overflow = TextOverflow.Ellipsis,
        Align = TextAlign.Center,
    };

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
            _valueLabel.Text = value;
        }
    }

    protected override Widget Build(BuildContext context)
    {
        var t = ThemeProvider.Of(context);
        _valueLabel.Text = Value;
        _valueLabel.Color = t.OnSurface;
        return new SizedBox(
            height: MathF.Max(x: AdwMetrics.RowMinHeight, y: DevKit.Row),
            child: new Padding(
                padding: EdgeInsets.Symmetric(DevKit.RowInset),
                child: new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        new Flexible(
                            child: new Label(
                                text: Label,
                                style: AdwTypography.Body,
                                color: t.OnSurface
                            ) {
                                MaxLines = 1,
                                Overflow = TextOverflow.Ellipsis,
                            },
                            fit: FlexFit.Loose
                        ),
                        new Spacer(),
                        Chip(icon: Icons.ChevronLeft, label: "Previous", onTap: OnPrev),
                        new SizedBox(width: 88f, child: _valueLabel),
                        Chip(icon: Icons.ChevronRight, label: "Next", onTap: OnNext),
                    },
                }
            )
        );
    }

    private static AdwButton Chip(string icon, string label, Action onTap)
    {
        return new AdwButton(label: label, onPressed: onTap) {
            IconName = icon,
            Style = AdwButtonStyle.Flat,
            Circular = true,
            Compact = true,
        };
    }
}
