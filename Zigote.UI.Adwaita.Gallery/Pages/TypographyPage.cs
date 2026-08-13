namespace AdwaitaGallery.Pages;

/// <summary>
///     Typography — the Adwaita type scale as specimens, plus the text behaviours a real UI needs:
///     wrapping, ellipsis, alignment and selection.
/// </summary>
public sealed class TypographyPage : ComposedWidget
{
    private const string Paragraph =
        "The quick brown fox jumps over the lazy dog. Adwaita sets body text at 11 pt and builds " +
        "the rest of the ramp from it, so a heading is a weight and a step, never a guess.";

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        var scale = new AdwPreferencesGroup(
            title: "The Scale",
            description: "libadwaita's style classes, converted from points and rendered in Inter."
        );
        foreach ((string name, var style, string note) in Specimens())
        {
            scale.Rows.Add(
                new AdwActionRow(title: name, subtitle: note) {
                    Suffixes = {
                        new Label(text: "Aa", style: style, color: theme.OnBackground) {
                            MaxLines = 1,
                        },
                    },
                }
            );
        }

        return new GalleryPage(
            title: "Typography",
            description:
            "One ramp, six steps and a monospace face — the whole type system of a GNOME app.",
            iconName: MaterialIcons.FormatSize
        ) {
            ClampWidth = 680f,
            Children = {
                Demo.Titled(
                    title: "Specimens",
                    description: null,
                    child: Demo.Stage(
                        child: new Column(
                            spacing: Spacing.Md,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Start
                        ) {
                            Children = {
                                new Label(
                                    text: "Title 1",
                                    style: AdwTypography.Title1,
                                    color: theme.OnBackground
                                ),
                                new Label(
                                    text: "Title 2",
                                    style: AdwTypography.Title2,
                                    color: theme.OnBackground
                                ),
                                new Label(
                                    text: "Title 3",
                                    style: AdwTypography.Title3,
                                    color: theme.OnBackground
                                ),
                                new Label(
                                    text: "Title 4",
                                    style: AdwTypography.Title4,
                                    color: theme.OnBackground
                                ),
                                new Label(
                                    text: "Heading",
                                    style: AdwTypography.Heading,
                                    color: theme.OnBackground
                                ),
                                new Label(
                                    text: "Body",
                                    style: AdwTypography.Body,
                                    color: theme.OnBackground
                                ),
                                new Label(
                                    text: "Caption heading",
                                    style: AdwTypography.CaptionHeading,
                                    color: theme.TextSecondary
                                ),
                                new Label(
                                    text: "Caption",
                                    style: AdwTypography.Caption,
                                    color: theme.TextSecondary
                                ),
                                new Label(
                                    text: "Monospace 0123",
                                    style: AdwTypography.Monospace,
                                    color: theme.OnBackground
                                ),
                            },
                        },
                        padding: Spacing.Lg
                    )
                ),
                scale,
                Demo.Titled(
                    title: "Behaviour",
                    description: "Wrapping, a hard one-line limit, and centring.",
                    child: Demo.Stage(
                        child: new Column(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                new Label(
                                    text: Paragraph,
                                    style: AdwTypography.Body,
                                    color: theme.OnBackground
                                ),
                                new Label(
                                    text: Paragraph,
                                    style: AdwTypography.Body,
                                    color: theme.TextSecondary
                                ) {
                                    MaxLines = 1,
                                    Overflow = TextOverflow.Ellipsis,
                                },
                                new Label(
                                    text: "Centred",
                                    style: AdwTypography.Heading,
                                    color: theme.OnBackground
                                ) {
                                    Align = TextAlign.Center,
                                },
                            },
                        },
                        padding: Spacing.Lg
                    )
                ),
                Demo.Titled(
                    title: "Selectable",
                    description: "Drag across it — the same text, selectable and copyable.",
                    child: Demo.Stage(
                        child: new SelectableText(Paragraph),
                        padding: Spacing.Lg
                    )
                ),
            },
        };
    }

    private static (string Name, TextStyle Style, string Note)[] Specimens()
    {
        return [
            ("Title 1", AdwTypography.Title1, "Status pages and page heroes"),
            ("Title 2", AdwTypography.Title2, "Dialog headings"),
            ("Title 3", AdwTypography.Title3, "Section titles"),
            ("Title 4", AdwTypography.Title4, "Sub-sections"),
            ("Heading", AdwTypography.Heading, "Row titles and group headers"),
            ("Body", AdwTypography.Body, "The default size"),
            ("Caption", AdwTypography.Caption, "Subtitles and secondary detail"),
            ("Monospace", AdwTypography.Monospace, "Code, keys and numbers"),
        ];
    }
}
