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
            "Wrap Box",
            "A row that becomes rows: chips, tags and filters that cannot know their own width.",
            MaterialIcons.WrapText
        ) {
            ClampWidth = 680f,
            Children = {
                Demo.Group(
                    "Children",
                    null,
                    new AdwSpinRow(
                        "Count",
                        null,
                        10,
                        1,
                        Tags.Length,
                        1,
                        v => _count.Value = v
                    ),
                    new AdwActionRow("Spacing") {
                        Suffixes = {
                            new SizedBox(
                                180f,
                                child: new AdwSlider(
                                    Spacing.Sm,
                                    0f,
                                    Spacing.Xl,
                                    v => _spacing.Value = MathF.Round(v)
                                )
                            ),
                        },
                    },
                    new Watch(() => new AdwActionRow("Gap") {
                            Suffixes = { Demo.Value($"{_spacing.Value:0} px") },
                        }
                    )
                ),
                new Watch(() => Demo.Stage(Chips(), Spacing.Md)),
                Demo.Caption(
                    "Resize the window: the runs reflow without any of the chips resizing."
                ),
            },
        };
    }

    private Widget Chips()
    {
        var gap = _spacing.Value;
        var wrap = new Wrap(spacing: gap, runSpacing: gap);
        for (var i = 0; i < (int)_count.Value; i++) wrap.Children.Add(new Chip(Tags[i]));
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
                EdgeInsets.Symmetric(Spacing.Md, Spacing.Xs),
                new Label(text, AdwTypography.Body, theme.OnBackground) { MaxLines = 1 }
            ),
        };
    }
}
