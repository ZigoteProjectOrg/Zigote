namespace AdwaitaGallery.Pages;

/// <summary>
///     Spinner — the indeterminate Adwaita spinner at the sizes it is used at, and inside the
///     things that wait.
/// </summary>
public sealed class SpinnerPage : ComposedWidget
{
    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return new GalleryPage(
            title: "Spinner",
            description:
            "For a wait with no total to divide by. The arc grows and shrinks as it turns.",
            iconName: ""
        ) {
            Children = {
                Demo.Stage(
                    child: new Column(
                        spacing: Spacing.Lg,
                        mainAxisSize: MainAxisSize.Min,
                        crossAxisAlignment: CrossAxisAlignment.Center
                    ) {
                        Children = {
                            new AdwSpinner(64f),
                            Demo.Caption("64 px — the status-page size"),
                        },
                    },
                    padding: Spacing.Xxl
                ),
                Demo.Titled(
                    title: "Sizes",
                    description: "It reads the same from a row suffix to a hero.",
                    child: Demo.Stage(
                        new Row(
                            spacing: Spacing.Xxl,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Center
                        ) {
                            Children = {
                                new AdwSpinner(16f),
                                new AdwSpinner(24f),
                                new AdwSpinner(),
                                new AdwSpinner(48f),
                            },
                        }
                    )
                ),
                Demo.Group(
                    title: "In Place",
                    description: "Where a spinner actually turns up in an app.",
                    new AdwActionRow(title: "Checking for updates", subtitle: "In a row suffix") {
                        Suffixes = { new AdwSpinner(16f) },
                    },
                    new AdwActionRow(title: "Signing in", subtitle: "Next to a label") {
                        Suffixes = {
                            new Row(spacing: Spacing.Sm, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    new AdwSpinner(16f),
                                    new Label(
                                        text: "Working…",
                                        style: AdwTypography.Body,
                                        color: theme.TextSecondary
                                    ),
                                },
                            },
                        },
                    }
                ),
                Demo.Titled(
                    title: "As a Status Page",
                    description: "The pattern for a view that has nothing to show yet.",
                    child: Demo.Stage(
                        new Column(
                            spacing: Spacing.Md,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Center
                        ) {
                            Children = {
                                new AdwSpinner(48f),
                                new Label(
                                    text: "Loading Library",
                                    style: AdwTypography.Title3,
                                    color: theme.OnBackground
                                ) { Align = TextAlign.Center },
                                new Label(
                                    text: "This takes a moment the first time",
                                    style: AdwTypography.Body,
                                    color: theme.TextSecondary
                                ) { Align = TextAlign.Center },
                            },
                        }
                    )
                ),
            },
        };
    }
}
