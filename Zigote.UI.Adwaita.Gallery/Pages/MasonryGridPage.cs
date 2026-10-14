namespace AdwaitaGallery.Pages;

/// <summary>
///     A masonry grid: the column count follows the width, and each tile drops into whichever
///     column is shortest, so tiles of different heights pack without gaps.
/// </summary>
public sealed class MasonryGridPage : ComposedWidget
{
    private static readonly float[] Ratios =
        [1f, 0.7f, 1.4f, 0.9f, 1.2f, 0.75f, 1f, 1.6f, 0.8f, 1.1f, 0.65f, 1.3f];

    private readonly Signal<float> _minColumn = new(160f);

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "Masonry Grid",
            description: "Columns that follow the width, tiles that keep their own height.",
            iconName: MaterialIcons.Dashboard
        ) {
            ClampWidth = 860f,
            Children = {
                Demo.Group(
                    title: "Options",
                    description: "Resize the window, or change the narrowest a column may get.",
                    new AdwActionRow("Minimum column") {
                        Suffixes = {
                            new SizedBox(
                                width: 200f,
                                child: new AdwSlider(
                                    value: 160f,
                                    min: 100f,
                                    max: 320f,
                                    onChanged: v => _minColumn.Value = MathF.Round(v)
                                )
                            ),
                            new Watch(() => Demo.Value($"{_minColumn.Value:0} px")),
                        },
                    }
                ),
                new Watch(() =>
                    {
                        var accents = Enum.GetValues<AdwAccent>();
                        var tiles = new List<Widget>();
                        for (int i = 0; i < Ratios.Length; i++)
                        {
                            tiles.Add(
                                new AspectRatio(
                                    aspectRatio: Ratios[i],
                                    child: new DecoratedBox {
                                        Fill = AdwAccentColors.Bg(accents[i % accents.Length]),
                                        Radius = AdwMetrics.CardRadius,
                                        Child = new Center(
                                            new Label(
                                                text: $"{i + 1}",
                                                style: AdwTypography.Title2,
                                                color: Color.White
                                            )
                                        ),
                                    }
                                )
                            );
                        }

                        return new AdwResponsiveGrid(tiles) { MinColumnWidth = _minColumn.Value };
                    }
                ),
            },
        };
    }
}
