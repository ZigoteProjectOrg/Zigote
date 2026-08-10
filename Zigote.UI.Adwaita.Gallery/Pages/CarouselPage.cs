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
        _pages = [.. ArtSource.Showcase.Select(piece => new ArtImage(piece, MaxDim))];
        _carousel = new AdwCarousel(_pages.Select(page => Framed(page)));
        _previous = TurnButton(MaterialIcons.ChevronLeft, -1);
        _next = TurnButton(MaterialIcons.ChevronRight, 1);

        _stage = new Stack {
            Children = {
                _carousel,
                new Align(
                    Alignment.CenterLeft,
                    new Padding(EdgeInsets.Only(Spacing.Md), _previous)
                ),
                new Align(
                    Alignment.CenterRight,
                    new Padding(EdgeInsets.Only(right: Spacing.Md), _next)
                ),
                // Expand and retry have to live up here too: a tap inside an interactive carousel
                // never reaches the page it lands on.
                new Align(
                    Alignment.TopRight,
                    new Padding(
                        EdgeInsets.Only(top: Spacing.Lg, right: Spacing.Lg),
                        new AdwButton(
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
                            Children = { new Expanded(_stage), Indicators(false) },
                        }
                        : new Row(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                            Children = { new Expanded(_stage), Indicators(true) },
                        }
                    )
                ),
                Options(),
            },
        };
    }

    /// <summary>The carousel gives each page the full box; the margin is what separates the cards.</summary>
    private static Widget Framed(Widget page)
    {
        return new Padding(EdgeInsets.All(Spacing.Md), page);
    }

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
            Alignment.BottomCenter,
            new Padding(
                EdgeInsets.Only(bottom: Spacing.Xxl),
                new AdwButton("Try Again", page.Reload) {
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
            EdgeInsets.All(Spacing.Sm),
            new Align(Alignment.Center, row) {
                WidthFactor = beside ? 1f : null,
                HeightFactor = beside ? null : 1f,
            }
        );
    }

    private Widget Options()
    {
        return new Padding(
            EdgeInsets.Only(Spacing.Lg, 0f, Spacing.Lg, Spacing.Lg),
            Demo.Bar(
                Labelled(
                    "Indicators",
                    new AdwToggleGroup(["Dots", "Lines"], 0, i => _indicators.Value = i)
                ),
                Labelled(
                    "Position",
                    new AdwToggleGroup(["Below", "Beside"], 0, i => _placement.Value = i)
                ),
                // Off is what libadwaita's interactive:false does: the indicators and the turn
                // buttons still page it, but the surface stops answering drags and wheels.
                Labelled(
                    "Drag & Scroll",
                    new AdwSwitch(true, on => _carousel.Interactive = on)
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
            Children = { Demo.Caption(label), control },
        };
    }
}
