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
            "Preferences",
            "Pages of groups of rows — the shape every GNOME settings dialog shares.",
            MaterialIcons.Settings
        ) {
            Children = {
                Demo.Group(
                    "The Real Thing",
                    "The gallery's own preferences — the appearance rows in it drive this window.",
                    new AdwActionRow("Open Preferences", "Ctrl+,") {
                        IconName = MaterialIcons.Settings,
                        ShowChevron = true,
                        OnActivated = host.ShowPreferences,
                    },
                    new AdwActionRow("Open a Subpage", "A dialog raised over a dialog") {
                        IconName = MaterialIcons.OpenInNew,
                        ShowChevron = true,
                        OnActivated = ShowSubpage,
                    }
                ),
                Demo.Group(
                    "Anatomy",
                    "A page holds groups; a group holds rows and can carry a title, a description and a header suffix.",
                    new AdwActionRow("Page", "Scrolls, and clamps its content to 600 px"),
                    new AdwActionRow("Group", "A card of rows under a heading"),
                    new AdwActionRow("Row", "Action, switch, spin, combo, entry, expander…")
                ),
                WithSuffix(host),
                Demo.Group(
                    "Sample Settings",
                    null,
                    new AdwSwitchRow(
                        "Automatic Updates",
                        "Check daily on a metered connection",
                        true
                    ),
                    new AdwComboRow("Update Channel", ["Stable", "Beta", "Nightly"]),
                    new AdwSpinRow(
                        "Keep Backups",
                        "Days",
                        30,
                        1,
                        365
                    ),
                    new AdwExpanderRow("Advanced", "Rarely worth changing") {
                        Rows = {
                            new AdwSwitchRow("Verbose Logging"),
                            new AdwSwitchRow("Developer Tools", value: true),
                        },
                    }
                ),
            },
        };
    }

    private static Widget WithSuffix(GalleryHost host)
    {
        return new AdwPreferencesGroup("Header Suffix", "A group can carry an action of its own") {
            HeaderSuffix = new AdwButton("Add", () => host.Toast("Added")) { Compact = true },
            Rows = {
                new AdwActionRow("First entry"),
                new AdwActionRow("Second entry"),
            },
        };
    }

    private static void ShowSubpage()
    {
        Demo.ShowDialog(
            "Subpage",
            new AdwStatusPage {
                IconName = MaterialIcons.Layers,
                Title = "This Is a Subpage",
                Description = "Dialogs stack, and the topmost one takes Escape.",
            },
            420f,
            340f
        );
    }
}
