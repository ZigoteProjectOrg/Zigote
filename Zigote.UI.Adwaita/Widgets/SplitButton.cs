using AppInstance = Zigote.UI.Host.App;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwSplitButton — a main action section joined to a dropdown-arrow section by a 1px
///     separator. Clicking the main section fires <see cref="OnPressed" />; the arrow opens a menu
///     popover of <see cref="MenuItems" />.
/// </summary>
public sealed class AdwSplitButton : ComposedWidget
{
    private bool _enabled = true;
    private string? _iconName;
    private string _label;
    private AdwButtonStyle _style = AdwButtonStyle.Regular;

    public AdwSplitButton(string label = "", Action? onPressed = null)
    {
        _label = label;
        OnPressed = onPressed;
    }

    public string Label
    {
        get => _label;
        set => this.Set(ref _label, value);
    }

    /// <summary>Optional icon glyph (an <see cref="Icons" /> constant) drawn before the label.</summary>
    public string? IconName
    {
        get => _iconName;
        set => this.Set(ref _iconName, value);
    }

    public Action? OnPressed { get; set; }

    public AdwButtonStyle Style
    {
        get => _style;
        set => this.Set(ref _style, value);
    }

    // Plain setters: both are read when the arrow opens the popover, never during Build.
    public IReadOnlyList<string> MenuItems { get; set; } = [];
    public Action<int>? OnMenuSelected { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set => this.Set(ref _enabled, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var fg = AdwStyle.ButtonForeground(theme, Style);
        const float radius = AdwMetrics.ControlRadius;
        const float height = AdwMetrics.ButtonHeight;

        var mainBox = new DecoratedBox {
            Child = AdwStyle.ButtonBody(
                new AdwButtonContent(IconName, Label) { Color = fg },
                height
            ),
        };
        var arrowBox = new DecoratedBox {
            // splitbutton > menubutton > button: padding 4px either side of a 16px arrow.
            Child = new SizedBox(
                AdwMetrics.IconSize + 8f,
                height,
                new Center(new IconGlyph(Icons.DropDown, AdwMetrics.IconSize, fg))
            ),
        };

        // The stylesheet only draws this hairline where the two halves would otherwise be
        // indistinguishable: a raised split button has none (`> separator { background: none }`),
        // a flat or solid one gets currentColor at $dimmer_opacity — the foreground of whatever
        // fill it sits on, so white on an accent and near-black on a neutral.
        var separator = Style switch {
            AdwButtonStyle.Regular => Color.Transparent,
            _ => fg.WithAlpha(AdwStyle.DimmerOpacity),
        };

        var main = new Pressable {
            Child = mainBox,
            Enabled = Enabled,
            FocusRadius = radius,
            OnPressed = () => OnPressed?.Invoke(),
            SemanticsLabel = Label,
        };
        main.WireFill(
            mainBox,
            theme,
            Style,
            () => Enabled
        );

        var group = new ClipRRect(radius);
        var arrow = new Pressable {
            Child = arrowBox,
            Enabled = Enabled,
            FocusRadius = radius,
            SemanticsLabel = "Open menu",
        };
        arrow.WireFill(
            arrowBox,
            theme,
            Style,
            () => Enabled
        );
        arrow.OnPressed = () => OpenMenu(group.Bounds);

        group.Child = new Row(mainAxisSize: MainAxisSize.Min) {
            Children = {
                main,
                // `> separator { margin-top: 6px; margin-bottom: 6px }` — the hairline is inset
                // from both ends rather than running the full height of the button.
                new Container {
                    Width = 1f,
                    Height = height - AdwMetrics.ToolbarPadding * 2f,
                    Background = separator,
                },
                arrow,
            },
        };

        // A flat control has no fill to lose, so it dims further than a raised one
        // ($strong_disabled_opacity).
        return Enabled
            ? group
            : new Opacity(
                Style is AdwButtonStyle.Flat
                    ? AdwStyle.StrongDisabledOpacity
                    : AdwStyle.DisabledOpacity,
                group
            );
    }

    private void OpenMenu(Rect anchor)
    {
        var app = AppInstance.Active;
        if (app is null || MenuItems.Count == 0) return;
        new AdwPopover(
            app,
            MenuItems,
            anchor,
            i => OnMenuSelected?.Invoke(i),
            minWidth: anchor.Width
        ).Show();
    }
}