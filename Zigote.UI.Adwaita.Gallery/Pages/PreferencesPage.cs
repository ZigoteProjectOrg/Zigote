namespace AdwaitaGallery.Pages;

/// <summary>
///     Preferences — the dialog pattern itself: an inline page showing how the pieces stack, and the
///     gallery's own (working) preferences dialog one click away.
/// </summary>
public sealed class PreferencesPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        return new GalleryPage(
            title: "Preferences",
            description: "Pages of groups of rows — the shape every GNOME settings dialog shares.",
            iconName: MaterialIcons.Settings
        ) {
            Children = {
                Demo.Group(
                    title: "The Real Thing",
                    description:
                    "The gallery's own preferences — the appearance rows in it drive this window.",
                    new AdwActionRow(title: "Open Preferences", subtitle: "Ctrl+,") {
                        IconName = MaterialIcons.Settings,
                        ShowChevron = true,
                        OnActivated = host.ShowPreferences,
                    },
                    new AdwActionRow(
                        title: "Open a Subpage",
                        subtitle: "A dialog raised over a dialog"
                    ) {
                        IconName = MaterialIcons.OpenInNew,
                        ShowChevron = true,
                        OnActivated = ShowSubpage,
                    }
                ),
                Demo.Group(
                    title: "Anatomy",
                    description:
                    "A page holds groups; a group holds rows and can carry a title, a description and a header suffix.",
                    new AdwActionRow(
                        title: "Page",
                        subtitle: "Scrolls, and clamps its content to 600 px"
                    ),
                    new AdwActionRow(title: "Group", subtitle: "A card of rows under a heading"),
                    new AdwActionRow(
                        title: "Row",
                        subtitle: "Action, switch, spin, combo, entry, expander…"
                    )
                ),
                WithSuffix(host),
                Demo.Group(
                    title: "Sample Settings",
                    description: null,
                    new AdwSwitchRow(
                        title: "Automatic Updates",
                        subtitle: "Check daily on a metered connection",
                        value: true
                    ),
                    new AdwComboRow(title: "Update Channel", items: ["Stable", "Beta", "Nightly"]),
                    new AdwSpinRow(
                        title: "Keep Backups",
                        subtitle: "Days",
                        value: 30,
                        min: 1,
                        max: 365
                    ),
                    new AdwExpanderRow(title: "Advanced", subtitle: "Rarely worth changing") {
                        Rows = {
                            new AdwSwitchRow("Verbose Logging"),
                            new AdwSwitchRow(title: "Developer Tools", value: true),
                        },
                    }
                ),
            },
        };
    }

    private static Widget WithSuffix(GalleryHost host)
    {
        return new AdwPreferencesGroup(
            title: "Header Suffix",
            description: "A group can carry an action of its own"
        ) {
            HeaderSuffix =
                new AdwButton(
                    label: "Add",
                    onPressed: () => host.Toast("Added")
                ) { Compact = true },
            Rows = {
                new AdwActionRow("First entry"),
                new AdwActionRow("Second entry"),
            },
        };
    }

    private static void ShowSubpage()
    {
        Demo.ShowDialog(
            title: "Subpage",
            content: new AdwStatusPage {
                IconName = MaterialIcons.Layers,
                Title = "This Is a Subpage",
                Description = "Dialogs stack, and the topmost one takes Escape.",
            },
            width: 420f,
            height: 340f
        );
    }
}
