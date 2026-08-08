namespace AdwaitaGallery.Pages;

/// <summary>
///     Colors — the libadwaita named colors of the appearance in force, and the nine system accents.
///     Change either and the whole gallery follows on the next frame: one ThemeData, one push.
/// </summary>
public sealed class ColorsPage : StatelessWidget
{
    protected override Widget Build(BuildContext context)
    {
        var app = GalleryHost.Of(context).App;
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        return new GalleryPage(
            "Colors",
            "The named palette Adwaita is defined in, live in whichever appearance is running.",
            MaterialIcons.ColorLens
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Titled(
                    "System Accent",
                    "GNOME 47 ships nine. Picking one rebuilds the theme for every open window.",
                    Demo.Specimen(
                        new AccentPicker(app) { Size = 36f },
                        new Watch(() => new AdwToggleGroup(
                                ["Light", "Dark"],
                                app.Dark.Value ? 1 : 0,
                                i =>
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
                    "Accent & Status",
                    ("accent_bg_color", p.AccentBg),
                    ("accent_color", p.Accent),
                    ("destructive_bg_color", p.DestructiveBg),
                    ("success_bg_color", p.SuccessBg),
                    ("warning_bg_color", p.WarningBg)
                ),
                Swatches(
                    "Surfaces",
                    ("window_bg_color", p.WindowBg),
                    ("view_bg_color", p.ViewBg),
                    ("headerbar_bg_color", p.HeaderbarBg),
                    ("sidebar_bg_color", p.SidebarBg),
                    ("card_bg_color", p.CardBg),
                    ("dialog_bg_color", p.DialogBg),
                    ("popover_bg_color", p.PopoverBg)
                ),
                Demo.Group(
                    "How It Reaches a Widget",
                    null,
                    new AdwActionRow("AdwPalette", "The named colors, both appearances, as values"),
                    new AdwActionRow("AdwTheme.Create", "Maps them onto one ThemeData"),
                    new AdwActionRow("ThemeProvider", "An inherited widget every build reads from")
                ),
            },
        };
    }

    private static Widget Swatches(string title, params (string Name, Color Value)[] colors)
    {
        var group = new AdwPreferencesGroup(title);
        foreach (var (name, value) in colors)
            group.Rows.Add(
                new AdwActionRow(name, Hex(value)) {
                    Prefix = new Swatch(value),
                }
            );
        return group;
    }

    private static string Hex(Color c)
    {
        return $"#{(int)(c.R * 255):X2}{(int)(c.G * 255):X2}{(int)(c.B * 255):X2}";
    }
}

/// <summary>A rounded colour chip with a hairline, so a white swatch is still visible on white.</summary>
internal sealed class Swatch(Color color) : StatelessWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new DecoratedBox {
            Fill = color,
            Radius = Radii.Md,
            BorderColor = theme.Separator,
            BorderWidth = 1f,
            Child = SizedBox.Square(28f, null),
        };
    }
}