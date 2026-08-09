namespace AdwaitaGallery.Pages;

/// <summary>
///     Banner — the bar that drops in above the content when something needs an answer, with the
///     controls that shape it underneath.
/// </summary>
public sealed class BannersPage : ComposedWidget
{
    private const string DefaultButton = "Network Settings";

    private readonly AdwBanner _banner = new("Metered connection — updates paused", DefaultButton);
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
                        "Banner",
                        "A bar with contextual information, revealed and dismissed in place.",
                        MaterialIcons.Campaign
                    ) {
                        Children = {
                            Demo.Group(
                                "This Banner",
                                "Every row here drives the bar above.",
                                new AdwSwitchRow(
                                    "Revealed",
                                    "Slides in and out rather than appearing",
                                    true,
                                    on => _banner.Revealed = on
                                ),
                                new AdwEntryRow("Title", _banner.Title, s => _banner.Title = s),
                                new AdwEntryRow(
                                    "Button",
                                    DefaultButton,
                                    s =>
                                    {
                                        _buttonLabel = s;
                                        ApplyButton();
                                    }
                                ),
                                new AdwSwitchRow(
                                    "Show Button",
                                    value: true,
                                    onChanged: on =>
                                    {
                                        _showButton = on;
                                        ApplyButton();
                                    }
                                )
                            ),
                            Demo.Group(
                                "When to Use One",
                                "A banner is for state the user can act on — not for confirmation.",
                                new AdwActionRow(
                                    "Use a banner",
                                    "Offline, metered, unsaved changes, an update waiting"
                                ) { IconName = MaterialIcons.Campaign },
                                new AdwActionRow(
                                    "Use a toast",
                                    "Something that already happened and needs no answer"
                                ) {
                                    IconName = MaterialIcons.Notifications,
                                    ShowChevron = true,
                                    OnActivated = () => host.Open("Toasts"),
                                },
                                new AdwActionRow(
                                    "Use an alert dialog",
                                    "A decision that has to be made before anything else"
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

    private void ApplyButton()
    {
        _banner.ButtonLabel = _showButton ? _buttonLabel : null;
    }
}