using Zigote.UI.Host;
using Zigote.UI.Material;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>Dialogs, snackbars and tooltips.</summary>
internal sealed class OverlaysPage : ComposedWidget
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
                // Tooltips are revealed by hover, and a finger never hovers — on a phone the
                // hover-only chip would be a dead control with no way to read its message, so
                // there the chip carries the tip itself and hands it to a snackbar on tap.
                new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                    ? new Chip(
                        "Tap for the tip",
                        onPressed: () => Toast("Tooltips are hover-only — a tap shows this instead")
                    )
                    : new Tooltip(
                        "Tooltips appear after a short hover",
                        new Chip("Hover for a tip")
                    )
                )
            )
        );
    }
}