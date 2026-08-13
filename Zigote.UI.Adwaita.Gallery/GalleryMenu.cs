namespace AdwaitaGallery;

/// <summary>
///     The primary menu: the appearance switch first (a style change is one click, the way GNOME
///     apps that offer one do it), then the window and app actions.
/// </summary>
internal static class GalleryMenu
{
    public static Widget Build(GalleryApp app, Shell shell)
    {
        bool follow = app.FollowSystem.Value;
        bool dark = app.Dark.Value;

        var button = new AdwMenuButton {
            MenuWidth = 240f,
            Sections = [
                [
                    AdwMenuItem.Header("Appearance"),
                    AdwMenuItem.Radio(
                        label: "Follow System",
                        selected: follow,
                        onActivated: () => app.FollowSystem.Value = true
                    ),
                    AdwMenuItem.Radio(
                        label: "Light",
                        selected: !follow && !dark,
                        onActivated: () => SetStyle(app: app, dark: false)
                    ),
                    AdwMenuItem.Radio(
                        label: "Dark",
                        selected: !follow && dark,
                        onActivated: () => SetStyle(app: app, dark: true)
                    ),
                ],
                [
                    new AdwMenuItem(label: "New Window", onActivated: app.NewWindow) {
                        Accel = "Ctrl+N",
                    },
                ],
                [
                    new AdwMenuItem(label: "Preferences", onActivated: shell.ShowPreferences) {
                        Accel = "Ctrl+,",
                    },
                    new AdwMenuItem(
                        label: "Keyboard Shortcuts",
                        onActivated: GalleryShortcuts.Show
                    ) {
                        Accel = "Ctrl+?",
                    },
                    new AdwMenuItem(label: "About Adwaita Demo", onActivated: GalleryAbout.Show),
                ],
            ],
        };
        return new Tooltip(message: "Main Menu", child: button);
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
                    Label: "Report an issue",
                    Subtitle: null,
                    OnActivated: () => { },
                    IconName: MaterialIcons.BugReport
                ),
                new AdwAboutLink(
                    Label: "Open-source licenses",
                    Subtitle: null,
                    OnActivated: ShowLicenses,
                    IconName: MaterialIcons.Gavel
                ),
            },
        }.Show();
    }

    /// <summary>
    ///     Everything <see cref="Zigote.Core.Licenses.LicenseRegistry" /> knows about — the
    ///     engine's own bundled components, plus anything the app registered.
    /// </summary>
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
        foreach ((string title, var rows) in Groups)
        {
            var group = new AdwPreferencesGroup(title);
            foreach ((string action, string chord) in rows)
                group.Rows.Add(new AdwActionRow(action) { Suffixes = { Demo.Value(chord) } });
            column.Children.Add(group);
        }

        Demo.ShowDialog(
            title: "Keyboard Shortcuts",
            content: new SingleChildScrollView {
                Child = new Padding(padding: EdgeInsets.All(Spacing.Lg), child: column),
            },
            width: 460f,
            height: 520f
        );
    }
}
