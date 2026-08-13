namespace AdwaitaGallery.Pages;

/// <summary>
///     Large Lists — two thousand rows in a boxed list. <see cref="ListView" /> measures and paints
///     only the rows the viewport can see, so the scroll stays flat however far down you go; the
///     counter is a signal the list writes on every scroll.
/// </summary>
public sealed class LargeListsPage : ComposedWidget
{
    private const int Rows = 2000;

    private static readonly string[] Kinds = [
        "Document", "Spreadsheet", "Presentation", "Image", "Archive", "Recording",
    ];

    private readonly Signal<int> _first = new(0);
    private readonly Signal<string> _picked = new("nothing yet");

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        var list = ListView.Builder(
            itemCount: Rows,
            itemBuilder: Row,
            itemExtent: AdwMetrics.RowMinHeight
        );
        list.OnScrolled = (_, y) =>
            _first.Value = (int)MathF.Floor(y / AdwMetrics.RowMinHeight) + 1;

        return new GalleryPage(
            title: "Large Lists",
            description:
            "Two thousand rows, of which only the visible ones are ever measured or painted.",
            iconName: MaterialIcons.FormatListNumbered
        ) {
            ClampWidth = 680f,
            Children = {
                Demo.Bar(
                    new Watch(() => Demo.Value($"first visible row: {_first.Value}")),
                    new Watch(() => Demo.Value($"picked: {_picked.Value}"))
                ),
                new SizedBox(
                    height: 420f,
                    child: new DecoratedBox {
                        Fill = p.CardBg,
                        Radius = AdwMetrics.CardRadius,
                        BorderColor = p.CardShade,
                        BorderWidth = 1f,
                        Child = new ClipRRect(radius: AdwMetrics.CardRadius, child: list),
                    }
                ),
                Demo.Caption(
                    "The rows are plain AdwActionRows — the list simply never asks the off-screen ones to build."
                ),
            },
        };
    }

    private Widget Row(int index)
    {
        string kind = Kinds[index % Kinds.Length];
        return new AdwActionRow(
            title: $"{kind} {index + 1:0000}",
            subtitle: $"Modified {(index % 28) + 1} days ago"
        ) {
            IconName = MaterialIcons.Description,
            ShowChevron = true,
            OnActivated = () => _picked.Value = $"{kind} {index + 1:0000}",
        };
    }
}
