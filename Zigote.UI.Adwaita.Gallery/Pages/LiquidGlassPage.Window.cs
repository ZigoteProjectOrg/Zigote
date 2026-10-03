using Zigote.UI.Material;

namespace AdwaitaGallery.Pages;

public sealed partial class LiquidGlassPage
{
    // A fixed-height window so the library has somewhere to scroll under the bars, and a hero
    // tall enough that the header starts out entirely over the picture.
    private const float WindowHeight = 420f;
    private const float WindowHeroHeight = 220f;
    private const float WindowThumbHeight = 150f;

    // Apple's floating-bar insets: the header pulled off the frame edge, the tab pill riding a
    // touch above the bottom. The header's radius is a card's, not the pill's — it holds a row.
    private const float WindowHeaderInset = 10f;
    private const float WindowHeaderRadius = 16f;
    private const float WindowPillBottom = 12f;
    private const float WindowPillPadding = 6f;

    // The pill's band plus a breath, so the last row can scroll clear of it.
    private const float WindowPillClearance = 72f;

    // The floating header's band: its inset plus the bar itself. The albums grid starts just
    // under it, so the band's lens is over the first row the moment the grid scrolls.
    private const float WindowAlbumsTop = 56f;

    private static readonly (string Title, string Artist, string Length)[] Songs = [
        ("Morning Light", "Aoi Sora", "3:42"),
        ("Paper Lanterns", "Kou", "4:05"),
        ("Still Water", "Bling", "2:58"),
        ("Neon Rain", "STARFOX1015", "3:21"),
        ("Glass Houses", "Benkyousiro_0", "5:12"),
        ("Long Shadows", "Aoi Sora", "3:37"),
        ("Half Moon", "Kou", "4:44"),
        ("Last Train Home", "Bling", "3:09"),
    ];

    // 0 is glass, 1 is flat. On the page, so a theme flip cannot switch the material back.
    private readonly Signal<int> _windowMaterial = new(0);

    // ── Scene 2: an app window ────────────────────────────────────────────────

