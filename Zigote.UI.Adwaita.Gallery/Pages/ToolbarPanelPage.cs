namespace AdwaitaGallery.Pages;

/// <summary>
///     A toolbar strip and the floating panel a tool window is made of — the chrome of an editor
///     rather than of a document app.
/// </summary>
public sealed class ToolbarPanelPage : ComposedWidget
{
    private AdwPanel? _panel;

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return new GalleryPage(
            title: "Toolbar & Panel",
            description: "A strip of actions, and a floating tool window you can move and resize.",
            iconName: MaterialIcons.WebAsset
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Titled(
                    title: "Toolbar",
                    description:
                    "Leading actions pack at the start and trailing ones at the end. A strip " +
                    "wider than its bar scrolls sideways instead of clipping.",
                    child: Demo.Stage(
                        child: new AdwToolbar(
                            leading: [
                                Demo.IconButton(icon: MaterialIcons.Undo, onPressed: () => { }),
                                Demo.IconButton(icon: MaterialIcons.Redo, onPressed: () => { }),
                                new AdwSeparator(vertical: true, margin: 8f),
                                new AdwButton(label: "Frame all", onPressed: () => { }) {
                                    IconName = MaterialIcons.Fullscreen,
                                    Style = AdwButtonStyle.Flat,
                                },
                            ],
                            trailing: [
                                new AdwButton(label: "Run", onPressed: () => { }) {
                                    IconName = MaterialIcons.PlayArrow,
                                    Style = AdwButtonStyle.Suggested,
                                },
                            ]
                        ),
                        padding: 0f
                    )
                ),
                Demo.Titled(
                    title: "Floating Panel",
                    description:
                    "A panel floats over the window: drag it by its title, resize it from any " +
                    "edge or corner, and close it from its header.",
                    child: Demo.Specimen(
                        new AdwButton(label: "Open Panel", onPressed: () => Open(theme)) {
                            Style = AdwButtonStyle.Suggested,
                            Pill = true,
                        }
                    )
                ),
            },
        };
    }

    private void Open(ThemeData theme)
    {
        if (_panel is not null || App.Active is not { } app) return;
        _panel = new AdwPanel(
            app: app,
            title: "Properties",
            x: 640f,
            y: 420f,
            width: 320f,
            height: 260f
        ) {
            Content = new Padding(
                padding: EdgeInsets.All(Spacing.Lg),
                child: new Column(spacing: Spacing.Md, crossAxisAlignment: CrossAxisAlignment.Stretch) {
                    Children = {
                        new Label(text: "Opacity", style: AdwTypography.Caption, color: theme.TextSecondary),
                        new AdwSlider(value: 0.8f),
                        new Label(text: "Blend", style: AdwTypography.Caption, color: theme.TextSecondary),
                        new AdwDropDown(items: ["Normal", "Multiply", "Screen"]),
                    },
                }
            ),
            OnClose = () => _panel = null,
        };
        _panel.Show();
    }
}
