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
            title: "Alert Dialogs",
            description:
            "A decision that has to be made now — heading, body, and two or three responses.",
            iconName: MaterialIcons.WebAsset
        ) {
            Children = {
                Demo.Group(
                    title: "Alerts",
                    description:
                    "The response you want people to take is suggested; the one that loses work is destructive.",
                    new AdwActionRow(
                        title: "Unsaved Changes",
                        subtitle: "Cancel · Discard · Save"
                    ) {
                        Suffixes = {
                            new AdwButton(label: "Show", onPressed: () => ShowSave(host)),
                        },
                    },
                    new AdwActionRow(
                        title: "Destructive",
                        subtitle: "One way out, and it is the loud one"
                    ) {
                        Suffixes = {
                            new AdwButton(label: "Show", onPressed: () => ShowDelete(host)),
                        },
                    },
                    new AdwActionRow(
                        title: "Plain",
                        subtitle: "Body text and a single acknowledgement"
                    ) {
                        Suffixes = {
                            new AdwButton(label: "Show", onPressed: () => ShowPlain(host)),
                        },
                    },
                    new AdwActionRow(
                        title: "With an Extra Child",
                        subtitle: "A check button between the body and the responses"
                    ) {
                        Suffixes = {
                            new AdwButton(label: "Show", onPressed: () => ShowExtraChild(host)),
                        },
                    }
                ),
                Demo.Group(
                    title: "Dialogs",
                    description:
                    "The same presenter without the alert layout — anything can be the content.",
                    new AdwActionRow(
                        title: "Custom Dialog",
                        subtitle: "A toolbar view inside a dialog"
                    ) {
                        Suffixes = { new AdwButton(label: "Show", onPressed: ShowCustom) },
                    }
                ),
                Demo.Caption("Escape or a click on the scrim closes any of them."),
            },
        };
    }

    private static void ShowSave(GalleryHost host)
    {
        var dialog = new AdwAlertDialog(
            heading: "Save Changes?",
            body:
            "“Untitled Document” contains unsaved changes. Changes which are not saved will be permanently lost."
        ) {
            OnResponse = id => host.Toast($"Response: {id}"),
            CloseResponse = "cancel",
            // Enter saves. Without this the first response added takes the focus, so the keyboard
            // default would be Cancel — GNOME points it at the suggested response.
            DefaultResponse = "save",
        };
        dialog.AddResponse(id: "cancel", label: "Cancel");
        dialog.AddResponse(
            id: "discard",
            label: "Discard",
            appearance: AdwResponseAppearance.Destructive
        );
        dialog.AddResponse(id: "save", label: "Save", appearance: AdwResponseAppearance.Suggested);
        dialog.Show();
    }

    private static void ShowDelete(GalleryHost host)
    {
        var dialog = new AdwAlertDialog(
            heading: "Delete Project?",
            body: "This removes the project and everything in it. There is no undo."
        ) {
            OnResponse = id => host.Toast($"Response: {id}"),
            CloseResponse = "cancel",
            // A destructive alert defaults to the safe response: Enter must not delete.
            DefaultResponse = "cancel",
        };
        dialog.AddResponse(id: "cancel", label: "Cancel");
        dialog.AddResponse(
            id: "delete",
            label: "Delete",
            appearance: AdwResponseAppearance.Destructive
        );
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
            heading: "Empty Trash?",
            body: "All items in the trash will be permanently deleted."
        ) {
            ExtraChild =
                new Align(alignment: Alignment.Center, child: dontAsk) { HeightFactor = 1f },
            OnResponse = id => host.Toast(
                dontAsk.Value ? $"Response: {id} · won't ask again" : $"Response: {id}"
            ),
            CloseResponse = "cancel",
            DefaultResponse = "cancel",
        };
        dialog.AddResponse(id: "cancel", label: "Cancel");
        dialog.AddResponse(
            id: "empty",
            label: "Empty Trash",
            appearance: AdwResponseAppearance.Destructive
        );
        dialog.Show();
    }

    private static void ShowPlain(GalleryHost host)
    {
        var dialog = new AdwAlertDialog(
            heading: "Update Installed",
            body: "Adwaita Demo will use the new version the next time it starts."
        ) {
            OnResponse = _ => host.Toast("Acknowledged"),
            CloseResponse = "ok",
        };
        dialog.AddResponse(id: "ok", label: "OK", appearance: AdwResponseAppearance.Suggested);
        dialog.Show();
    }

    private static void ShowCustom()
    {
        Demo.ShowDialog(
            title: "Custom Dialog",
            content: new Padding(
                padding: EdgeInsets.All(Spacing.Lg),
                child: new AdwPreferencesGroup("Anything Goes") {
                    Rows = {
                        new AdwEntryRow(title: "Name", text: "Untitled"),
                        new AdwComboRow(title: "Format", items: ["PNG", "SVG", "PDF"]),
                        new AdwSwitchRow(title: "Open when finished", value: true),
                    },
                }
            ),
            width: 420f,
            height: 320f
        );
    }
}
