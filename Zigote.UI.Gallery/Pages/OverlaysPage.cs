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
                title: "Dialogs",
                child: new Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                        new ElevatedButton(
                            child: new Text("Alert"),
                            onPressed: () => Dialog.Alert(
                                title: "Heads up",
                                message: "This is an alert dialog."
                            ).Show()
                        ),
                        new OutlinedButton(
                            child: new Text("Confirm"),
                            onPressed: () => Dialog.Confirm(
                                title: "Delete file?",
                                message: "This can't be undone.",
                                onConfirm: () => Toast("Confirmed"),
                                onCancel: () => Toast("Cancelled")
                            ).Show()
                        ),
                    ]
                )
            ),
            Section(
                title: "Snackbars",
                child: new Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                        new FilledButton(
                            child: new Text("Show snackbar"),
                            onPressed: () => Toast("It worked!")
                        ),
                        new TextButton(
                            child: new Text("With action"),
                            onPressed: () => App.Active?.ShowSnackbar(
                                message: "Item archived",
                                duration: 4f,
                                actionLabel: "Undo",
                                onAction: () => Toast("Undone")
                            )
                        ),
                    ]
                )
            ),
            Section(
                title: "Tooltip",
                // Tooltips are revealed by hover, and a finger never hovers — on a phone the
                // hover-only chip would be a dead control with no way to read its message, so
                // there the chip carries the tip itself and hands it to a snackbar on tap.
                child: new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                    ? new Chip(
                        label: "Tap for the tip",
                        onPressed: () => Toast("Tooltips are hover-only — a tap shows this instead")
                    )
                    : new Tooltip(
                        message: "Tooltips appear after a short hover",
                        child: new Chip("Hover for a tip")
                    )
                )
            )
        );
    }
}
