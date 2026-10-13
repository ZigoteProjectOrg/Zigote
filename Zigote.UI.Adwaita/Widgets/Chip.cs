using Zigote.Core.Animation;
using LayoutPadding = Zigote.UI.Widgets.Layout.Padding;

namespace Zigote.UI.Adwaita;

/// <summary>
///     A pill toggle used as a selectable tag or filter — the small <c>.pill</c> toggle button GNOME
///     apps use for category and filter rows. It takes the raised-button fill ladder: the neutral
///     <c>currentColor 10%</c> at rest and the 30% "checked" step when selected, with no border.
///     A <see cref="Color" /> overrides the selected fill for chips that carry a meaning of their
///     own (a tag colour). Composed from <see cref="Pressable" /> over a pill
///     <see cref="DecoratedBox" /> + centred <see cref="Label" />.
/// </summary>
public sealed class AdwChip : ComposedWidget
{
    private readonly DecoratedBox _box = new();

    private readonly ConstrainedBox _minHeight =
        new(new Constraints(minHeight: AdwMetrics.CompactControlHeight));

    private readonly LayoutPadding _padding = new(EdgeInsets.Zero);

    private readonly Pressable _root;
    private readonly Label _text = new("") { MaxLines = 1 };

    private Color? _color;
    private bool _enabled = true;
    private string _label;
    private AnimationController _sel = null!;
    private bool _selTarget;
    private bool _selected;
    private ThemeData _theme = ThemeData.Dark;

    public AdwChip(string label, bool selected = false, Action? onPressed = null)
    {
        _label = label;
        _selected = selected;
        OnPressed = onPressed;

        _padding.Child = _text;
        _box.Radius = AdwMetrics.Pill;
        _minHeight.Child = new Align(alignment: Alignment.Center, child: _padding) {
            WidthFactor = 1f,
            HeightFactor = 1f,
        };
        _box.Child = _minHeight;
        _root = new Pressable {
            Child = _box,
            FocusRadius = AdwMetrics.Pill,
            OnStateChanged = ApplyColors,
            OnPressed = () => OnPressed?.Invoke(),
        };
    }

    public string Label
    {
        get => _label;
        set => SetBuild(field: ref _label, value: value);
    }

    public bool Selected
    {
        get => _selected;
        set => SetBuild(field: ref _selected, value: value);
    }

    public Action? OnPressed { get; set; }

    public Color? Color
    {
        get => _color;
        set
        {
            _color = value;
            MarkNeedsBuild();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetBuild(field: ref _enabled, value: value);
    }

    // The controller owns a Ticker, whose lifetime is the mount period — so it is built here rather
    // than in the constructor, and rebuilt if this chip is re-attached.
    protected override void OnMount()
    {
        // Crossfades the fill/border/text between the neutral and accent states on selection.
        _sel = new AnimationController(durationSeconds: Durations.Fast, vsync: this) {
            Curve = Curves.EaseOut,
        };
        _sel.OnTick += () =>
        {
            ApplyColors();
            _root.MarkNeedsPaint();
        };
        _selTarget = Selected;
        if (_selTarget) _sel.Complete();
        else _sel.Dismiss();
    }

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is AdwChip c)
        {
            Label = c.Label;
            Selected = c.Selected;
            OnPressed = c.OnPressed;
            Color = c.Color;
            Enabled = c.Enabled;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            value1: Label,
            value2: Selected,
            value3: Enabled,
            value4: base.DebugStateHash()
        );
    }

    protected override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);

        if (Selected != _selTarget)
        {
            _selTarget = Selected;
            if (_selTarget) _sel.Forward();
            else _sel.Reverse();
        }

        _text.Text = Label;
        _text.FontSize = AdwTypography.Body.Size;
        _text.FontWeight = FontWeight.Normal;
        // Filter/choice chips are toggles: 22pt tall is unusable with a finger. Grow the capsule
        // itself (a hit-rect trick would overlap neighbours in a tightly-spaced Wrap).
        bool compact = AdwTouchMetrics.IsCompact;
        _minHeight.Constraints = new Constraints(
            minHeight: compact ? 36f : AdwMetrics.CompactControlHeight
        );
        _padding.Insets = EdgeInsets.Symmetric(
            horizontal: compact ? Spacing.Lg : AdwMetrics.RowPaddingX,
            vertical: Spacing.Xxs
        );
        _root.Enabled = Enabled;

        ApplyColors();
        return _root;
    }

    private void ApplyColors()
    {
        bool hovered = _root.Hovered;
        bool pressed = _root.Pressed;

        // A tinted chip is a solid colour when selected (`%opaque_button`); a plain one latches to
        // the neutral checked step, as every Adwaita toggle does — accent is not a selection colour
        // for buttons.
        var bgSel = Color is { } tint
            ? AdwStyle.Solid(baseColor: tint, hovered: hovered, pressed: pressed)
            : AdwStyle.ButtonFill(
                theme: _theme,
                style: AdwButtonStyle.Regular,
                hovered: hovered,
                pressed: pressed,
                @checked: true
            );
        var bgUn = AdwStyle.ButtonFill(
            theme: _theme,
            style: AdwButtonStyle.Regular,
            hovered: hovered,
            pressed: pressed
        );
        var fgUn = _theme.OnSurface;
        var fgSel = Color is null ? fgUn : Core.Color.White;

        float t = _sel.Value;
        var bg = Lerp(a: bgUn, b: bgSel, t: t);
        var fg = Lerp(a: fgUn, b: fgSel, t: t);
        // Insensitive widgets dim as a whole in Adwaita rather than swapping to a grey palette.
        float dim = Enabled ? 1f : AdwStyle.DisabledOpacity;
        _box.Fill = bg.WithAlpha(bg.A * dim);
        _box.BorderColor = Core.Color.Transparent;
        _text.Color = fg.WithAlpha(fg.A * dim);
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        return new Color(
            r: a.R + ((b.R - a.R) * t),
            g: a.G + ((b.G - a.G) * t),
            b: a.B + ((b.B - a.B) * t),
            a: a.A + ((b.A - a.A) * t)
        );
    }
}
