using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     Retained-widget building blocks for devtools panels, drawn in the Adwaita idiom: the GNOME type
///     ramp (<see cref="AdwTypography" />), the named-colour palette (<see cref="AdwPalette" />) and the
///     libadwaita control metrics. Panels compose these instead of hand-painting, keep references to the
///     ones they mutate (e.g. a <see cref="DevKeyValue" />'s <see cref="DevKeyValue.Value" />) and update
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

    /// <summary>Horizontal inset of a readout row — matches the Adwaita boxed-list inset.</summary>
    public const float RowInset = AdwMetrics.RowPaddingX;

    // Twelve hues spread so neighbouring depths never read as the same colour — the same idea as the
    // overlay's repaint rainbow, reused for tree-depth guides.
    private static readonly Color[] Depths = [
        Color.Rgb(0xE0, 0x6C, 0x75), Color.Rgb(0xE5, 0xA5, 0x4B), Color.Rgb(0xD8, 0xCF, 0x54),
        Color.Rgb(0x98, 0xC3, 0x79), Color.Rgb(0x56, 0xC2, 0x8E), Color.Rgb(0x56, 0xB6, 0xC2),
        Color.Rgb(0x61, 0xAF, 0xEF), Color.Rgb(0x82, 0x8B, 0xF0), Color.Rgb(0xB0, 0x77, 0xE8),
        Color.Rgb(0xD4, 0x70, 0xD0), Color.Rgb(0xEA, 0x6F, 0xA8), Color.Rgb(0xC0, 0x92, 0x6B),
    ];

    /// <summary>The guide colour for a tree depth — cycles every 12 levels.</summary>
    public static Color DepthColor(int depth)
    {
        return Depths[(depth % Depths.Length + Depths.Length) % Depths.Length];
    }
}

/// <summary>A boxed-list group heading: Adwaita <c>.heading</c> over the rows it introduces.</summary>
public sealed class DevSectionHeader(string title) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var t = ThemeProvider.Of(context);
        return new Padding(
            EdgeInsets.Only(top: Spacing.Lg, bottom: Spacing.Sm),
            new Label(title, AdwTypography.Heading, t.OnBackground) { MaxLines = 1 }
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
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
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
///     A key → value readout row, laid out like an <see cref="AdwActionRow" /> but at devtools density:
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
        _valueLabel = new Label(value, mono ? AdwTypography.Monospace : AdwTypography.Caption) {
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
                EdgeInsets.Symmetric(DevKit.RowInset),
                new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        // Key at its natural width, value takes everything left and ellipsizes if it
                        // still does not fit. Sharing the row between two Flexibles and a Spacer (as
                        // this did) gave the value a third of the row, which clipped every long
                        // readout — flex children split what is left AFTER the fixed ones.
                        new Label(_key, AdwTypography.Caption, t.TextSecondary) {
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

/// <summary>A dim caption line (hints / notes), Adwaita <c>.caption</c>. <see cref="Text" /> is live-mutable.</summary>
public sealed class DevNote : ComposedWidget
{
    private readonly Label _label;

    public DevNote(string text, Color? color = null)
    {
        _label = new Label(text, AdwTypography.Caption) { Color = color };
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
        _label.Color ??= AdwPalette.For(ThemeProvider.Of(context)).DimLabel;
        return new Padding(EdgeInsets.Symmetric(DevKit.RowInset, Spacing.Sm), _label);
    }
}

/// <summary>
///     A labelled meter: key + value on one line over an Adwaita progress bar. Panels mutate
///     <see cref="Value" />, <see cref="Fraction" /> and <see cref="Color" /> each frame.
/// </summary>
public sealed class DevMeter : ComposedWidget
{
    private readonly DevFillBox _fill = new(Color.Transparent, AdwMetrics.Pill);
    private readonly string _key;
    private readonly Label _valueLabel;
    private FractionBox _fraction = null!;

    public DevMeter(string key, Color color)
    {
        _key = key;
        _valueLabel = new Label("", AdwTypography.Monospace) {
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
        _fraction = new FractionBox(_fill, p.ButtonFill, AdwMetrics.ProgressBarHeight);
        return new Padding(
            EdgeInsets.Symmetric(DevKit.RowInset, Spacing.Sm),
            new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                        Children = {
                            new Label(_key, AdwTypography.Caption, p.DimLabel) {
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
        private readonly DevFillBox _track = new(track, AdwMetrics.Pill);
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
            Bounds = new Rect(
                origin.X,
                origin.Y,
                _size.Width,
                _size.Height
            );
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

/// <summary>
///     A label + switch row — an <see cref="AdwSwitchRow" />, so the whole row is the (finger-sized)
///     target and the state reaches a screen reader. <see cref="OnChanged" /> fires with the new value.
/// </summary>
public sealed class DevToggle : ComposedWidget
{
    private readonly AdwSwitchRow _row;

    public DevToggle(string label, bool value, Action<bool> onChanged, bool enabled = true)
    {
        Label = label;
        OnChanged = onChanged;
        _row = new AdwSwitchRow(label, value: value, onChanged: onChanged) { Enabled = enabled };
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

    protected override Widget Build(BuildContext context)
    {
        return _row;
    }
}

/// <summary>
///     A label + ◀ value ▶ stepper row for values that cycle rather than count (enum debug views,
///     variable presets). Chevrons fire <see cref="OnPrev" />/<see cref="OnNext" />.
/// </summary>
public sealed class DevStepper : ComposedWidget
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
            _valueLabel.Text = value;
        }
    }

    private readonly Label _valueLabel = new("", AdwTypography.Monospace) {
        MaxLines = 1,
        Overflow = TextOverflow.Ellipsis,
        Align = TextAlign.Center,
    };

    protected override Widget Build(BuildContext context)
    {
        var t = ThemeProvider.Of(context);
        _valueLabel.Text = Value;
        _valueLabel.Color = t.OnSurface;
        return new SizedBox(
            height: MathF.Max(AdwMetrics.RowMinHeight, DevKit.Row),
            child: new Padding(
                EdgeInsets.Symmetric(DevKit.RowInset),
                new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        new Flexible(
                            new Label(Label, AdwTypography.Body, t.OnSurface) {
                                MaxLines = 1,
                                Overflow = TextOverflow.Ellipsis,
                            },
                            fit: FlexFit.Loose
                        ),
                        new Spacer(),
                        Chip(Icons.ChevronLeft, "Previous", OnPrev),
                        new SizedBox(88f, child: _valueLabel),
                        Chip(Icons.ChevronRight, "Next", OnNext),
                    },
                }
            )
        );
    }

    private static AdwButton Chip(string icon, string label, Action onTap)
    {
        return new AdwButton(label, onTap) {
            IconName = icon,
            Style = AdwButtonStyle.Flat,
            Circular = true,
            Compact = true,
        };
    }
}
