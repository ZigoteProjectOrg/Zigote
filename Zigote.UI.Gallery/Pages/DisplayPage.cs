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
                title: "Chips",
                child: new Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                        new Chip("Plain"),
                        new FilterChip(
                            label: new Text("Filter A"),
                            selected: _filters[0],
                            onSelected: v => Set(() => _filters[0] = v)
                        ),
                        new FilterChip(
                            label: new Text("Filter B"),
                            selected: _filters[1],
                            onSelected: v => Set(() => _filters[1] = v)
                        ),
                        new ChoiceChip(
                            label: new Text("Choice"),
                            selected: _filters[2],
                            onSelected: v => Set(() => _filters[2] = v)
                        ),
                    ]
                )
            ),
            Section(
                title: "Badge & avatar",
                child: new Row(
                    [
                        new Badge(child: new Icon(MaterialIcons.Doorbell) { Size = 28 }, count: 3),
                        new SizedBox(24),
                        new CircleAvatar(child: new Text("AZ"), backgroundColor: Colors.Indigo),
                        new SizedBox(12),
                        new CircleAvatar(
                            child: new Icon(MaterialIcons.Home),
                            backgroundColor: Colors.Teal
                        ),
                    ]
                )
            ),
            Section(
                title: "Dividers",
                child: new Column(
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
                title: "List tiles",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new ListTile(
                            leading: new Icon(MaterialIcons.Home),
                            title: new Text("Home"),
                            subtitle: new Text("Landing screen"),
                            onPressed: () => Toast("Home")
                        ),
                        new Divider(),
                        new ListTile(
                            leading: new Icon(MaterialIcons.Settings),
                            title: new Text("Notifications"),
                            trailing: new Switch(
                                value: _switch,
                                onChanged: v => Set(() => _switch = v)
                            ),
                            onPressed: () => Toast("Notifications")
                        ),
                    ]
                )
            ),
            Section(title: "Tooltip", child: TooltipDemo())
        );
    }

    // A tooltip only ever surfaces on hover, which fingers never produce — on a phone the bare
    // button would be a dead control whose label is unreachable. Compact keeps the tooltip (a
    // mouse in a narrow window still gets it) but also puts the text behind a tap.
    private static Widget TooltipDemo()
    {
        const string message = "This is a tooltip";

        return new AdaptiveBuilder((_, size) => new Tooltip(
                message: message,
                child: size == WindowSizeClass.Compact
                    ? new OutlinedButton(
                        child: new Text("Tap for a tip"),
                        onPressed: () => Toast(message)
                    )
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
