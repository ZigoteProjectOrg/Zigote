using Zigote.Core;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>Chips, badges, avatars, dividers, list tiles and tooltips.</summary>
internal sealed class DisplayPage : ComposedWidget
{
    private readonly bool[] _filters = [true, false, false];
    private bool _switch = true;

    protected override Widget Build(BuildContext context)
    {
        return Sections(
            Section(
                "Chips",
                new Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                        new Chip("Plain"),
                        new FilterChip(
                            new Text("Filter A"),
                            _filters[0],
                            v => Set(() => _filters[0] = v)
                        ),
                        new FilterChip(
                            new Text("Filter B"),
                            _filters[1],
                            v => Set(() => _filters[1] = v)
                        ),
                        new ChoiceChip(
                            new Text("Choice"),
                            _filters[2],
                            v => Set(() => _filters[2] = v)
                        ),
                    ]
                )
            ),
            Section(
                "Badge & avatar",
                new Row(
                    [
                        new Badge(new Icon(MaterialIcons.Doorbell) { Size = 28 }, 3),
                        new SizedBox(24),
                        new CircleAvatar(new Text("AZ"), Colors.Indigo),
                        new SizedBox(12),
                        new CircleAvatar(new Icon(MaterialIcons.Home), Colors.Teal),
                    ]
                )
            ),
            Section(
                "Dividers",
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new Text("above"),
                        new Divider(),
                        new Text("below"),
                        new SizedBox(
                            height: 28,
                            child: new Row(
                                [
                                    new Text("left"), new VerticalDivider(), new Text("right"),
                                ]
                            )
                        ),
                    ]
                )
            ),
            Section(
                "List tiles",
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new ListTile(
                            new Icon(MaterialIcons.Home),
                            new Text("Home"),
                            new Text("Landing screen"),
                            onPressed: () => Toast("Home")
                        ),
                        new Divider(),
                        new ListTile(
                            new Icon(MaterialIcons.Settings),
                            new Text("Notifications"),
                            trailing: new Switch(_switch, v => Set(() => _switch = v)),
                            onPressed: () => Toast("Notifications")
                        ),
                    ]
                )
            ),
            Section("Tooltip", TooltipDemo())
        );
    }

    // A tooltip only ever surfaces on hover, which fingers never produce — on a phone the bare
    // button would be a dead control whose label is unreachable. Compact keeps the tooltip (a
    // mouse in a narrow window still gets it) but also puts the text behind a tap.
    private static Widget TooltipDemo()
    {
        const string message = "This is a tooltip";

        return new AdaptiveBuilder((_, size) => new Tooltip(
                message,
                size == WindowSizeClass.Compact
                    ? new OutlinedButton(new Text("Tap for a tip"), () => Toast(message))
                    : new OutlinedButton(new Text("Hover me"))
            )
        );
    }

    private void Set(Action mutate)
    {
        mutate();
        MarkNeedsBuild();
    }
}