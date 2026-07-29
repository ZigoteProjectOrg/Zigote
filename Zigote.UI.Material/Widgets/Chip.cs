using Zigote.Core.Animation;
using LayoutPadding = Zigote.UI.Widgets.Layout.Padding;

namespace Zigote.UI.Material;

/// <summary>
///     A flat, macOS-style capsule chip used as a selectable tag or filter. Unselected chips use a
///     translucent neutral fill with a hairline border; selected chips use the accent colour. Composed
///     from <see cref="Pressable" /> over a capsule <see cref="DecoratedBox" /> + centred
///     <see cref="Label" />. Sizing, colour and shape come from the theme tokens.
/// </summary>
public sealed class Chip : StatefulWidget
{
    private Color? _color;
    private bool _enabled = true;
    private string _label;
    private bool _selected;

    public Chip(string label, bool selected = false, Action? onPressed = null)
    {
        _label = label;
        _selected = selected;
        OnPressed = onPressed;
    }

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            MarkNeedsBuild();
        }
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            MarkNeedsBuild();
        }
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
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            MarkNeedsBuild();
        }
    }

    protected override WidgetState CreateState()
    {
        return new ChipState();
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
}

internal sealed class ChipState : SingleTickerProviderState<Chip>
{
    private readonly DecoratedBox _box = new();
    private readonly Label _label = new("") { MaxLines = 1 };
    private readonly LayoutPadding _padding = new(EdgeInsets.Zero);
    private readonly ConstrainedBox _minHeight = new(new Constraints(minHeight: ControlMetrics.CompactHeight));
    private AnimationController _sel = null!;
    private bool _selTarget;
    private Pressable _root = null!;
    private ThemeData _theme = ThemeData.Dark;

    public override void InitState()
    {
        _padding.Child = _label;
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
            OnPressed = () => Widget.OnPressed?.Invoke(),
        };

        // Crossfades the fill/border/text between the neutral and accent states on selection.
        _sel = new AnimationController(Motion.Fast, this) { Curve = Curves.EaseOut };
        _sel.OnTick += () =>
        {
            ApplyColors();
            _root.MarkNeedsPaint();
        };
        _selTarget = Widget.Selected;
        if (_selTarget) _sel.Complete();
        else _sel.Dismiss();
    }

    public override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);
        var w = Widget;

        if (w.Selected != _selTarget)
        {
            _selTarget = w.Selected;
            if (_selTarget) _sel.Forward();
            else _sel.Reverse();
        }

        _label.Text = w.Label;
        _label.FontSize = _theme.FontSizeCaption;
        _label.FontWeight = w.Selected ? FontWeight.Medium : FontWeight.Normal;
        // Filter/choice chips are toggles: 22pt tall is unusable with a finger. Grow the capsule
        // itself (a hit-rect trick would overlap neighbours in a tightly-spaced Wrap).
        var compact = TouchMetrics.IsCompact;
        _minHeight.Constraints = new Constraints(
            minHeight: compact ? 36f : ControlMetrics.CompactHeight
        );
        _padding.Insets = EdgeInsets.Symmetric(compact ? Spacing.Lg : Spacing.Md, Spacing.Xxs);
        _root.Enabled = w.Enabled;

        ApplyColors();
        return _root;
    }

    private void ApplyColors()
    {
        var w = Widget;
        var hovered = _root.Hovered;
        var pressed = _root.Pressed;

        // Selected style (accent).
        var bgSel = StateStyle.Fill(w.Color ?? _theme.Primary, hovered, pressed);
        var fgSel = _theme.OnPrimary;
        var borderSel = Color.Transparent;

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

        if (!w.Enabled)
        {
            bg = StateStyle.Disabled(bg);
            fg = StateStyle.Disabled(fg);
        }

        _box.Fill = bg;
        _box.BorderColor = border;
        _label.Color = fg;
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