    /// <summary>
    ///     The hybrid at app scale. GNOME's header bar and view switcher, made of glass and
    ///     floating over a library that scrolls under both — Apple's layout — and, one toggle
    ///     away, the same two bars the way Adwaita draws them: flat, framing the content. The
    ///     structure never changes; only what the bars are made of does.
    /// </summary>
    private Widget Window()
    {
        var palette = AdwPalette.For(_theme);
        return new Column(
            spacing: Spacing.Lg,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                Demo.Bar(
                    new AdwToggleGroup(
                        labels: ["Glass", "Flat"],
                        active: _windowMaterial.Peek(),
                        onActive: i => _windowMaterial.Value = i
                    )
                ),
                // An opaque window surface under everything: glass over the gallery's card
                // would read as glass over glass, and the lens needs something to refract.
                new DecoratedBox {
                    Fill = palette.WindowBg,
                    Radius = AdwMetrics.WindowRadius,
                    BorderColor = palette.CardShade,
                    BorderWidth = 1f,
                    Child = new ClipRRect(
                        radius: AdwMetrics.WindowRadius,
                        child: new SizedBox(
                            height: WindowHeight,
                            child: new Watch(() =>
                                _windowMaterial.Value == 0 ? GlassWindow() : FlatWindow()
                            )
                        )
                    ),
                },
                Demo.Caption(
                    "Both are the same structure: bars where GNOME puts them. What they are made "
                    + "of — and whether they float or frame — is the material's choice."
                ),
            },
        };
    }

    /// <summary>
    ///     Glass: the header floats over the hero picture and the switcher sits where iOS puts
    ///     its tab bar, both in one group — one capture for the pair. The viewport is a sibling
    ///     of the group, not inside it, so the wheel over either bar is handed across on purpose.
    /// </summary>
    private Widget GlassWindow()
    {
        var stack = Views(
            bottomClearance: WindowPillClearance,
            albumsTop: WindowAlbumsTop,
            fillSearch: true
        );
        var scroll = new SingleChildScrollView { Child = stack };

        // Flat: the bar's own titlebar fill and hairline would be a slab on the lens. The
        // structure is GNOME's; the material is the pane's.
        var header = LiquidPane.Regular(
            child: HeaderBar(stack: stack, flat: true),
            radius: WindowHeaderRadius,
            elevation: 4f
        );
        var switcher = LiquidPane.Regular(
            child: new Padding(
                padding: EdgeInsets.All(WindowPillPadding),
                child: new AdwInlineViewSwitcher(stack) { Round = true }
            ),
            radius: AdwMetrics.Pill,
            elevation: 4f
        );

        return new Stack {
            Children = {
                scroll,
                new LiquidGroup(
                    new Stack {
                        Children = {
                            new Positioned(
                                top: WindowHeaderInset,
                                left: WindowHeaderInset,
                                right: WindowHeaderInset,
                                child: new ScrollsWith(target: scroll, child: header)
                            ),
                            new Positioned(
                                bottom: WindowPillBottom,
                                left: 0f,
                                right: 0f,
                                child: new Center {
                                    Child = new ScrollsWith(target: scroll, child: switcher),
                                }
                            ),
                        },
                    }
                ),
            },
        };
    }

    /// <summary>The honest GNOME framing: a raised header on top, the switcher bar at the foot,
    ///     the library between them and under neither.</summary>
    private Widget FlatWindow()
    {
        // No floating bars to clear: the toolbar view frames the content, so the views keep
        // only their ordinary breathing room.
        var stack = Views(bottomClearance: Spacing.Md, albumsTop: Spacing.Md, fillSearch: false);
        return new AdwToolbarView(new SingleChildScrollView { Child = stack }) {
            TopBars = { HeaderBar(stack: stack, flat: false) },
            BottomBars = { new AdwViewSwitcherBar(stack) },
            RaisedTopBar = true,
        };
    }

    /// <summary>The one header bar both materials share: a menu, the title, and a search button
    ///     that jumps the stack to its search view.</summary>
    private static AdwHeaderBar HeaderBar(AdwViewStack stack, bool flat)
    {
        return new AdwHeaderBar {
            Flat = flat,
            Title = "Music",
            ShowStartWindowControls = false,
            ShowEndWindowControls = false,
            Start = { Demo.IconButton(icon: MaterialIcons.Menu, onPressed: () => { }) },
            End = {
                Demo.IconButton(
                    icon: MaterialIcons.Search,
                    onPressed: () => stack.VisibleName = "search"
                ),
            },
        };
    }

    /// <summary>
    ///     The three views. Built fresh per material — the stack is content, and a material
    ///     flip resetting the scroll is fine. Everything here is flat and opaque: it is what the
    ///     glass has to refract, so nothing in it may be glass. The floating-bar clearances are
    ///     the caller's: only the glass window has bars to scroll clear of.
    /// </summary>
    private AdwViewStack Views(float bottomClearance, float albumsTop, bool fillSearch)
    {
        var palette = AdwPalette.For(_theme);

        var library = new AdwPreferencesGroup();
        foreach (var (title, artist, length) in Songs)
        {
            library.Rows.Add(
                new AdwActionRow(title: title, subtitle: artist) {
                    IconName = MaterialIcons.MusicNote,
                    Suffixes = {
                        new Label(
                            text: length,
                            style: AdwTypography.Caption,
                            color: _theme.TextSecondary
                        ),
                    },
                }
            );
        }

        var albums = new Column(
            spacing: Spacing.Md,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        );
        for (int i = 1; i < 7; i += 2)
        {
            albums.Children.Add(
                new Row(spacing: Spacing.Md, crossAxisAlignment: CrossAxisAlignment.Start) {
                    Children = {
                        new Expanded(Album(ArtSource.Showcase[i])),
                        new Expanded(Album(ArtSource.Showcase[i + 1])),
                    },
                }
            );
        }

        return new AdwViewStack(
            new AdwViewStackPage(
                name: "library",
                title: "Library",
                child: new Column(
                    crossAxisAlignment: CrossAxisAlignment.Stretch,
                    mainAxisSize: MainAxisSize.Min
                ) {
                    Children = {
                        // Full bleed and no top inset: the header starts over the picture, and
                        // the first scroll drags the hero's edge up through its lens.
                        new SizedBox(
                            height: WindowHeroHeight,
                            child: Backdrop(art: ArtSource.Showcase[0], radius: 0f, maxDecode: 800)
                        ),
                        new Padding(
                            padding: EdgeInsets.Only(
                                left: Spacing.Md,
                                top: Spacing.Md,
                                right: Spacing.Md,
                                bottom: bottomClearance
                            ),
                            child: library
                        ),
                    },
                }
            ) { IconName = MaterialIcons.QueueMusic },
            new AdwViewStackPage(
                name: "albums",
                title: "Albums",
                child: new Padding(
                    // The grid opens right under the header's band; the pill floats over the last.
                    padding: EdgeInsets.Only(
                        left: Spacing.Md,
                        top: albumsTop,
                        right: Spacing.Md,
                        bottom: bottomClearance
                    ),
                    child: albums
                )
            ) { IconName = MaterialIcons.Album },
            new AdwViewStackPage(
                name: "search",
                title: "Search",
                // Glass floats over the whole frame, so the status page gets the frame's height
                // to centre in; the flat toolbar view keeps only what the bars leave, where the
                // natural height is what avoids a pointless scroll.
                child: fillSearch
                    ? new SizedBox(height: WindowHeight, child: SearchStatus())
                    : SearchStatus()
            ) { IconName = MaterialIcons.Search }
        );

        Widget Album(ArtPiece art) =>
            new Column(
                spacing: Spacing.Xs,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    // AsyncImage.Radius rounds only the placeholder; the decoded texture needs
                    // the clip for its corners.
                    new ClipRRect(
                        radius: AdwMetrics.CardRadius,
                        child: new SizedBox(
                            height: WindowThumbHeight,
                            child: Backdrop(art: art, radius: AdwMetrics.CardRadius, maxDecode: 400)
                        )
                    ),
                    new Label(
                        text: art.Artist,
                        style: AdwTypography.Caption,
                        color: palette.ViewFg
                    ) { MaxLines = 1, Overflow = TextOverflow.Ellipsis },
                },
            };
    }

    /// <summary>The empty search view, shared by both materials.</summary>
    private static Widget SearchStatus()
    {
        return new AdwStatusPage {
            IconName = MaterialIcons.Search,
            Title = "Search your library",
            Description = "Songs, albums and artists",
            Compact = true,
        };
    }
}
