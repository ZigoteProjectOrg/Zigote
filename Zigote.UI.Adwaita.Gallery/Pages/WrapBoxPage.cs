namespace AdwaitaGallery.Pages;

/// <summary>
///     Wrap Box — children that flow onto a new line when the row runs out, with the spacing and the
///     count under live control.
/// </summary>
public sealed class WrapBoxPage : ComposedWidget
{
    private static readonly string[] Tags = [
        "adwaita", "gnome", "widgets", "retained", "signals", "layout", "dark mode", "accents",
        "toolbar", "sheet", "carousel", "clamp", "toast", "banner", "avatar", "spinner",
    ];

    private readonly Signal<double> _count = new(10);
    private readonly Signal<float> _spacing = new(Spacing.Sm);

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "Wrap Box",
            description:
            "A row that becomes rows: chips, tags and filters that cannot know their own width.",
            iconName: MaterialIcons.WrapText
        ) {
            ClampWidth = 680f,
            Children = {
                Demo.Group(
                    title: "Children",
                    description: null,
                    new AdwSpinRow(
                        title: "Count",
                        subtitle: null,
                        value: 10,
                        min: 1,
                        max: Tags.Length,
                        step: 1,
                        onChanged: v => _count.Value = v
                    ),
                    new AdwActionRow("Spacing") {
                        Suffixes = {
                            new SizedBox(
                                width: 180f,
                                child: new AdwSlider(
                                    value: Spacing.Sm,
                                    min: 0f,
                                    max: Spacing.Xl,
                                    onChanged: v => _spacing.Value = MathF.Round(v)
                                )
                            ),
                        },
                    },
                    new Watch(() => new AdwActionRow("Gap") {
                            Suffixes = { Demo.Value($"{_spacing.Value:0} px") },
                        }
                    )
                ),
                new Watch(() => Demo.Stage(child: Chips(), padding: Spacing.Md)),
                Demo.Caption(
                    "Resize the window: the runs reflow without any of the chips resizing."
                ),
            },
        };
    }

    private Widget Chips()
    {
        float gap = _spacing.Value;
        var wrap = new Wrap(spacing: gap, runSpacing: gap);
        for (int i = 0; i < (int)_count.Value; i++) wrap.Children.Add(new Chip(Tags[i]));
        return wrap;
    }
}

/// <summary>A pill-shaped tag — the thing wrap boxes are usually full of.</summary>
internal sealed class Chip(string text) : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        return new DecoratedBox {
            Fill = theme.Fill2,
            Radius = AdwMetrics.Pill,
            Child = new Padding(
                padding: EdgeInsets.Symmetric(horizontal: Spacing.Md, vertical: Spacing.Xs),
                child: new Label(text: text, style: AdwTypography.Body, color: theme.OnBackground) {
                    MaxLines = 1,
                }
            ),
        };
    }
}
