namespace AdwaitaGallery;

/// <summary>
///     The primary menu: the appearance switch first (a style change is one click, the way GNOME
///     apps that offer one do it), then the window and app actions.
/// </summary>
internal static class GalleryMenu
{
    public static Widget Build(GalleryApp app, Shell shell)
    {
        var follow = app.FollowSystem.Value;
        var dark = app.Dark.Value;

        var button = new AdwMenuButton {
            MenuWidth = 240f,
            Sections = [
                [
                    AdwMenuItem.Header("Appearance"),
                    AdwMenuItem.Radio("Follow System", follow, () => app.FollowSystem.Value = true),
                    AdwMenuItem.Radio("Light", !follow && !dark, () => SetStyle(app, false)),
                    AdwMenuItem.Radio("Dark", !follow && dark, () => SetStyle(app, true)),
                ],
                [
                    new AdwMenuItem("New Window", app.NewWindow) { Accel = "Ctrl+N" },
                ],
                [
                    new AdwMenuItem("Preferences", shell.ShowPreferences) { Accel = "Ctrl+," },
                    new AdwMenuItem("Keyboard Shortcuts", GalleryShortcuts.Show) {
                        Accel = "Ctrl+?",
                    },
                    new AdwMenuItem("About Adwaita Demo", GalleryAbout.Show),
                ],
            ],
        };
        return new Tooltip("Main Menu", button);
    }

    private static void SetStyle(GalleryApp app, bool dark)
    {
        app.FollowSystem.Value = false;
        app.Dark.Value = dark;
    }
}

/// <summary>The app's about dialog, filled in the way a shipped GNOME app fills its appdata.</summary>
internal static class GalleryAbout
{
    public static void Show()
    {
        new AdwAboutDialog {
            AppName = "Adwaita Demo",
            DeveloperName = "Zigote UI",
            IconName = MaterialIcons.AutoAwesome,
            Version = "1.0",
            Website = "https://gnome.pages.gitlab.gnome.org/libadwaita/",
            OnWebsite = () => { },
            Comments =
                "A tour of the Adwaita widget set as implemented on Zigote UI — the same GNOME " +
                "look, laid out and animated by the toolkit's own retained widget tree.",
            Copyright = "© 2024 The Zigote authors\nAdwaita design © The GNOME Project",
            License = "LGPL-2.1-or-later",
            Links = {
                new AdwAboutLink(
                    "Report an issue",
                    null,
                    () => { },
                    MaterialIcons.BugReport
                ),
                new AdwAboutLink(
                    "Open-source licenses",
                    null,
                    ShowLicenses,
                    MaterialIcons.Gavel
                ),
            },
        }.Show();
    }

    /// <summary>Everything <see cref="Zigote.Core.Licenses.LicenseRegistry" /> knows about — the
    ///     engine's own bundled components, plus anything the app registered.</summary>
    private static void ShowLicenses()
    {
        new AdwDialog {
            ContentWidth = 560f,
            ContentHeight = 620f,
            Child = new LicensesView { Title = "Open-source licenses" },
        }.Show();
    }
}

/// <summary>The shortcuts window: one boxed list per group, chords as monospace chips.</summary>
internal static class GalleryShortcuts
{
    private static readonly (string Group, (string Action, string Chord)[] Rows)[] Groups = [
        ("General", [
            ("Search pages", "Ctrl+F"),
            ("Preferences", "Ctrl+,"),
            ("Keyboard shortcuts", "Ctrl+?"),
            ("Toggle dark style", "Ctrl+D"),
        ]),
        ("Windows", [
            ("New window", "Ctrl+N"),
            ("Close window", "Ctrl+W"),
        ]),
        ("Navigation", [
            ("Move focus", "Tab"),
            ("Move focus backwards", "Shift+Tab"),
            ("Dismiss dialog or popover", "Esc"),
        ]),
    ];

    public static void Show()
    {
        var column = new Column(
            spacing: Spacing.Xl,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        );
        foreach (var (title, rows) in Groups)
        {
            var group = new AdwPreferencesGroup(title);
            foreach (var (action, chord) in rows)
                group.Rows.Add(new AdwActionRow(action) { Suffixes = { Demo.Value(chord) } });
            column.Children.Add(group);
        }

        Demo.ShowDialog(
            "Keyboard Shortcuts",
            new SingleChildScrollView {
                Child = new Padding(EdgeInsets.All(Spacing.Lg), column),
            },
            460f,
            520f
        );
    }
}
