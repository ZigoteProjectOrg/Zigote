using Zigote.UI.Widgets.DragDrop;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Drag and Drop — a typed payload picked up from one list and dropped into another. The target
///     highlights while a compatible drag is over it, and the ghost under the pointer is a widget
///     like any other.
/// </summary>
public sealed class DragDropPage : StatelessWidget
{
    private readonly Signal<List<string>> _backlog = new(
        [
            "Write the release notes",
            "Update the screenshots",
            "Check the dark palette",
            "Answer the bug report",
        ]
    );

    private readonly Signal<List<string>> _done = new([]);

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);

        return new GalleryPage(
            "Drag and Drop",
            "Pick a task up and drop it in Done — a typed payload, a drop target and a ghost.",
            MaterialIcons.OpenWith
        ) {
            ClampWidth = 760f,
            Children = {
                new LayoutBuilder((_, c) => Board(host, c.MaxWidth < 520f)),
                Demo.Group(
                    "The Pieces",
                    null,
                    new AdwActionRow("Draggable<T>", "Arms after 6 px of travel, then carries T"),
                    new AdwActionRow(
                        "DragTarget<T>",
                        "Rebuilds with a highlight flag while hovered"
                    ),
                    new AdwActionRow("Feedback", "Any widget, painted under the pointer")
                ),
                Demo.Bar(
                    new AdwButton(
                        "Reset",
                        () =>
                        {
                            _backlog.Value = [
                                "Write the release notes",
                                "Update the screenshots",
                                "Check the dark palette",
                                "Answer the bug report",
                            ];
                            _done.Value = [];
                        }
                    )
                ),
            },
        };
    }

    private Widget Board(GalleryHost host, bool stacked)
    {
        Widget backlog = new Watch(() => Column(
                "Backlog",
                _backlog.Value,
                true,
                host
            )
        );
        Widget done = new Watch(() => Column(
                "Done",
                _done.Value,
                false,
                host
            )
        );

        if (stacked)
            return new Column(
                spacing: Spacing.Md,
                mainAxisSize: MainAxisSize.Min,
                crossAxisAlignment: CrossAxisAlignment.Stretch
            ) {
                Children = {
                    backlog,
                    done,
                },
            };

        return new Row(spacing: Spacing.Md, crossAxisAlignment: CrossAxisAlignment.Start) {
            Children = {
                new Expanded(backlog),
                new Expanded(done),
            },
        };
    }

    /// <summary>One column of the board: a drop target wrapping the cards it currently holds.</summary>
    private Widget Column(string title, List<string> items, bool isBacklog, GalleryHost host)
    {
        // One retained zone, highlighted by recolouring it: the builder runs on every hover change,
        // mid-drag, and handing back a new widget there would remount the cards — including the
        // Draggable under the pointer, whose capture the drag depends on.
        var zone = new DropZone(title, items.Count, Cards(items));
        return new DragTarget<string>(hovering =>
            {
                zone.Hovering = hovering;
                return zone;
            }
        ) {
            OnAccept = item =>
            {
                var from = isBacklog ? _done : _backlog;
                var to = isBacklog ? _backlog : _done;
                if (!from.Value.Contains(item)) return;

                // New lists rather than mutation: the signal compares by reference, so a mutated
                // list would never announce itself.
                from.Value = [.. from.Value.Where(x => x != item)];
                to.Value = [.. to.Value, item];
                host.Toast(isBacklog ? $"Reopened “{item}”" : $"Finished “{item}”");
            },
        };
    }

    private static Widget Cards(List<string> items)
    {
        var column = new Column(
            spacing: Spacing.Sm,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Stretch
        );
        foreach (var item in items)
            column.Children.Add(
                new Draggable<string>(
                    item,
                    new TaskCard(item),
                    // The ghost is measured against the whole window, so it has to bring its own
                    // width — a card that fills its column would otherwise fill the screen.
                    () => new SizedBox(240f, child: new TaskCard(item) { Ghost = true })
                ) { DragText = item }
            );
        if (items.Count == 0) column.Children.Add(Demo.Caption("Drop something here"));
        return column;
    }
}

/// <summary>A task card — the thing being dragged, and (with <see cref="Ghost" />) its own ghost.</summary>
internal sealed class TaskCard(string text) : StatelessWidget
{
    /// <summary>Lifted look for the widget painted under the pointer.</summary>
    public bool Ghost { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        return new DecoratedBox {
            Fill = p.CardBg,
            Radius = AdwMetrics.ControlRadius,
            BorderColor = Ghost ? theme.Accent : p.CardShade,
            BorderWidth = 1f,
            Elevation = Ghost ? Elevation.Z2 : null,
            Child = new Padding(
                EdgeInsets.Symmetric(Spacing.Md, Spacing.Sm),
                // MainAxisSize.Min: in the column the surrounding stretch gives the card its width,
                // and as a ghost it hugs its label instead of running the width it is offered.
                new Row(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) {
                    Children = {
                        new IconGlyph(
                            MaterialIcons.DragIndicator,
                            AdwMetrics.IconSize,
                            theme.TextSecondary
                        ),
                        new Label(text, AdwTypography.Body, theme.OnBackground) {
                            MaxLines = 2,
                            Overflow = TextOverflow.Ellipsis,
                        },
                    },
                }
            ),
        };
    }
}

/// <summary>
///     The column frame: a well that takes the accent while a compatible drag hovers.
///     <see cref="Hovering" /> recolours the retained box rather than rebuilding — the cards inside
///     must survive the highlight, since one of them may be the widget being dragged.
/// </summary>
internal sealed class DropZone(string title, int count, Widget child) : StatelessWidget
{
    private DecoratedBox? _box;
    private bool _hovering;
    private AdwColors _palette = AdwPalette.Light;
    private ThemeData _theme = ThemeData.Dark;

    public bool Hovering
    {
        set
        {
            if (_hovering == value) return;
            _hovering = value;
            ApplyHighlight();
        }
    }

    private void ApplyHighlight()
    {
        if (_box is null) return;
        _box.Fill = _hovering ? _theme.Accent.WithAlpha(0.1f) : _theme.Fill2;
        _box.BorderColor = _hovering ? _theme.Accent : _palette.CardShade;
        _box.BorderWidth = _hovering ? 2f : 1f;
        _box.MarkNeedsPaint();
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        _theme = theme;
        _palette = p;
        _box = new DecoratedBox {
            Fill = _hovering ? theme.Accent.WithAlpha(0.1f) : theme.Fill2,
            Radius = AdwMetrics.CardRadius,
            BorderColor = _hovering ? theme.Accent : p.CardShade,
            BorderWidth = _hovering ? 2f : 1f,
            Child = new Padding(
                EdgeInsets.All(Spacing.Md),
                new Column(
                    spacing: Spacing.Sm,
                    mainAxisSize: MainAxisSize.Min,
                    crossAxisAlignment: CrossAxisAlignment.Stretch
                ) {
                    Children = {
                        new Row {
                            Children = {
                                new Expanded(
                                    new Label(title, AdwTypography.Heading, theme.OnBackground)
                                ),
                                Demo.Value(count.ToString()),
                            },
                        },
                        child,
                    },
                }
            ),
        };
        return _box;
    }
}