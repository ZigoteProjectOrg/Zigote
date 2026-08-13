using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.DragDrop;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>
///     Live bench for the drag-and-drop + clipboard support: in-app <see cref="Draggable{T}" /> →
///     <see cref="DragTarget{T}" />, external OS file drops, two-way clipboard, and macOS drag-out.
/// </summary>
internal sealed class DragDropPage : ComposedWidget
{
    private readonly Label _files = new("Drop files from Finder / Explorer onto the box above.");
    private readonly Label _lastDrop = new("Nothing dropped yet.");
    private readonly Label _pasted = new("(paste result appears here)");

    protected override Widget Build(BuildContext ctx)
    {
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            children: [
                Section(
                    "In-app drag & drop",
                    new Column(
                        crossAxisAlignment: CrossAxisAlignment.Start,
                        children: [
                            new Text("Drag a chip into the drop zone:"),
                            new SizedBox(height: 10),
                            new Row(
                                mainAxisSize: MainAxisSize.Min,
                                children: [
                                    Chip("Red", Color.Red),
                                    new SizedBox(10),
                                    Chip("Green", Color.Green),
                                    new SizedBox(10),
                                    Chip("Blue", Color.Blue),
                                ]
                            ),
                            new SizedBox(height: 14),
                            DropZone(),
                            new SizedBox(height: 8),
                            _lastDrop,
                        ]
                    )
                ),
                Section(
                    "External file drop (OS → app)",
                    new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                        ? DesktopOnly("Desktop only — phones have no OS file drag.")
                        : new Column(
                            crossAxisAlignment: CrossAxisAlignment.Start,
                            children: [FileZone(), new SizedBox(height: 8), _files]
                        )
                    )
                ),
                Section(
                    "Clipboard (two-way)",
                    new Column(
                        crossAxisAlignment: CrossAxisAlignment.Start,
                        children: [
                            new Row(
                                mainAxisSize: MainAxisSize.Min,
                                children: [
                                    new FilledButton(
                                        new Text("Copy sample text"),
                                        () => ZigoteEngine.Instance?.SetClipboard(
                                            "Hello from Zigote!"
                                        )
                                    ),
                                    new SizedBox(10),
                                    new OutlinedButton(new Text("Paste"), Paste),
                                ]
                            ),
                            new SizedBox(height: 10),
                            _pasted,
                        ]
                    )
                ),
                Section(
                    "Drag OUT to the OS (macOS best-effort)",
                    new AdaptiveBuilder((_, size) => size == WindowSizeClass.Compact
                        ? DesktopOnly("Desktop only — no app-to-app drag on a phone.")
                        : new Column(
                            crossAxisAlignment: CrossAxisAlignment.Start,
                            children: [
                                new Text("Drag this onto Finder or a text field in another app:"),
                                new SizedBox(height: 10),
                                new Draggable<string>(
                                    "Zigote drag-out",
                                    Pill("⤴  Drag me out", Color.Purple),
                                    dragText: "Dragged out of Zigote"
                                ) { AllowDragOut = true },
                            ]
                        )
                    )
                ),
            ]
        );
    }

    // Both OS integrations above are desktop-only surfaces: there is no OS file drag and no
    // app-to-app drag on a phone, so those sections say so rather than baiting a dead gesture.
    private static Widget DesktopOnly(string note)
    {
        return new Label(note, 13, Colors.Grey[500]);
    }

    private void Paste()
    {
        var text = ZigoteEngine.Instance?.GetClipboard() ?? string.Empty;
        _pasted.Text = text.Length > 0 ? text : "(clipboard empty)";
    }

    private Widget Chip(string label, Color color)
    {
        return new Draggable<string>(label, Pill(label, color), dragText: label);
    }

    private static Widget Pill(string label, Color color)
    {
        return new DecoratedBox {
            Fill = color.WithAlpha(0.9f),
            Radius = Radii.Capsule,
            BorderWidth = 0f,
            Child = new Padding(
                EdgeInsets.Symmetric(Spacing.Md, Spacing.Sm),
                new Label(label, 13, Color.White)
            ),
        };
    }

    private Widget DropZone()
    {
        return new DragTarget<string>(hover => new DecoratedBox {
                Fill = hover
                    ? Color.Blue.WithAlpha(0.18f)
                    : new Color(
                        0.5f,
                        0.5f,
                        0.5f,
                        0.10f
                    ),
                BorderColor = hover
                    ? Color.Blue
                    : new Color(
                        0.5f,
                        0.5f,
                        0.5f,
                        0.45f
                    ),
                BorderWidth = 1.5f,
                Radius = Radii.Md,
                Child = new Padding(
                    EdgeInsets.All(Spacing.Xl),
                    new Center(new Text(hover ? "Release to drop" : "Drop a chip here"))
                ),
            }
        ) {
            OnAccept = s => _lastDrop.Text = $"Dropped: {s}",
        };
    }

    private Widget FileZone()
    {
        return new DragTarget<string>(hover => new DecoratedBox {
                Fill = hover
                    ? Color.Green.WithAlpha(0.18f)
                    : new Color(
                        0.5f,
                        0.5f,
                        0.5f,
                        0.10f
                    ),
                BorderColor = hover
                    ? Color.Green
                    : new Color(
                        0.5f,
                        0.5f,
                        0.5f,
                        0.45f
                    ),
                BorderWidth = 1.5f,
                Radius = Radii.Md,
                Child = new Padding(
                    EdgeInsets.All(Spacing.Xl),
                    new Center(
                        new Text(hover ? "Release to drop files" : "Drag files here from the OS")
                    )
                ),
            }
        ) {
            AcceptExternalFiles = true,
            OnAccept = path => _files.Text = $"Dropped file: {path}",
        };
    }
}
