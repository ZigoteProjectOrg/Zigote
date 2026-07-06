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

        var rows = new List<Widget> {
            new ListTile(new Icon(MaterialIcons.Home), new Text("First — drag to reorder")),
            new ListTile(new Icon(MaterialIcons.Settings), new Text("Second — drag to reorder")),
            new ListTile(new Icon(MaterialIcons.Search), new Text("Third — drag to reorder")),
        };

        string[] mail = ["Inbox", "Sent", "Drafts"];

        return Grid2(
            Section(
                "TreeView",
                new SizedBox(
                    height: 200,
                    child: new TreeView<Node>(
                        roots,
                        n => n.Children,
                        n => n.Name,
                        n => Toast(n.Name)
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
                            height: 72,
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
                new SizedBox(
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

    private sealed record Node(string Name, List<Node> Children);
}