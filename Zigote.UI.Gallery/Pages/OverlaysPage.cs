using Zigote.UI.Host;
using Zigote.UI.Material;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>Dialogs, snackbars and tooltips.</summary>
internal sealed class OverlaysPage : StatelessWidget
{
    protected override Widget Build(BuildContext context)
    {
        return Sections(
            Section(
                "Dialogs",
                new Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                        new ElevatedButton(
                            new Text("Alert"),
                            () => Dialog.Alert("Heads up", "This is an alert dialog.").Show()
                        ),
                        new OutlinedButton(
                            new Text("Confirm"),
                            () => Dialog.Confirm(
                                "Delete file?",
                                "This can't be undone.",
                                () => Toast("Confirmed"),
                                () => Toast("Cancelled")
                            ).Show()
                        ),
                    ]
                )
            ),
            Section(
                "Snackbars",
                new Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                        new FilledButton(new Text("Show snackbar"), () => Toast("It worked!")),
                        new TextButton(
                            new Text("With action"),
                            () => App.Active?.ShowSnackbar(
                                "Item archived",
                                4f,
                                "Undo",
                                () => Toast("Undone")
                            )
                        ),
                    ]
                )
            ),
            Section(
                "Tooltip",
                new Tooltip(
                    "Tooltips appear after a short hover",
                    new Chip("Hover for a tip")
                )
            )
        );
    }
}