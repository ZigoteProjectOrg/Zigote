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
            "The Scale",
            "libadwaita's style classes, converted from points and rendered in Inter."
        );
        foreach (var (name, style, note) in Specimens())
            scale.Rows.Add(
                new AdwActionRow(name, note) {
                    Suffixes = {
                        new Label("Aa", style, theme.OnBackground) { MaxLines = 1 },
                    },
                }
            );

        return new GalleryPage(
            "Typography",
            "One ramp, six steps and a monospace face — the whole type system of a GNOME app.",
            MaterialIcons.FormatSize
        ) {
            ClampWidth = 680f,
            Children = {
                Demo.Titled(
                    "Specimens",
                    null,
                    Demo.Stage(
                        new Column(
                            spacing: Spacing.Md,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Start
                        ) {
                            Children = {
                                new Label("Title 1", AdwTypography.Title1, theme.OnBackground),
                                new Label("Title 2", AdwTypography.Title2, theme.OnBackground),
                                new Label("Title 3", AdwTypography.Title3, theme.OnBackground),
                                new Label("Title 4", AdwTypography.Title4, theme.OnBackground),
                                new Label("Heading", AdwTypography.Heading, theme.OnBackground),
                                new Label("Body", AdwTypography.Body, theme.OnBackground),
                                new Label(
                                    "Caption heading",
                                    AdwTypography.CaptionHeading,
                                    theme.TextSecondary
                                ),
                                new Label("Caption", AdwTypography.Caption, theme.TextSecondary),
                                new Label(
                                    "Monospace 0123",
                                    AdwTypography.Monospace,
                                    theme.OnBackground
                                ),
                            },
                        },
                        Spacing.Lg
                    )
                ),
                scale,
                Demo.Titled(
                    "Behaviour",
                    "Wrapping, a hard one-line limit, and centring.",
                    Demo.Stage(
                        new Column(
                            spacing: Spacing.Lg,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                new Label(Paragraph, AdwTypography.Body, theme.OnBackground),
                                new Label(Paragraph, AdwTypography.Body, theme.TextSecondary) {
                                    MaxLines = 1,
                                    Overflow = TextOverflow.Ellipsis,
                                },
                                new Label("Centred", AdwTypography.Heading, theme.OnBackground) {
                                    Align = TextAlign.Center,
                                },
                            },
                        },
                        Spacing.Lg
                    )
                ),
                Demo.Titled(
                    "Selectable",
                    "Drag across it — the same text, selectable and copyable.",
                    Demo.Stage(
                        new SelectableText(Paragraph),
                        Spacing.Lg
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
