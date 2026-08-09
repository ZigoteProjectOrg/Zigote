namespace AdwaitaGallery.Pages;

/// <summary>
///     Alert Dialogs — the adaptive alert with its response appearances, and the plain dialog it is
///     built on.
/// </summary>
public sealed class AlertsPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        return new GalleryPage(
            "Alert Dialogs",
            "A decision that has to be made now — heading, body, and two or three responses.",
            MaterialIcons.WebAsset
        ) {
            Children = {
                Demo.Group(
                    "Alerts",
                    "The response you want people to take is suggested; the one that loses work is destructive.",
                    new AdwActionRow("Unsaved Changes", "Cancel · Discard · Save") {
                        Suffixes = { new AdwButton("Show", () => ShowSave(host)) },
                    },
                    new AdwActionRow("Destructive", "One way out, and it is the loud one") {
                        Suffixes = { new AdwButton("Show", () => ShowDelete(host)) },
                    },
                    new AdwActionRow("Plain", "Body text and a single acknowledgement") {
                        Suffixes = { new AdwButton("Show", () => ShowPlain(host)) },
                    },
                    new AdwActionRow(
                        "With an Extra Child",
                        "A check button between the body and the responses"
                    ) {
                        Suffixes = { new AdwButton("Show", () => ShowExtraChild(host)) },
                    }
                ),
                Demo.Group(
                    "Dialogs",
                    "The same presenter without the alert layout — anything can be the content.",
                    new AdwActionRow("Custom Dialog", "A toolbar view inside a dialog") {
                        Suffixes = { new AdwButton("Show", ShowCustom) },
                    }
                ),
                Demo.Caption("Escape or a click on the scrim closes any of them."),
            },
        };
    }

    private static void ShowSave(GalleryHost host)
    {
        var dialog = new AdwAlertDialog(
            "Save Changes?",
            "“Untitled Document” contains unsaved changes. Changes which are not saved will be permanently lost."
        ) {
            OnResponse = id => host.Toast($"Response: {id}"),
            CloseResponse = "cancel",
            // Enter saves. Without this the first response added takes the focus, so the keyboard
            // default would be Cancel — GNOME points it at the suggested response.
            DefaultResponse = "save",
        };
        dialog.AddResponse("cancel", "Cancel");
        dialog.AddResponse("discard", "Discard", AdwResponseAppearance.Destructive);
        dialog.AddResponse("save", "Save", AdwResponseAppearance.Suggested);
        dialog.Show();
    }

    private static void ShowDelete(GalleryHost host)
    {
        var dialog = new AdwAlertDialog(
            "Delete Project?",
            "This removes the project and everything in it. There is no undo."
        ) {
            OnResponse = id => host.Toast($"Response: {id}"),
            CloseResponse = "cancel",
            // A destructive alert defaults to the safe response: Enter must not delete.
            DefaultResponse = "cancel",
        };
        dialog.AddResponse("cancel", "Cancel");
        dialog.AddResponse("delete", "Delete", AdwResponseAppearance.Destructive);
        dialog.Show();
    }

    /// <summary>
    ///     The extra child: libadwaita packs it between the body and the responses, which is where
    ///     GNOME's "Don't ask again" lives. The check button is read when a response comes back —
    ///     the alert does not carry the state for you.
    /// </summary>
    private static void ShowExtraChild(GalleryHost host)
    {
        var dontAsk = new AdwCheckButton("Don't ask again");
        var dialog = new AdwAlertDialog(
            "Empty Trash?",
            "All items in the trash will be permanently deleted."
        ) {
            ExtraChild = new Align(Alignment.Center, dontAsk) { HeightFactor = 1f },
            OnResponse = id => host.Toast(
                dontAsk.Value ? $"Response: {id} · won't ask again" : $"Response: {id}"
            ),
            CloseResponse = "cancel",
            DefaultResponse = "cancel",
        };
        dialog.AddResponse("cancel", "Cancel");
        dialog.AddResponse("empty", "Empty Trash", AdwResponseAppearance.Destructive);
        dialog.Show();
    }

    private static void ShowPlain(GalleryHost host)
    {
        var dialog = new AdwAlertDialog(
            "Update Installed",
            "Adwaita Demo will use the new version the next time it starts."
        ) {
            OnResponse = _ => host.Toast("Acknowledged"),
            CloseResponse = "ok",
        };
        dialog.AddResponse("ok", "OK", AdwResponseAppearance.Suggested);
        dialog.Show();
    }

    private static void ShowCustom()
    {
        Demo.ShowDialog(
            "Custom Dialog",
            new Padding(
                EdgeInsets.All(Spacing.Lg),
                new AdwPreferencesGroup("Anything Goes") {
                    Rows = {
                        new AdwEntryRow("Name", "Untitled"),
                        new AdwComboRow("Format", ["PNG", "SVG", "PDF"]),
                        new AdwSwitchRow("Open when finished", value: true),
                    },
                }
            ),
            420f,
            320f
        );
    }
}