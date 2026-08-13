namespace AdwaitaGallery.Pages;

/// <summary>
///     The landing page: what the gallery is, where to start, and the app-level actions. Every row
///     here navigates through <see cref="GalleryHost" />, so the sidebar follows along.
/// </summary>
public sealed class WelcomePage : ComposedWidget
{
    private static readonly (string Page, string Icon, string Blurb)[] StartHere = [
        ("Boxed Lists", MaterialIcons.ViewList, "The row types every GNOME app is built from"),
        ("Style Classes", MaterialIcons.Palette,
            "Suggested, destructive, flat, pill — on real widgets"),
        ("Adaptive", MaterialIcons.Devices, "One layout, from a phone width to a desktop one"),
        ("Reactivity", MaterialIcons.Bolt,
            "Signals driving the tree, with nothing rebuilt by hand"),
    ];

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        var start = new AdwPreferencesGroup(
            "Start Here",
            "A few pages that show what the toolkit is made of."
        );
        foreach (var (page, icon, blurb) in StartHere)
            start.Rows.Add(
                new AdwActionRow(page, blurb) {
                    IconName = icon,
                    ShowChevron = true,
                    OnActivated = () => host.Open(page),
                }
            );

        return new GalleryPage(
            "Adwaita on Zigote UI",
            "The GNOME widget set, laid out and animated by Zigote's own retained widget tree.",
            MaterialIcons.AutoAwesome
        ) {
            Children = {
                Demo.Bar(
                    new AdwButton("Preferences", host.ShowPreferences) {
                        Style = AdwButtonStyle.Suggested,
                        Pill = true,
                    },
                    new AdwButton("Shortcuts", GalleryShortcuts.Show) { Pill = true },
                    new AdwButton("New Window", host.App.NewWindow) { Pill = true }
                ),
                start,
                new AdwPreferencesGroup(
                    "In This Build",
                    "Everything in the sidebar is live — no screenshots, no mock rows."
                ) {
                    Rows = {
                        new AdwActionRow(
                            $"{GalleryRegistry.Entries.Length} pages",
                            "One per widget family, each with its controls wired up"
                        ) { IconName = MaterialIcons.Widgets },
                        new AdwActionRow(
                            "9 system accents",
                            "Switch appearance or accent and every open window follows"
                        ) { IconName = MaterialIcons.ColorLens },
                        new AdwActionRow(
                            "One adaptive shell",
                            "The panes fold into a single page below 620 px"
                        ) { IconName = MaterialIcons.Devices },
                    },
                },
            },
        };
    }
}
