using Zigote.Core;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>Trees, reorderable lists, tabs and split views.</summary>
internal sealed class DataPage : StatefulWidget
{
    protected override WidgetState CreateState()
    {
        return new DataPageState();
    }
}

internal sealed class DataPageState : WidgetState<DataPage>
{
    private int _innerTab;
    private int _navSel;

    public override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        // The window's size class, not the pane's: every section here sits in a grid cell that is
        // only ~540 px wide on a desktop window, so an AdaptiveBuilder inside one would read
        // Compact there and hand the desktop the phone arm.
        var compact = MediaQuery.Of(context).SizeClass == WindowSizeClass.Compact;

        var roots = new List<Node> {
            new(
                "Fruits",
                [
                    new Node("Apple", []), new Node("Banana", []),
                    new Node("Citrus", [new Node("Orange", []), new Node("Lemon", [])]),
                ]
            ),
            new("Vegetables", [new Node("Carrot", []), new Node("Pea", [])]),
        };

        // A drag from rest belongs to the page scroller on touch, so a finger lifts a row by
        // pressing and holding it.
        var grab = compact ? "hold to reorder" : "drag to reorder";
        var rows = new List<Widget> {
            new ListTile(new Icon(MaterialIcons.Home), new Text($"First — {grab}")),
            new ListTile(new Icon(MaterialIcons.Settings), new Text($"Second — {grab}")),
            new ListTile(new Icon(MaterialIcons.Search), new Text($"Third — {grab}")),
        };

        string[] mail = ["Inbox", "Sent", "Drafts"];

        return Grid2(
            Section(
                "TreeView",
                new SizedBox(
                    height: 200,
                    // TreeView measures to its full row count and scrolls nothing itself, so rows
                    // past the box were clipped and unreachable; the scroll view also gives the
                    // tree drag-to-scroll on touch.
                    child: new SingleChildScrollView(
                        new TreeView<Node>(
                            roots,
                            n => n.Children,
                            n => n.Name,
                            n => Toast(n.Name)
                        )
                    )
                )
            ),
            Section(
                "ReorderableListView",
                new SizedBox(
                    height: 150,
                    child: new ReorderableListView(rows, (o, n) => Toast($"moved {o} → {n}"))
                )
            ),
            Section(
                "TabBar + TabBarView",
                new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new TabBar(
                            [
                                new Tab("First"),
                                new Tab("Second"),
                                new Tab("Third"),
                            ],
                            _innerTab,
                            i => SetStateRebuild(() => _innerTab = i)
                        ),
                        new SizedBox(
                            // Finger-sized tabs eat most of a 72-px box; give the page below room.
                            height: compact ? 96 : 72,
                            child: new TabBarView(
                                [
                                    new Center(new Text("First page")),
                                    new Center(new Text("Second page")),
                                    new Center(new Text("Third page")),
                                ],
                                _innerTab
                            )
                        ),
                    ]
                )
            ),
            Section(
                "SplitPane",
                new SizedBox(
                    height: 140,
                    child: new SplitPane(
                        theme,
                        new ColoredBox(Colors.Indigo, new Center(new Text("First"))),
                        new ColoredBox(Colors.Teal, new Center(new Text("Second")))
                    ) { SplitRatio = 0.4f }
                )
            ),
            Section(
                "NavigationSplitView",
                // Side by side the two panes want 160 px each plus a divider that then has nowhere
                // to travel, and the sidebar labels clip. A phone shows the source list stacked
                // over the detail instead — the same selection model, one column.
                compact
                    ? MailStack(theme, mail)
                    : new SizedBox(
                        height: 220,
                        child: new NavigationSplitView(
                            theme,
                            mail,
                            i => new Center(new Text($"{mail[i]} detail")),
                            _navSel,
                            i => SetStateRebuild(() => _navSel = i)
                        )
                    )
            ),
            Section(
                "Toolbar",
                new Toolbar(
                    [new TextButton(new Text("File")), new TextButton(new Text("Edit"))],
                    [new IconButton(new Icon(MaterialIcons.Search), () => { })]
                )
            )
        );
    }

    private Widget MailStack(ThemeData theme, string[] mail)
    {
        var stack = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch);
        for (var i = 0; i < mail.Length; i++)
        {
            var index = i;
            stack.Children.Add(
                new ListTile(
                    title: new Text(mail[index]),
                    onPressed: () => SetStateRebuild(() => _navSel = index),
                    selected: index == _navSel
                )
            );
        }

        stack.Children.Add(new SizedBox(height: 12));
        stack.Children.Add(
            new SizedBox(
                height: 120,
                child: new ColoredBox(
                    theme.Fill2,
                    new Center(new Text($"{mail[_navSel]} detail"))
                )
            )
        );
        return stack;
    }

    private sealed record Node(string Name, List<Node> Children);
}