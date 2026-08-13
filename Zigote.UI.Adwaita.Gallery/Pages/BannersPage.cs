namespace AdwaitaGallery.Pages;

/// <summary>
///     Banner — the bar that drops in above the content when something needs an answer, with the
///     controls that shape it underneath.
/// </summary>
public sealed class BannersPage : ComposedWidget
{
    private const string DefaultButton = "Network Settings";

    private readonly AdwBanner _banner = new(
        title: "Metered connection — updates paused",
        buttonLabel: DefaultButton
    );

    private string _buttonLabel = DefaultButton;
    private bool _showButton = true;

    protected override Widget Build(BuildContext context)
    {
        var host = GalleryHost.Of(context);
        _banner.OnButtonClicked = () => host.Toast("Banner action");

        // The banner pins to the top of the pane; the scrolling page takes the rest.
        return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
            Children = {
                _banner,
                new Expanded(
                    new GalleryPage(
                        title: "Banner",
                        description:
                        "A bar with contextual information, revealed and dismissed in place.",
                        iconName: MaterialIcons.Campaign
                    ) {
                        Children = {
                            Demo.Group(
                                title: "This Banner",
                                description: "Every row here drives the bar above.",
                                new AdwSwitchRow(
                                    title: "Revealed",
                                    subtitle: "Slides in and out rather than appearing",
                                    value: true,
                                    onChanged: on => _banner.Revealed = on
                                ),
                                new AdwEntryRow(
                                    title: "Title",
                                    text: _banner.Title,
                                    onChanged: s => _banner.Title = s
                                ),
                                new AdwEntryRow(
                                    title: "Button",
                                    text: DefaultButton,
                                    onChanged: s =>
                                    {
                                        _buttonLabel = s;
                                        ApplyButton();
                                    }
                                ),
                                new AdwSwitchRow(
                                    title: "Show Button",
                                    value: true,
                                    onChanged: on =>
                                    {
                                        _showButton = on;
                                        ApplyButton();
                                    }
                                )
                            ),
                            Demo.Group(
                                title: "When to Use One",
                                description:
                                "A banner is for state the user can act on — not for confirmation.",
                                new AdwActionRow(
                                    title: "Use a banner",
                                    subtitle: "Offline, metered, unsaved changes, an update waiting"
                                ) { IconName = MaterialIcons.Campaign },
                                new AdwActionRow(
                                    title: "Use a toast",
                                    subtitle: "Something that already happened and needs no answer"
                                ) {
                                    IconName = MaterialIcons.Notifications,
                                    ShowChevron = true,
                                    OnActivated = () => host.Open("Toasts"),
                                },
                                new AdwActionRow(
                                    title: "Use an alert dialog",
                                    subtitle: "A decision that has to be made before anything else"
                                ) {
                                    IconName = MaterialIcons.WebAsset,
                                    ShowChevron = true,
                                    OnActivated = () => host.Open("Alert Dialogs"),
                                }
                            ),
                        },
                    }
                ),
            },
        };
    }

    private void ApplyButton() => _banner.ButtonLabel = _showButton ? _buttonLabel : null;
}
