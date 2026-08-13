using Zigote.UI.Widgets.DragDrop;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Drag and Drop — a typed payload picked up from one list and dropped into another. The target
///     highlights while a compatible drag is over it, and the ghost under the pointer is a widget
///     like any other.
/// </summary>
public sealed class DragDropPage : ComposedWidget
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
            title: "Drag and Drop",
            description:
            "Pick a task up and drop it in Done — a typed payload, a drop target and a ghost.",
            iconName: MaterialIcons.OpenWith
        ) {
            ClampWidth = 760f,
            Children = {
                new LayoutBuilder((_, c) => Board(host: host, stacked: c.MaxWidth < 520f)),
                Demo.Group(
                    title: "The Pieces",
                    description: null,
                    new AdwActionRow(
                        title: "Draggable<T>",
                        subtitle: "Arms after 6 px of travel, then carries T"
                    ),
                    new AdwActionRow(
                        title: "DragTarget<T>",
                        subtitle: "Rebuilds with a highlight flag while hovered"
                    ),
                    new AdwActionRow(
                        title: "Feedback",
                        subtitle: "Any widget, painted under the pointer"
                    )
                ),
                Demo.Bar(
                    new AdwButton(
                        label: "Reset",
                        onPressed: () =>
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
                title: "Backlog",
                items: _backlog.Value,
                isBacklog: true,
                host: host
            )
        );
        Widget done = new Watch(() => Column(
                title: "Done",
                items: _done.Value,
                isBacklog: false,
                host: host
            )
        );

        if (stacked)
        {
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
        }

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
        var zone = new DropZone(title: title, count: items.Count, child: Cards(items));
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
        foreach (string item in items)
        {
            column.Children.Add(
                new Draggable<string>(
                    data: item,
                    child: new TaskCard(item),
                    // The ghost is measured against the whole window, so it has to bring its own
                    // width — a card that fills its column would otherwise fill the screen.
                    feedbackBuilder: () => new SizedBox(
                        width: 240f,
                        child: new TaskCard(item) { Ghost = true }
                    )
                ) { DragText = item }
            );
        }

        if (items.Count == 0) column.Children.Add(Demo.Caption("Drop something here"));
        return column;
    }
}

/// <summary>A task card — the thing being dragged, and (with <see cref="Ghost" />) its own ghost.</summary>
internal sealed class TaskCard(string text) : ComposedWidget
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
                padding: EdgeInsets.Symmetric(horizontal: Spacing.Md, vertical: Spacing.Sm),
                // MainAxisSize.Min: in the column the surrounding stretch gives the card its width,
                // and as a ghost it hugs its label instead of running the width it is offered.
                child: new Row(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) {
                    Children = {
                        new IconGlyph(
                            glyph: MaterialIcons.DragIndicator,
                            size: AdwMetrics.IconSize,
                            color: theme.TextSecondary
                        ),
                        new Label(
                            text: text,
                            style: AdwTypography.Body,
                            color: theme.OnBackground
                        ) {
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
internal sealed class DropZone(string title, int count, Widget child) : ComposedWidget
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
                padding: EdgeInsets.All(Spacing.Md),
                child: new Column(
                    spacing: Spacing.Sm,
                    mainAxisSize: MainAxisSize.Min,
                    crossAxisAlignment: CrossAxisAlignment.Stretch
                ) {
                    Children = {
                        new Row {
                            Children = {
                                new Expanded(
                                    new Label(
                                        text: title,
                                        style: AdwTypography.Heading,
                                        color: theme.OnBackground
                                    )
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
