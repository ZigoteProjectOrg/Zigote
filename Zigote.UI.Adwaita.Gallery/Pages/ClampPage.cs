namespace AdwaitaGallery.Pages;

/// <summary>
///     Clamp — the widget that keeps text at a readable measure in a window that is anything but.
///     The slider changes the cap live, and the stage reports the width the child actually got.
/// </summary>
public sealed class ClampPage : ComposedWidget
{
    private const string Sample =
        "Adwaita clamps its reading content because a line of text that runs the full width of a " +
        "1600 px window is miserable to read: the eye loses the start of the next line. The clamp " +
        "gives the child everything up to a maximum and centres what is left over.";

    private readonly Signal<float> _max = new(400f);

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return new GalleryPage(
            title: "Clamp",
            description: "A maximum width for content, centred in whatever space it is given.",
            iconName: MaterialIcons.FitScreen
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Group(
                    title: "Maximum Width",
                    description: "Every page in this gallery is inside one of these, at 600 px.",
                    new AdwActionRow("Cap") {
                        Suffixes = {
                            new SizedBox(
                                width: 220f,
                                child: new AdwSlider(
                                    value: 400f,
                                    min: 180f,
                                    max: 680f,
                                    onChanged: v => _max.Value = MathF.Round(v)
                                )
                            ),
                        },
                    },
                    new Watch(() => new AdwActionRow("Value") {
                            Suffixes = { Demo.Value($"{_max.Value:0} px") },
                        }
                    )
                ),
                new Watch(() => Demo.Stage(
                        child: new AdwClamp(
                            child: new Column(
                                spacing: Spacing.Md,
                                mainAxisSize: MainAxisSize.Min,
                                crossAxisAlignment: CrossAxisAlignment.Stretch
                            ) {
                                Children = {
                                    new Label(
                                        text: Sample,
                                        style: AdwTypography.Body,
                                        color: theme.OnBackground
                                    ),
                                    new AdwButton("A button inside the clamp") { Pill = true },
                                },
                            },
                            maximumSize: _max.Value
                        ),
                        padding: Spacing.Md
                    )
                ),
                Demo.Caption("Narrow the window: the clamp gives way before the content wraps."),
            },
        };
    }
}
