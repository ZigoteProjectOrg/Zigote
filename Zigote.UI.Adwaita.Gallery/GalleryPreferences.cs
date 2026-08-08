namespace AdwaitaGallery;

/// <summary>
///     The gallery's preferences dialog — and the app's real settings, not a mock-up: the rows here
///     drive <see cref="GalleryApp" />'s appearance signals, so every open window re-themes as they
///     move.
/// </summary>
internal static class GalleryPreferences
{
    public static void Show(GalleryApp app, Action<string> toast)
    {
        new AdwPreferencesDialog {
            Pages = {
                Appearance(app),
                General(app, toast),
            },
        }.Show();
    }

    private static Widget Appearance(GalleryApp app)
    {
        return new AdwPreferencesPage {
            Title = "Appearance",
            IconName = MaterialIcons.Palette,
            Groups = {
                new AdwPreferencesGroup(
                    "Style",
                    "Adwaita ships a light and a dark appearance. Following the system tracks the GNOME preference and its accent live."
                ) {
                    Rows = {
                        new Watch(() => new AdwSwitchRow(
                                "Follow System Style",
                                "Track the desktop appearance and accent",
                                app.FollowSystem.Value,
                                v => app.FollowSystem.Value = v
                            )
                        ),
                        new AdwActionRow("Style", "Overrides the system preference") {
                            Suffixes = {
                                new Watch(() => new AdwToggleGroup(
                                        ["Light", "Dark"],
                                        app.Dark.Value ? 1 : 0,
                                        index =>
                                        {
                                            app.FollowSystem.Value = false;
                                            app.Dark.Value = index == 1;
                                        }
                                    )
                                ),
                            },
                        },
                    },
                },
                new AdwPreferencesGroup(
                    "Accent Color",
                    "The nine GNOME 47 system accents. Each one is a whole ThemeData, rebuilt and pushed to every open window."
                ) {
                    Rows = {
                        new Padding(EdgeInsets.All(Spacing.Md), new AccentPicker(app)),
                        new Watch(() => new AdwActionRow("Selected") {
                                Suffixes = { Demo.Value(NameOf(app.Accent.Value)) },
                            }
                        ),
                    },
                },
                new AdwPreferencesGroup("Preview") {
                    Rows = {
                        new AdwActionRow("Suggested action") {
                            Suffixes = {
                                new AdwButton("Send") { Style = AdwButtonStyle.Suggested },
                            },
                        },
                        new AdwSwitchRow("A switch", value: true),
                        new AdwActionRow("A slider") {
                            Suffixes = { new SizedBox(160f, child: new AdwSlider(0.6f)) },
                        },
                    },
                },
            },
        };
    }

    private static Widget General(GalleryApp app, Action<string> toast)
    {
        return new AdwPreferencesPage {
            Title = "General",
            IconName = MaterialIcons.Settings,
            Groups = {
                new AdwPreferencesGroup("Windows") {
                    Rows = {
                        new AdwActionRow("New Window", "Each window navigates on its own") {
                            Suffixes = { new AdwButton("Open", app.NewWindow) },
                        },
                    },
                },
                new AdwPreferencesGroup("Feedback") {
                    Rows = {
                        new AdwActionRow("Toasts", "Float over the window that raised them") {
                            Suffixes = {
                                new AdwButton("Show", () => toast("Sent from Preferences")),
                            },
                        },
                    },
                },
                new AdwPreferencesGroup("Help") {
                    Rows = {
                        new AdwActionRow("Keyboard Shortcuts") {
                            ShowChevron = true,
                            OnActivated = GalleryShortcuts.Show,
                        },
                        new AdwActionRow("About Adwaita Demo") {
                            ShowChevron = true,
                            OnActivated = GalleryAbout.Show,
                        },
                    },
                },
            },
        };
    }

    private static string NameOf(AdwAccent accent)
    {
        foreach (var (value, name) in AccentPicker.Accents)
            if (value == accent)
                return name;
        return accent.ToString();
    }
}