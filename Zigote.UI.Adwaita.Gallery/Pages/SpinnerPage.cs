namespace AdwaitaGallery.Pages;

/// <summary>
///     Spinner — the indeterminate Adwaita spinner at the sizes it is used at, and inside the
///     things that wait.
/// </summary>
public sealed class SpinnerPage : StatelessWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return new GalleryPage(
            "Spinner",
            "For a wait with no total to divide by. The arc grows and shrinks as it turns.",
            ""
        ) {
            Children = {
                Demo.Stage(
                    new Column(
                        spacing: Spacing.Lg,
                        mainAxisSize: MainAxisSize.Min,
                        crossAxisAlignment: CrossAxisAlignment.Center
                    ) {
                        Children = {
                            new AdwSpinner(64f),
                            Demo.Caption("64 px — the status-page size"),
                        },
                    },
                    Spacing.Xxl
                ),
                Demo.Titled(
                    "Sizes",
                    "It reads the same from a row suffix to a hero.",
                    Demo.Stage(
                        new Row(
                            spacing: Spacing.Xxl,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Center
                        ) {
                            Children = {
                                new AdwSpinner(16f),
                                new AdwSpinner(24f),
                                new AdwSpinner(32f),
                                new AdwSpinner(48f),
                            },
                        }
                    )
                ),
                Demo.Group(
                    "In Place",
                    "Where a spinner actually turns up in an app.",
                    new AdwActionRow("Checking for updates", "In a row suffix") {
                        Suffixes = { new AdwSpinner(16f) },
                    },
                    new AdwActionRow("Signing in", "Next to a label") {
                        Suffixes = {
                            new Row(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    new AdwSpinner(16f),
                                    new Label("Working…", AdwTypography.Body, theme.TextSecondary),
                                },
                            },
                        },
                    }
                ),
                Demo.Titled(
                    "As a Status Page",
                    "The pattern for a view that has nothing to show yet.",
                    Demo.Stage(
                        new Column(
                            spacing: Spacing.Md,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Center
                        ) {
                            Children = {
                                new AdwSpinner(48f),
                                new Label(
                                    "Loading Library",
                                    AdwTypography.Title3,
                                    theme.OnBackground
                                ) { Align = TextAlign.Center },
                                new Label(
                                    "This takes a moment the first time",
                                    AdwTypography.Body,
                                    theme.TextSecondary
                                ) { Align = TextAlign.Center },
                            },
                        }
                    )
                ),
            },
        };
    }
}