using Zigote.Core;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using static Gallery.GalleryUi;

namespace Gallery;

/// <summary>Trees, reorderable lists, tabs and split views.</summary>
internal sealed class DataPage : ComposedWidget
{
    private int _innerTab;
    private int _navSel;

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        // The window's size class, not the pane's: every section here sits in a grid cell that is
        // only ~540 px wide on a desktop window, so an AdaptiveBuilder inside one would read
        // Compact there and hand the desktop the phone arm.
        bool compact = MediaQuery.Of(context).SizeClass == WindowSizeClass.Compact;

        var roots = new List<Node> {
            new(
                Name: "Fruits",
                Children: [
                    new Node(Name: "Apple", Children: []), new Node(Name: "Banana", Children: []),
                    new Node(
                        Name: "Citrus",
                        Children: [
                            new Node(Name: "Orange", Children: []),
                            new Node(Name: "Lemon", Children: []),
                        ]
                    ),
                ]
            ),
            new(
                Name: "Vegetables",
                Children: [
                    new Node(Name: "Carrot", Children: []), new Node(Name: "Pea", Children: []),
                ]
            ),
        };

        // A drag from rest belongs to the page scroller on touch, so a finger lifts a row by
        // pressing and holding it.
        string grab = compact ? "hold to reorder" : "drag to reorder";
        var rows = new List<Widget> {
            new ListTile(leading: new Icon(MaterialIcons.Home), title: new Text($"First — {grab}")),
            new ListTile(
                leading: new Icon(MaterialIcons.Settings),
                title: new Text($"Second — {grab}")
            ),
            new ListTile(
                leading: new Icon(MaterialIcons.Search),
                title: new Text($"Third — {grab}")
            ),
        };

        string[] mail = ["Inbox", "Sent", "Drafts"];

        return Grid2(
            Section(
                title: "TreeView",
                child: new SizedBox(
                    height: 200,
                    // TreeView measures to its full row count and scrolls nothing itself, so rows
                    // past the box were clipped and unreachable; the scroll view also gives the
                    // tree drag-to-scroll on touch.
                    child: new SingleChildScrollView(
                        new TreeView<Node>(
                            roots: roots,
                            childrenOf: n => n.Children,
                            labelOf: n => n.Name,
                            onSelect: n => Toast(n.Name)
                        )
                    )
                )
            ),
            Section(
                title: "ReorderableListView",
                child: new SizedBox(
                    height: 150,
                    child: new ReorderableListView(
                        children: rows,
                        onReorder: (o, n) => Toast($"moved {o} → {n}")
                    )
                )
            ),
            Section(
                title: "TabBar + TabBarView",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    children: [
                        new TabBar(
                            tabs: [
                                new Tab("First"),
                                new Tab("Second"),
                                new Tab("Third"),
                            ],
                            initialIndex: _innerTab,
                            onChanged: i =>
                            {
                                _innerTab = i;
                                MarkNeedsBuild();
                            }
                        ),
                        new SizedBox(
                            // Finger-sized tabs eat most of a 72-px box; give the page below room.
                            height: compact ? 96 : 72,
                            child: new TabBarView(
                                children: [
                                    new Center(new Text("First page")),
                                    new Center(new Text("Second page")),
                                    new Center(new Text("Third page")),
                                ],
                                initialIndex: _innerTab
                            )
                        ),
                    ]
                )
            ),
            Section(
                title: "SplitPane",
                child: new SizedBox(
                    height: 140,
                    child: new SplitPane(
                        theme: theme,
                        first: new ColoredBox(
                            color: Colors.Indigo,
                            child: new Center(new Text("First"))
                        ),
                        second: new ColoredBox(
                            color: Colors.Teal,
                            child: new Center(new Text("Second"))
                        )
                    ) { SplitRatio = 0.4f }
                )
            ),
            Section(
                title: "NavigationSplitView",
                // Side by side the two panes want 160 px each plus a divider that then has nowhere
                // to travel, and the sidebar labels clip. A phone shows the source list stacked
                // over the detail instead — the same selection model, one column.
                child: compact
                    ? MailStack(theme: theme, mail: mail)
                    : new SizedBox(
                        height: 220,
                        child: new NavigationSplitView(
                            theme: theme,
                            items: mail,
                            detailBuilder: i => new Center(new Text($"{mail[i]} detail")),
                            selected: _navSel,
                            onChanged: i =>
                            {
                                _navSel = i;
                                MarkNeedsBuild();
                            }
                        )
                    )
            ),
            Section(
                title: "Toolbar",
                child: new Toolbar(
                    leading: [new TextButton(new Text("File")), new TextButton(new Text("Edit"))],
                    trailing: [
                        new IconButton(icon: new Icon(MaterialIcons.Search), onPressed: () => { }),
                    ]
                )
            )
        );
    }

    private Widget MailStack(ThemeData theme, string[] mail)
    {
        var stack = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch);
        for (int i = 0; i < mail.Length; i++)
        {
            int index = i;
            stack.Children.Add(
                new ListTile(
                    title: new Text(mail[index]),
                    onPressed: () =>
                    {
                        _navSel = index;
                        MarkNeedsBuild();
                    },
                    selected: index == _navSel
                )
            );
        }

        stack.Children.Add(new SizedBox(height: 12));
        stack.Children.Add(
            new SizedBox(
                height: 120,
                child: new ColoredBox(
                    color: theme.Fill2,
                    child: new Center(new Text($"{mail[_navSel]} detail"))
                )
            )
        );
        return stack;
    }

    private sealed record Node(string Name, List<Node> Children);
}
