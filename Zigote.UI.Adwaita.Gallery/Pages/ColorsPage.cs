namespace AdwaitaGallery.Pages;

/// <summary>
///     Colors — the libadwaita named colors of the appearance in force, and the nine system accents.
///     Change either and the whole gallery follows on the next frame: one ThemeData, one push.
/// </summary>
public sealed class ColorsPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var app = GalleryHost.Of(context).App;
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        return new GalleryPage(
            title: "Colors",
            description:
            "The named palette Adwaita is defined in, live in whichever appearance is running.",
            iconName: MaterialIcons.ColorLens
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Titled(
                    title: "System Accent",
                    description:
                    "GNOME 47 ships nine. Picking one rebuilds the theme for every open window.",
                    child: Demo.Specimen(
                        new AccentPicker(app) { Size = 36f },
                        new Watch(() => new AdwToggleGroup(
                                labels: ["Light", "Dark"],
                                active: app.Dark.Value ? 1 : 0,
                                onActive: i =>
                                {
                                    app.FollowSystem.Value = false;
                                    app.Dark.Value = i == 1;
                                }
                            )
                        ),
                        Demo.Caption("Or press Ctrl+D.")
                    )
                ),
                Swatches(
                    title: "Accent & Status",
                    ("accent_bg_color", p.AccentBg),
                    ("accent_color", p.Accent),
                    ("destructive_bg_color", p.DestructiveBg),
                    ("success_bg_color", p.SuccessBg),
                    ("warning_bg_color", p.WarningBg)
                ),
                Swatches(
                    title: "Surfaces",
                    ("window_bg_color", p.WindowBg),
                    ("view_bg_color", p.ViewBg),
                    ("headerbar_bg_color", p.HeaderbarBg),
                    ("sidebar_bg_color", p.SidebarBg),
                    ("card_bg_color", p.CardBg),
                    ("dialog_bg_color", p.DialogBg),
                    ("popover_bg_color", p.PopoverBg)
                ),
                Demo.Group(
                    title: "How It Reaches a Widget",
                    description: null,
                    new AdwActionRow(
                        title: "AdwPalette",
                        subtitle: "The named colors, both appearances, as values"
                    ),
                    new AdwActionRow(
                        title: "AdwTheme.Create",
                        subtitle: "Maps them onto one ThemeData"
                    ),
                    new AdwActionRow(
                        title: "ThemeProvider",
                        subtitle: "An inherited widget every build reads from"
                    )
                ),
            },
        };
    }

    private static Widget Swatches(string title, params (string Name, Color Value)[] colors)
    {
        var group = new AdwPreferencesGroup(title);
        foreach ((string name, var value) in colors)
        {
            group.Rows.Add(
                new AdwActionRow(title: name, subtitle: Hex(value)) {
                    Prefix = new Swatch(value),
                }
            );
        }

        return group;
    }

    private static string Hex(Color c) =>
        $"#{(int)(c.R * 255):X2}{(int)(c.G * 255):X2}{(int)(c.B * 255):X2}";
}

/// <summary>A rounded colour chip with a hairline, so a white swatch is still visible on white.</summary>
internal sealed class Swatch(Color color) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new DecoratedBox {
            Fill = color,
            Radius = Radii.Md,
            BorderColor = theme.Separator,
            BorderWidth = 1f,
            Child = SizedBox.Square(size: 28f, child: null),
        };
    }
}
