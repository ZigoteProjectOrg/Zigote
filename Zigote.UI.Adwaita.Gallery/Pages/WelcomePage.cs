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
            title: "Start Here",
            description: "A few pages that show what the toolkit is made of."
        );
        foreach ((string page, string icon, string blurb) in StartHere)
        {
            start.Rows.Add(
                new AdwActionRow(title: page, subtitle: blurb) {
                    IconName = icon,
                    ShowChevron = true,
                    OnActivated = () => host.Open(page),
                }
            );
        }

        return new GalleryPage(
            title: "Adwaita on Zigote UI",
            description:
            "The GNOME widget set, laid out and animated by Zigote's own retained widget tree.",
            iconName: MaterialIcons.AutoAwesome
        ) {
            Children = {
                Demo.Bar(
                    new AdwButton(label: "Preferences", onPressed: host.ShowPreferences) {
                        Style = AdwButtonStyle.Suggested,
                        Pill = true,
                    },
                    new AdwButton(label: "Shortcuts", onPressed: GalleryShortcuts.Show) {
                        Pill = true,
                    },
                    new AdwButton(label: "New Window", onPressed: host.App.NewWindow) {
                        Pill = true,
                    }
                ),
                start,
                new AdwPreferencesGroup(
                    title: "In This Build",
                    description: "Everything in the sidebar is live — no screenshots, no mock rows."
                ) {
                    Rows = {
                        new AdwActionRow(
                            title: $"{GalleryRegistry.Entries.Length} pages",
                            subtitle: "One per widget family, each with its controls wired up"
                        ) { IconName = MaterialIcons.Widgets },
                        new AdwActionRow(
                            title: "9 system accents",
                            subtitle: "Switch appearance or accent and every open window follows"
                        ) { IconName = MaterialIcons.ColorLens },
                        new AdwActionRow(
                            title: "One adaptive shell",
                            subtitle: "The panes fold into a single page below 620 px"
                        ) { IconName = MaterialIcons.Devices },
                    },
                },
            },
        };
    }
}
