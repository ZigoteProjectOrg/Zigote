namespace AdwaitaGallery.Pages;

/// <summary>
///     The two list shapes a boxed list cannot express: a hierarchy you expand, and an order you
///     set by dragging.
/// </summary>
public sealed class TreesPage : ComposedWidget
{
    private static readonly Node[] Roots = [
        new(
            Name: "Zigote",
            Children: [
                new Node(
                    Name: "Zigote.UI",
                    Children: [
                        new Node(Name: "Widgets", Children: [new Node("Layout"), new Node("Controls")]),
                        new Node("Theme"),
                        new Node("Semantics"),
                    ]
                ),
                new Node(
                    Name: "Zigote.UI.Adwaita",
                    Children: [new Node("Theme"), new Node("Widgets")]
                ),
                new Node("README.md"),
            ]
        ),
    ];

    private readonly List<string> _order = ["Bloom", "Tone mapping", "Colour grading", "Vignette", "Film grain"];
    private readonly Signal<string> _selected = new("(nothing selected)");
    private readonly Signal<int> _revision = new(0);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return new GalleryPage(
            title: "Trees & Reordering",
            description: "A hierarchy you expand, and a list you put in order by dragging.",
            iconName: MaterialIcons.AccountTree
        ) {
            Children = {
                Demo.Titled(
                    title: "Tree View",
                    description:
                    "Rows indent per level and expand on the chevron. The tree is described by " +
                    "three delegates, so it works over any data.",
                    child: Demo.Stage(
                        child: new AdwTreeView<Node>(
                            roots: Roots,
                            childrenOf: n => n.Children ?? [],
                            labelOf: n => n.Name,
                            onSelect: n => _selected.Value = n.Name
                        ) {
                            IconOf = n => n.Children is null
                                ? MaterialIcons.Description
                                : MaterialIcons.Folder,
                        },
                        padding: AdwMetrics.MenuMargin
                    )
                ),
                new Watch(() => Demo.Group(
                        title: "Selection",
                        description: null,
                        new AdwActionRow("Selected") { Suffixes = { Demo.Value(_selected.Value) } }
                    )
                ),
                Demo.Titled(
                    title: "Reorderable List",
                    description:
                    "Drag a row by its grip. A line shows where it will land, and the order " +
                    "below follows the drop.",
                    child: Demo.Stage(
                        child: new Watch(() =>
                            {
                                _ = _revision.Value;
                                var rows = new List<Widget>();
                                foreach (string name in _order)
                                {
                                    rows.Add(
                                        new Align(
                                            alignment: Alignment.CenterLeft,
                                            child: new Label(
                                                text: name,
                                                style: AdwTypography.Body,
                                                color: theme.OnBackground
                                            )
                                        )
                                    );
                                }

                                return new AdwReorderableList(
                                    items: rows,
                                    onReorder: (from, to) =>
                                    {
                                        string moved = _order[from];
                                        _order.RemoveAt(from);
                                        _order.Insert(index: Math.Clamp(value: to, min: 0, max: _order.Count), item: moved);
                                        _revision.Value++;
                                    }
                                );
                            }
                        ),
                        padding: 0f
                    )
                ),
                new Watch(() =>
                    {
                        _ = _revision.Value;
                        return Demo.Caption(string.Join(separator: " → ", values: _order));
                    }
                ),
            },
        };
    }

    private sealed record Node(string Name, Node[]? Children = null);
}
