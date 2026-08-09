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
            "Shortcuts",
            "Accelerators drawn as key caps, and the dialog that lists them.",
            MaterialIcons.Keyboard
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Group(
                    "Shortcut Label",
                    "Modifiers always render Ctrl · Alt · Shift · Super, whatever order the " +
                    "accelerator string lists them in.",
                    Row("Save", "<Primary>s"),
                    Row("Save As", "<Primary><Shift>s"),
                    Row("Same shortcut, typed backwards", "<Shift><Primary>s"),
                    Row("Named keys", "<Primary>Return"),
                    Row("Alternatives", "<Primary>plus <Primary>equal"),
                    Row("Unset", "")
                ),
                Demo.Titled(
                    "Shortcuts Dialog",
                    "The window every GNOME app opens on Ctrl+? — sections of boxed-list rows.",
                    Demo.Specimen(
                        new AdwButton("Show Shortcuts", ShowDialog) { Pill = true },
                        Demo.Caption("Escape closes it, like any Adwaita dialog.")
                    )
                ),
            },
        };
    }

    private static Widget Row(string title, string accel)
    {
        return new AdwActionRow(title, accel.Length > 0 ? accel : "(no accelerator)") {
            Suffixes = { new AdwShortcutLabel(accel) },
        };
    }

    private static void ShowDialog()
    {
        var dialog = new AdwShortcutsDialog();
        dialog.Add(
            new AdwShortcutsSection(
                "General",
                [.. General.Select(s => new AdwShortcutsItem(s.Title, s.Accel))]
            )
        );
        dialog.Add(
            new AdwShortcutsSection(
                "Editing",
                [.. Editing.Select(s => new AdwShortcutsItem(s.Title, s.Accel))]
            )
        );
        dialog.Show();
    }
}
