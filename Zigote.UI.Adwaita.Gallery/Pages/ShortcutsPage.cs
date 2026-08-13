namespace AdwaitaGallery.Pages;

/// <summary>
///     Shortcut label and the shortcuts dialog. The label is the interesting half: it parses a GTK
///     accelerator and draws key caps, in libadwaita 1.10's fixed modifier order — the accelerators
///     below are deliberately written in inconsistent orders to show they land the same way.
/// </summary>
public sealed class ShortcutsPage : ComposedWidget
{
    private static readonly (string Title, string Accel)[] General = [
        ("New Window", "<Primary>n"),
        ("Open", "<Primary>o"),
        ("Save", "<Primary>s"),
        ("Save As", "<Primary><Shift>s"),
        ("Quit", "<Primary>q"),
    ];

    private static readonly (string Title, string Accel)[] Editing = [
        ("Undo", "<Primary>z"),
        ("Redo", "<Shift><Primary>z"), // typed modifier-last on purpose
        ("Find", "<Primary>f"),
        ("Fullscreen", "F11"),
        ("Close Tab", "<Primary>w"),
    ];

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "Shortcuts",
            description: "Accelerators drawn as key caps, and the dialog that lists them.",
            iconName: MaterialIcons.Keyboard
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Group(
                    title: "Shortcut Label",
                    description:
                    "Modifiers always render Ctrl · Alt · Shift · Super, whatever order the " +
                    "accelerator string lists them in.",
                    Row(title: "Save", accel: "<Primary>s"),
                    Row(title: "Save As", accel: "<Primary><Shift>s"),
                    Row(title: "Same shortcut, typed backwards", accel: "<Shift><Primary>s"),
                    Row(title: "Named keys", accel: "<Primary>Return"),
                    Row(title: "Alternatives", accel: "<Primary>plus <Primary>equal"),
                    Row(title: "Unset", accel: "")
                ),
                Demo.Titled(
                    title: "Shortcuts Dialog",
                    description:
                    "The window every GNOME app opens on Ctrl+? — sections of boxed-list rows.",
                    child: Demo.Specimen(
                        new AdwButton(label: "Show Shortcuts", onPressed: ShowDialog) {
                            Pill = true,
                        },
                        Demo.Caption("Escape closes it, like any Adwaita dialog.")
                    )
                ),
            },
        };
    }

    private static Widget Row(string title, string accel)
    {
        return new AdwActionRow(
            title: title,
            subtitle: accel.Length > 0 ? accel : "(no accelerator)"
        ) {
            Suffixes = { new AdwShortcutLabel(accel) },
        };
    }

    private static void ShowDialog()
    {
        var dialog = new AdwShortcutsDialog();
        dialog.Add(
            new AdwShortcutsSection(
                title: "General",
                items: [
                    .. General.Select(s => new AdwShortcutsItem(
                            title: s.Title,
                            accelerator: s.Accel
                        )
                    ),
                ]
            )
        );
        dialog.Add(
            new AdwShortcutsSection(
                title: "Editing",
                items: [
                    .. Editing.Select(s => new AdwShortcutsItem(
                            title: s.Title,
                            accelerator: s.Accel
                        )
                    ),
                ]
            )
        );
        dialog.Show();
    }
}
