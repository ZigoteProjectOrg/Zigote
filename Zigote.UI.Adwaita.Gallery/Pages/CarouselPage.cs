namespace AdwaitaGallery.Pages;

/// <summary>Carousel — an AdwCarousel with dots/lines indicators and its options page.</summary>
public sealed class CarouselPage : ComposedWidget
{
    // 0 = Horizontal, 1 = Vertical: as in the demo, the indicators sit across the carousel's axis.
    private readonly Signal<int> _orientation = new(0);
    private readonly Signal<int> _indicators = new(0);
    private readonly Signal<int> _position = new(0);
    private readonly AdwCarousel _carousel;

    public CarouselPage()
    {
        // ponytail: Interactive = false keeps the option rows and the button on the pages
        // clickable (an interactive AdwCarousel claims the whole pointer), so paging happens
        // through the indicators; a gesture arena in AdwCarousel is what a full version needs.
        _carousel = new AdwCarousel(
            new AdwStatusPage {
                IconName = MaterialIcons.ViewCarousel,
                Title = "Carousel",
                Description = "A widget for paginated scrolling",
            },
            OptionsPage(),
            new AdwStatusPage {
                Title = "Another Page",
                // _carousel is assigned by the time the button can be pressed.
                Child = new AdwButton("Return to the First Page", () => _carousel!.Position = 0) {
                    Style = AdwButtonStyle.Suggested,
                    Pill = true,
                },
            }
        ) {
            Interactive = false,
        };
        _carousel.OnPageChanged = i => _position.Value = i;
    }

    protected override Widget Build(BuildContext context)
    {
        return new Watch(() => _orientation.Value == 0
            ? new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                Children = {
                    new Expanded(_carousel),
                    Indicators(),
                },
            }
            : new Row(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                Children = {
                    new Expanded(_carousel),
                    Indicators(),
                },
            }
        );
    }

    private Widget Indicators()
    {
        return new Padding(
            EdgeInsets.All(Spacing.Sm),
            new Center {
                Child = new Row(
                    spacing: Spacing.Md,
                    mainAxisSize: MainAxisSize.Min,
                    crossAxisAlignment: CrossAxisAlignment.Center
                ) {
                    Children = {
                        new Watch(() => _indicators.Value == 0
                            ? new AdwCarouselIndicatorDots(_carousel)
                            : new AdwCarouselIndicatorLines(_carousel)
                        ),
                        // OnPageChanged fires for indicator clicks, keys and flings alike.
                        new Watch(() => Demo.Value($"{_position.Value + 1}/{_carousel.Pages.Count}")
                        ),
                    },
                },
            }
        );
    }

    /// <summary>The carousel's second page: the clamped options group.</summary>
    private Widget OptionsPage()
    {
        // ponytail: AdwCarousel is horizontal-only, so Orientation only flips the indicator
        // placement; a vertical AdwCarousel is the full version.
        var group = new AdwPreferencesGroup {
            Rows = {
                new AdwComboRow(
                    "Orientation",
                    ["Horizontal", "Vertical"],
                    0,
                    i => _orientation.Value = i
                ),
                new AdwComboRow(
                    "Page Indicators",
                    ["Dots", "Lines"],
                    0,
                    i => _indicators.Value = i
                ),
                // Disabled rather than silently inert: the switches are here to name the two
                // libadwaita properties AdwCarousel has no equivalent of.
                new AdwSwitchRow(
                    "Scroll Wheel",
                    "No allow-scroll-wheel property — wheel paging follows Interactive",
                    true
                ) { Enabled = false },
                new AdwSwitchRow(
                    "Long Swipes",
                    "No allow-long-swipes property — a swipe always advances one page"
                ) { Enabled = false },
            },
        };

        // Center + ConstrainedBox rather than AdwClamp: the demo clamp is vertically centred.
        return new Center {
            Child = new ConstrainedBox(
                new Constraints(
                    0f,
                    400f,
                    0f,
                    float.PositiveInfinity
                ),
                new Padding(
                    EdgeInsets.Only(Spacing.Md, right: Spacing.Md, bottom: Spacing.Xxxl),
                    group
                )
            ),
        };
    }
}