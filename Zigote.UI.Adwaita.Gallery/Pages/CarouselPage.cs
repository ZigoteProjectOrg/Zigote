namespace AdwaitaGallery.Pages;

/// <summary>
///     Carousel — an <see cref="AdwCarousel" /> of network artwork you can drag, swipe, scroll or
///     turn with the buttons floated over it, under dot or line indicators.
///     <para>
///         The turn buttons are the point of the layout: an interactive carousel claims the pointer
///         so it can drag, which leaves nothing inside a page clickable. Controls that need clicks
///         go in a <see cref="Stack" /> <em>above</em> the carousel, where they are hit first and
///         every other pixel still drags.
///     </para>
/// </summary>
public sealed class CarouselPage : ComposedWidget
{
    // Big enough for the widest the page ever gets, small enough that eight of them are 32 MB of
    // GPU rather than 200: the engine box-downsamples during the decode, so the full 2500 px
    // buffer never reaches the GPU at all.
    private const uint MaxDim = 1024;

    private readonly AdwCarousel _carousel;
    private readonly Signal<int> _indicators = new(0);
    private readonly AdwButton _next;
    private readonly ArtImage[] _pages;
    private readonly Signal<int> _placement = new(0);
    private readonly Signal<int> _position = new(0);
    private readonly AdwButton _previous;
    private readonly Stack _stage;

    public CarouselPage()
    {
        _pages = [
            .. ArtSource.Showcase.Select(piece => new ArtImage(piece: piece, maxDim: MaxDim)),
        ];
        _carousel = new AdwCarousel(_pages.Select(page => Framed(page)));
        _previous = TurnButton(icon: MaterialIcons.ChevronLeft, delta: -1);
        _next = TurnButton(icon: MaterialIcons.ChevronRight, delta: 1);

        _stage = new Stack {
            Children = {
                _carousel,
                new Align(
                    alignment: Alignment.CenterLeft,
                    child: new Padding(padding: EdgeInsets.Only(Spacing.Md), child: _previous)
                ),
                new Align(
                    alignment: Alignment.CenterRight,
                    child: new Padding(padding: EdgeInsets.Only(right: Spacing.Md), child: _next)
                ),
                // Expand and retry have to live up here too: a tap inside an interactive carousel
                // never reaches the page it lands on.
                new Align(
                    alignment: Alignment.TopRight,
                    child: new Padding(
                        padding: EdgeInsets.Only(top: Spacing.Lg, right: Spacing.Lg),
                        child: new AdwButton(
                            onPressed: () => ArtViewer.Show(ArtSource.Showcase[_position.Peek()])
                        ) {
                            IconName = MaterialIcons.OpenInFull,
                            Circular = true,
                        }
                    )
                ),
                new Watch(Retry),
            },
        };

        _carousel.OnPageChanged = Settle;
        Settle(0);
    }

    protected override Widget Build(BuildContext context)
    {
        return new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
            Children = {
                new Expanded(
                    new Watch(() => _placement.Value == 0
                        ? new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                            Children = {
                                new Expanded(_stage),
                                Indicators(false),
                            },
                        }
                        : new Row(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                            Children = {
                                new Expanded(_stage),
                                Indicators(true),
                            },
                        }
                    )
                ),
                Options(),
            },
        };
    }

    /// <summary>The carousel gives each page the full box; the margin is what separates the cards.</summary>
    private static Widget Framed(Widget page) => new Padding(
        padding: EdgeInsets.All(Spacing.Md),
        child: page
    );

    private AdwButton TurnButton(string icon, int delta)
    {
        // Regular rather than Flat: a flat button over a photograph is invisible.
        return new AdwButton(onPressed: () => _carousel.Position += delta) {
            IconName = icon,
            Circular = true,
        };
    }

    private void Settle(int index)
    {
        _position.Value = index;
        _previous.Enabled = index > 0;
        _next.Enabled = index < _pages.Length - 1;
    }

    private Widget Retry()
    {
        var page = _pages[_position.Value];
        if (page.State.Value != ArtState.Failed) return SizedBox.Shrink();

        return new Align(
            alignment: Alignment.BottomCenter,
            child: new Padding(
                padding: EdgeInsets.Only(bottom: Spacing.Xxl),
                child: new AdwButton(label: "Try Again", onPressed: page.Reload) {
                    Style = AdwButtonStyle.Suggested,
                    Pill = true,
                }
            )
        );
    }

    private Widget Indicators(bool beside)
    {
        var row = new Row(
            spacing: Spacing.Md,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        ) {
            Children = {
                new Watch(() => _indicators.Value == 0
                    ? new AdwCarouselIndicatorDots(_carousel)
                    : new AdwCarouselIndicatorLines(_carousel)
                ),
                // Fires for a drag, a fling, a wheel notch, an arrow key and a dot click alike —
                // everything that can move the carousel goes through one hook.
                new Watch(() => Demo.Value($"{_position.Value + 1}/{_pages.Length}")),
            },
        };

        // A bare Center would fill the flex's main axis — it reports the constraint it was handed,
        // and non-flex children are measured first, so it would take the whole box and leave the
        // Expanded carousel nothing. The factor sizes this to the row on the axis that matters.
        return new Padding(
            padding: EdgeInsets.All(Spacing.Sm),
            child: new Align(alignment: Alignment.Center, child: row) {
                WidthFactor = beside ? 1f : null,
                HeightFactor = beside ? null : 1f,
            }
        );
    }

    private Widget Options()
    {
        return new Padding(
            padding: EdgeInsets.Only(
                left: Spacing.Lg,
                top: 0f,
                right: Spacing.Lg,
                bottom: Spacing.Lg
            ),
            child: Demo.Bar(
                Labelled(
                    label: "Indicators",
                    control: new AdwToggleGroup(
                        labels: ["Dots", "Lines"],
                        active: 0,
                        onActive: i => _indicators.Value = i
                    )
                ),
                Labelled(
                    label: "Position",
                    control: new AdwToggleGroup(
                        labels: ["Below", "Beside"],
                        active: 0,
                        onActive: i => _placement.Value = i
                    )
                ),
                // Off is what libadwaita's interactive:false does: the indicators and the turn
                // buttons still page it, but the surface stops answering drags and wheels.
                Labelled(
                    label: "Drag & Scroll",
                    control: new AdwSwitch(value: true, onChanged: on => _carousel.Interactive = on)
                )
            )
        );
    }

    private static Widget Labelled(string label, Widget control)
    {
        return new Row(
            spacing: Spacing.Sm,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Center
        ) {
            Children = {
                Demo.Caption(label),
                control,
            },
        };
    }
}
