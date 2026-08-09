using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Layout;
using LayoutPadding = Zigote.UI.Widgets.Layout.Padding;

namespace Zigote.UI.Widgets.Controls;

public enum ButtonStyle
{
    /// <summary>Prominent, accent-filled (the default action).</summary>
    Elevated,

    /// <summary>Subtle neutral fill with a hairline border.</summary>
    Outlined,

    /// <summary>Borderless — fills only on hover/press.</summary>
    Flat,
}

/// <summary>
///     A flat, macOS-style push button. Accent-filled by default; <see cref="Style" /> selects the
///     bordered or borderless variants. Composed from <see cref="Pressable" /> (interaction + focus)
///     over
///     a <see cref="DecoratedBox" /> (background) + a centred <see cref="Label" /> — no hand-written
///     layout or paint. Sizing, colour and shape come from the theme tokens.
/// </summary>
public class Button : ComposedWidget
{
    private readonly DecoratedBox _box = new();
    private readonly Label _text = new("") { MaxLines = 1 };
    private readonly LayoutPadding _padding = new(EdgeInsets.Zero);
    private readonly Pressable _root;
    private ThemeData _theme = ThemeData.Dark;

    private bool _enabled = true;
    private string _label;
    private ButtonStyle _style = ButtonStyle.Elevated;

    public Button(string label, Action? onPressed)
    {
        _label = label;
        OnPressed = onPressed;

        _padding.Child = _text;
        _box.Child = new ConstrainedBox(
            new Constraints(minHeight: ControlMetrics.RegularHeight),
            new Align(Alignment.Center, _padding) {
                WidthFactor = 1f,
                HeightFactor = 1f,
            }
        );
        _root = new Pressable {
            Child = _box,
            OnStateChanged = ApplyColors,
            OnPressed = () => OnPressed?.Invoke(),
        };
    }

    public string Label
    {
        get => _label;
        set => SetBuild(ref _label, value);
    }

    // Read live by the Pressable on each press, so changing it needs no rebuild. Enablement is
    // derived per Build, though: a button with no callback renders (and hit-tests) as disabled.
    public Action? OnPressed { get; set; }

    /// <summary>
    ///     Optional widget child shown instead of the string <see cref="Label" /> — the
    ///     <c>child: …</c> form (e.g. an icon + text <see cref="Row" />). When null the
    ///     button renders its <see cref="Label" />. Set at construction by the alias buttons.
    /// </summary>
    public Widget? Content { get; set; }

    public ButtonStyle Style
    {
        get => _style;
        set => SetBuild(ref _style, value);
    }

    public Color? BackgroundColor { get; set; }
    public Color? TextColor { get; set; }
    public Color? BorderColor { get; set; }
    public float? FontSize { get; set; }
    public EdgeInsets? Padding { get; set; }
    public float? Radius { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set => SetBuild(ref _enabled, value);
    }

    /// <summary>Opt-in Liquid Glass treatment for buttons that live on translucent chrome.</summary>
    public bool UseGlass { get; set; }

    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget is Button b)
        {
            Label = b.Label;
            OnPressed = b.OnPressed;
            Style = b.Style;
            Enabled = b.Enabled;
        }
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(
            Label,
            Enabled,
            Style,
            base.DebugStateHash()
        );
    }

    protected override Widget Build(BuildContext context)
    {
        _theme = Theme.Of(context);

        // A widget child (`child:`) replaces the string label when provided.
        _padding.Child = Content ?? _text;
        _text.Text = Label;
        _text.FontSize = FontSize ?? _theme.FontSizeBody;
        _text.FontWeight = Style == ButtonStyle.Elevated ? FontWeight.Medium : FontWeight.Normal;
        _padding.Insets = Padding ?? EdgeInsets.Symmetric(Spacing.Md, Spacing.Xs);

        var radius = Radius ?? _theme.ButtonRadius;
        _box.Radius = radius;
        _root.Enabled = Enabled && OnPressed is not null;
        _root.FocusRadius = radius;
        _root.SemanticsLabel = Label;

        ApplyColors();
        return _root;
    }

    private void ApplyColors()
    {
        var hovered = _root.Hovered;
        var pressed = _root.Pressed;
        var fg = TextColor ??
                 (Style == ButtonStyle.Elevated ? _theme.OnPrimary : _theme.OnSurface);

        if (!Enabled || OnPressed is null)
        {
            _box.Fill = _theme.Fill2;
            _box.BorderColor = Color.Transparent;
            // Disabled buttons use the neutral Fill2 background regardless of style, so the content must
            // be a neutral (on-surface) faded colour — a faded OnPrimary (white) vanishes on the light
            // disabled fill.
            var disabledFg = StateStyle.Disabled(TextColor ?? _theme.OnSurface);
            _text.Color = disabledFg;
            TintContent(disabledFg);
            return;
        }

        switch (Style)
        {
            case ButtonStyle.Elevated:
                _box.Fill = StateStyle.Fill(BackgroundColor ?? _theme.Primary, hovered, pressed);
                _box.BorderColor = Color.Transparent;
                break;
            case ButtonStyle.Outlined:
                _box.Fill = pressed ? _theme.Fill1 : hovered ? _theme.Fill2 : Color.Transparent;
                _box.BorderColor = BorderColor ?? _theme.Separator;
                break;
            default: // Flat
                _box.Fill = pressed ? _theme.Fill1 : hovered ? _theme.Fill2 : Color.Transparent;
                _box.BorderColor = Color.Transparent;
                break;
        }

        _text.Color = fg;
        TintContent(fg);
    }

    /// <summary>
    ///     Propagate the button's foreground colour into an uncoloured widget
    ///     <see cref="Button.Content" />
    ///     (the icon + text <see cref="Row" /> form). Without this a plain <see cref="Label" />/
    ///     <see cref="Icon" />
    ///     inside an accent-filled button paints in the default on-surface colour, which is unreadable on
    ///     the
    ///     blue fill. Only uncoloured children are tinted, so an explicitly-coloured child is left alone.
    /// </summary>
    private void TintContent(Color fg)
    {
        if (Content is { } content) ApplyForeground(content, fg);
    }

    private static void ApplyForeground(Widget w, Color fg)
    {
        switch (w)
        {
            case Label { Color: null } l:
                l.Color = fg;
                break;
            case Icon { Color: null } i:
                i.Color = fg;
                break;
        }

        foreach (var child in w.GetChildren()) ApplyForeground(child, fg);
    }
}