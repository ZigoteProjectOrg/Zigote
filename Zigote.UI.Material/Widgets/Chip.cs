using Zigote.Core.Animation;
using LayoutPadding = Zigote.UI.Widgets.Layout.Padding;

namespace Zigote.UI.Material;

/// <summary>
///     A flat, macOS-style capsule chip used as a selectable tag or filter. Unselected chips use a
///     translucent neutral fill with a hairline border; selected chips use the accent colour. Composed
///     from <see cref="Pressable" /> over a capsule <see cref="DecoratedBox" /> + centred
///     <see cref="Label" />. Sizing, colour and shape come from the theme tokens.
/// </summary>
public sealed class Chip : ComposedWidget
{
    private readonly DecoratedBox _box = new();
    private readonly Label _text = new("") { MaxLines = 1 };
    private readonly LayoutPadding _padding = new(EdgeInsets.Zero);

    private readonly ConstrainedBox _minHeight =
        new(new Constraints(minHeight: ControlMetrics.CompactHeight));

    private readonly Pressable _root;
    private AnimationController _sel = null!;
    private bool _selTarget;
    private ThemeData _theme = ThemeData.Dark;

    private Color? _color;
    private bool _enabled = true;
    private string _label;
    private bool _selected;

    public Chip(string label, bool selected = false, Action? onPressed = null)
    {
        _label = label;
        _selected = selected;
        OnPressed = onPressed;

        _padding.Child = _text;
        _box.Radius = Radii.Capsule;
        _minHeight.Child = new Align(Alignment.Center, _padding) {
            WidthFactor = 1f,
            HeightFactor = 1f,
        };
        _box.Child = _minHeight;
        _root = new Pressable {
            Child = _box,
            FocusRadius = Radii.Capsule,
            OnStateChanged = ApplyColors,
            OnPressed = () => OnPressed?.Invoke(),
        };
    }

    // The controller owns a Ticker, whose lifetime is the mount period — so it is built here rather
    // than in the constructor, and rebuilt if this chip is re-attached.
    protected override void OnMount()
    {
        // Crossfades the fill/border/text between the neutral and accent states on selection.
        _sel = new AnimationController(Motion.Fast, this) { Curve = Curves.EaseOut };
        _sel.OnTick += () =>
        {
            ApplyColors();
            _root.MarkNeedsPaint();
        };
        _selTarget = Selected;
        if (_selTarget) _sel.Complete();
        else _sel.Dismiss();
    }

    public string Label
    {
        get => _label;
        set => SetBuild(ref _label, value);
    }

    public bool Selected
    {
        get => _selected;
        set => SetBuild(ref _selected, value);
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
        set => SetBuild(ref _enabled, value);
    }

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is Chip c)
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
            Label,
            Selected,
            Enabled,
            base.DebugStateHash()
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
        _text.FontSize = _theme.FontSizeCaption;
        _text.FontWeight = Selected ? FontWeight.Medium : FontWeight.Normal;
        // Filter/choice chips are toggles: 22pt tall is unusable with a finger. Grow the capsule
        // itself (a hit-rect trick would overlap neighbours in a tightly-spaced Wrap).
        var compact = TouchMetrics.IsCompact;
        _minHeight.Constraints = new Constraints(
            minHeight: compact ? 36f : ControlMetrics.CompactHeight
        );
        _padding.Insets = EdgeInsets.Symmetric(compact ? Spacing.Lg : Spacing.Md, Spacing.Xxs);
        _root.Enabled = Enabled;

        ApplyColors();
        return _root;
    }

    private void ApplyColors()
    {
        var hovered = _root.Hovered;
        var pressed = _root.Pressed;

        // Selected style (accent).
        var bgSel = StateStyle.Fill(Color ?? _theme.Primary, hovered, pressed);
        var fgSel = _theme.OnPrimary;
        var borderSel = Zigote.Core.Color.Transparent;

        // Unselected style: a clearly visible neutral fill (translucent so it adapts to any backdrop)
        // plus a hairline border so the chip always reads as a shape — the Fill2 token alone is too faint.
        var a = pressed ? 0.16f : hovered ? 0.12f : 0.08f;
        var bgUn = _theme.OnSurface.WithAlpha(a);
        var fgUn = _theme.OnSurface;
        var borderUn = _theme.Separator;

        var t = _sel.Value;
        var bg = Lerp(bgUn, bgSel, t);
        var fg = Lerp(fgUn, fgSel, t);
        var border = Lerp(borderUn, borderSel, t);

        if (!Enabled)
        {
            bg = StateStyle.Disabled(bg);
            fg = StateStyle.Disabled(fg);
        }

        _box.Fill = bg;
        _box.BorderColor = border;
        _text.Color = fg;
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        return new Color(
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t,
            a.A + (b.A - a.A) * t
        );
    }
}