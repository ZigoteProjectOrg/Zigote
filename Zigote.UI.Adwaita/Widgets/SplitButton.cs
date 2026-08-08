using AppInstance = Zigote.UI.Host.App;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwSplitButton — a main action section joined to a dropdown-arrow section by a 1px
///     separator. Clicking the main section fires <see cref="OnPressed" />; the arrow opens a menu
///     popover of <see cref="MenuItems" />.
/// </summary>
public sealed class AdwSplitButton : StatelessWidget
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
            Child = new SizedBox(
                24f,
                height,
                new Center(new IconGlyph(Icons.DropDown, AdwMetrics.IconSize, fg))
            ),
        };

        // On solid fills the theme separator hairline vanishes — use a darkened fill instead.
        var separator = Style is AdwButtonStyle.Suggested or AdwButtonStyle.Destructive
            ? AdwStyle.ButtonFill(theme, Style).Darken(0.25f)
            : theme.Separator;

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
                new Container {
                    Width = 1f,
                    Height = height,
                    Background = separator,
                },
                arrow,
            },
        };

        return Enabled ? group : new Opacity(AdwStyle.DisabledOpacity, group);
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