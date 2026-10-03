using Zigote.UI.Material;
using Zigote.UI.Semantics;

namespace AdwaitaGallery.Pages;

public sealed partial class LiquidGlassPage
{
    // The article card and the chrome riding it — a hero card, so rounder than a boxed list,
    // and a fixed-height viewport so its content has somewhere to scroll under the glass.
    private const float ArticleRadius = 24f;
    private const float ArticlePadding = 20f;
    private const float ArticleHeight = 480f;

    // The picture fills the card at rest, so the chrome starts out over it and its bottom edge
    // sweeps up through the lens on the first scroll, the headline right behind — shorter than
    // the card less its padding and neither could ever reach the chrome's band.
    private const float ArticleImageHeight = ArticleHeight - ArticlePadding;
    private const float ToolbarHeight = 56f;
    private const float BodySize = 15f;
    private const float BodyLineHeight = 1.45f;

    // A tab cell: wide enough for a 24px glyph with the accent pill's air around it, narrow
    // enough that four of them and the search circle fit the card at the Adwaita clamp.
    private const float TabWidth = 64f;

    // The selected tab's pill sits this far inside the capsule, so its radius is Inner(r, inset)
    // and the two curves stay concentric.
    private const float PillInset = 6f;

    // The tab bar folds away once the article has scrolled this far, and only while the reader
    // is still heading down: a wheel notch either way is what flips it (iOS 26's minimise
    // behaviour), with two pixels of slack so layout jitter never flickers it.
    private const float MinimiseAfter = 24f;
    private const float MinimiseSlack = 2f;
    private const float AlwaysExpandedAbove = 8f;

    private static readonly string[] TabNames = ["Home", "Browse", "Library", "Profile"];

    private static readonly string[] TabIcons = [
        MaterialIcons.Home,
        MaterialIcons.Explore,
        MaterialIcons.MyLibraryMusic,
        MaterialIcons.Person,
    ];

    private static readonly string[] Families = ["Auto", "Light", "Dark", "Accent"];
    private static readonly string[] Shapes = ["Capsule", "Card", "Square"];

    // The material lab's dials, on the page so a theme flip keeps them and the readout can watch
    // them. The three sliders write the panes' live setters directly; the four choices below
    // change what a pane contains, so they rebuild the chrome through one Watch.
    private readonly Signal<float> _tinted = new(0.5f);
    private readonly Signal<float> _elevation = new(4f);
    private readonly Signal<float> _bevel = new(0.25f);
    private readonly Signal<int> _chrome = new(0);
    private readonly Signal<int> _family = new(0);
    private readonly Signal<int> _shape = new(0);
    private readonly Signal<bool> _glass = new(true);
    private readonly Signal<int> _tab = new(0);
    private readonly Signal<bool> _minimised = new(false);

    // Whatever the current chrome is made of; the sliders write into these without a rebuild.
    private readonly List<LiquidPane> _panes = [];
    private float _lastScroll;

    // ── Scene 1: chrome over an article ───────────────────────────────────────

    /// <summary>
    ///     The reference scene: a card that is a scroll viewport — a full-bleed picture, a
    ///     headline and the copy — with the chrome floating over its foot, and under it the
    ///     material lab: a boxed list whose every row changes the glass live. The content scrolls
    ///     <i>under</i> the chrome, which is the point: drag the picture's edge under the lens and
    ///     the rim bends it, park the headline under it and clear shows the letters through while
    ///     tinted covers them. A lens with nothing under it shows nothing.
    /// </summary>
    private Widget Article()
    {
        var theme = _theme;
        var palette = AdwPalette.For(theme);
        var art = ArtSource.Showcase[2];

        var body = new TextStyle(Size: BodySize, LineHeight: BodyLineHeight);
        var content = new SingleChildScrollView {
            Child = new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    // Full bleed: the card's clip rounds its top corners, so no radius of its own.
                    new SizedBox(
                        height: ArticleImageHeight,
                        child: Backdrop(art: art, radius: 0f, maxDecode: 1024)
                    ),
                    new Padding(
                        padding: EdgeInsets.All(ArticlePadding),
                        child: new Column(
                            crossAxisAlignment: CrossAxisAlignment.Stretch,
                            mainAxisSize: MainAxisSize.Min
                        ) {
                            Children = {
                                new Label(
                                    text: "Ink and Light",
                                    style: AdwTypography.Title1,
                                    color: palette.CardFg
                                ),
                                new SizedBox(height: 10f),
                                new Label(
                                    text:
                                    $"Drawn by {art.Artist}, served by nekos.best. The chrome "
                                    + "floating over this card is navigation, so it is glass; "
                                    + "the card is content, so it stays flat — and that "
                                    + "flatness is what the lens has to refract.",
                                    style: body,
                                    color: palette.CardFg
                                ),
                                new SizedBox(height: 12f),
                                new Label(
                                    text:
                                    "Scroll the picture's edge under the chrome and watch the "
                                    + "rim bend it. Park the headline under it: clear shows the "
                                    + "letters through the lens, tinted covers them. Then dial "
                                    + "the lab below — every row changes the glass you are "
                                    + "looking at.",
                                    style: body,
                                    color: palette.CardFg
                                ),
                                new SizedBox(height: 12f),
                                new Label(
                                    text:
                                    "Switch the chrome to tabs and keep scrolling: the tab bar "
                                    + "folds down to the selected tab while you read on, and "
                                    + "opens again the moment you head back up.",
                                    style: body,
                                    color: palette.CardFg
                                ),
                                // The last line must be able to clear the chrome.
                                new SizedBox(height: ToolbarHeight + ArticlePadding),
                            },
                        }
                    ),
                },
            },
        };

        // A page rebuild (theme, accent) recreates the viewport at offset zero; the fold must
        // not outlive the scroll position that caused it. Written before the chrome Watch below
        // exists, so nothing rebuilds twice.
        _lastScroll = 0f;
        if (_minimised.Peek()) _minimised.Value = false;

        // Direction, with slack: a notch down past the threshold folds the tab bar, a notch up
        // or the top of the article opens it. Movement under the slack is ignored without
        // moving the reference point, so a slow drift still adds up to a notch. The signal is
        // only written on a change, and the Watch reading it defers its swap out of this layout
        // pass (App.OnScrolled contract).
        content.OnScrolled = (y, _) =>
        {
            float dy = y - _lastScroll;
            if (MathF.Abs(dy) < MinimiseSlack && y >= AlwaysExpandedAbove) return;
            _lastScroll = y;
            // The signal's own setter drops the write when nothing changed.
            _minimised.Value = dy > 0f && y > MinimiseAfter;
        };

        var card = new DecoratedBox {
            Fill = palette.CardBg,
            Radius = ArticleRadius,
            BorderColor = palette.CardShade,
            BorderWidth = 1f,
            Child = new ClipRRect(
                radius: ArticleRadius,
                child: new SizedBox(
                    height: ArticleHeight,
                    child: new Stack {
                        Children = {
                            content,
                            new Positioned(
                                left: ArticlePadding,
                                right: ArticlePadding,
                                bottom: ArticlePadding,
                                // The chrome is beside the viewport, not in it, so a wheel over
                                // the glass has to be handed to the article on purpose. What
                                // the chrome is made of — search or tabs, glass or flat, which
                                // family and shape — is the one Watch; the sliders never
                                // touch it.
                                child: new ScrollsWith(
                                    target: content,
                                    child: new Watch(Chrome)
                                )
                            ),
                        },
                    }
                )
            ),
        };

        return new Column(
            spacing: Spacing.Lg,
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                card,
                MaterialLab(theme),
                new Align(
                    alignment: Alignment.Center,
                    child: new Watch(() => Demo.Value(
                            $"tinted {_tinted.Value:0.00} · float {_elevation.Value:0} px · "
                            + $"rim {_bevel.Value:0.00} · "
                            + $"{Shapes[_shape.Value].ToLowerInvariant()} · "
                            + $"{Families[_family.Value].ToLowerInvariant()}"
                        )
                    )
                ) { HeightFactor = 1f },
                Demo.Caption(
                    "Clear is more transparent; tinted increases opacity. The rim is a hairline "
                    + "on purpose — widen it and the capsule frosts over."
                ),
            },
        };
    }

    // ── The material lab ──────────────────────────────────────────────────────

    /// <summary>
    ///     The boxed list under the card. The three sliders write straight into the current
    ///     panes — the material moves, the widget tree does not, and the gel state survives
    ///     every drag; the choices set a signal the chrome Watch reads.
    /// </summary>
    private Widget MaterialLab(ThemeData theme)
    {
        return Demo.Group(
            title: "Material",
            description: null,
            new AdwActionRow(title: "Chrome", subtitle: "What floats over the article") {
                Suffixes = {
                    new AdwToggleGroup(
                        labels: ["Search", "Tabs"],
                        active: _chrome.Peek(),
                        onActive: i => _chrome.Value = i
                    ),
                },
            },
            new AdwActionRow(title: "Clear → Tinted") {
                Suffixes = {
                    new IconGlyph(
                        glyph: MaterialIcons.FilterNone,
                        size: 22f,
                        color: theme.TextSecondary
                    ),
                    new SizedBox(
                        width: 180f,
                        child: new AdwSlider(
                            value: _tinted.Peek(),
                            onChanged: v =>
                            {
                                _tinted.Value = v;
                                foreach (var pane in _panes) pane.Tinted = v;
                            }
                        ) { SemanticsLabel = "Clear to tinted" }
                    ),
                    // The nearest this font has to Apple's square.filled.on.square: the same
                    // stacked squares as FilterNone, with the front one filled.
                    new IconGlyph(
                        glyph: MaterialIcons.AutoAwesomeMotion,
                        size: 22f,
                        color: theme.TextSecondary
                    ),
                },
            },
            new AdwActionRow(title: "Float", subtitle: "Shadow under the chrome") {
                Suffixes = {
                    new SizedBox(
                        width: 180f,
                        child: new AdwSlider(
                            value: _elevation.Peek(),
                            min: 0f,
                            max: 12f,
                            onChanged: v =>
                            {
                                _elevation.Value = v;
                                foreach (var pane in _panes) pane.Elevation = v;
                            }
                        ) { SemanticsLabel = "Float" }
                    ),
                },
            },
            new AdwActionRow(title: "Rim", subtitle: "Hairline is right; wider is a frosted ring") {
                Suffixes = {
                    new SizedBox(
                        width: 180f,
                        child: new AdwSlider(
                            value: _bevel.Peek(),
                            min: 0.1f,
                            // The pane caps its bevel at a tenth of the shorter side — 5.6px on
                            // this 56px chrome — so past this point the dial would move nothing.
                            max: ToolbarHeight * 0.1f / theme.GlassThickness,
                            onChanged: v =>
                            {
                                _bevel.Value = v;
                                foreach (var pane in _panes) pane.Bevel = v;
                            }
                        ) { SemanticsLabel = "Rim" }
                    ),
                },
            },
            new AdwActionRow(title: "Family", subtitle: "The scrim's tone, and the ink with it") {
                Suffixes = {
                    new AdwToggleGroup(
                        labels: Families,
                        active: _family.Peek(),
                        onActive: i => _family.Value = i
                    ),
                },
            },
            new AdwActionRow(title: "Shape") {
                Suffixes = {
                    new AdwToggleGroup(
                        labels: Shapes,
                        active: _shape.Peek(),
                        onActive: i => _shape.Value = i
                    ),
                },
            },
            new AdwSwitchRow(
                title: "Glass",
                subtitle: "Off is the same structure the way Adwaita draws it",
                value: _glass.Peek(),
                onChanged: v => _glass.Value = v
            )
        );
    }

    // ── The chrome ────────────────────────────────────────────────────────────

    /// <summary>
    ///     What floats over the article's foot, built for the current choices: a menu circle and
    ///     a search capsule, or Apple's tab bar beside a search circle — as glass in one group,
    ///     or as the flat Adwaita widgets that make the same structure. Reads only the choice
    ///     signals: the tab and the fold are handled inside the panes, so they never rebuild one.
    /// </summary>
    private Widget Chrome()
    {
        bool tabs = _chrome.Value == 1;
        bool glass = _glass.Value;
        int family = _family.Value;
        float radius = _shape.Value switch { 0 => AdwMetrics.Pill, 1 => 16f, _ => 6f };
        _panes.Clear();

        if (!glass) return tabs ? FlatTabs() : FlatSearch();

        // The family: the theme's own haze, or a full scrim forced to one tone — Apple's tint,
        // which "increases opacity" — and ink to match, since the content takes its colour from
        // the glass, not the page.
        (Color? tint, Color ink) = family switch {
            1 => (Color.White.WithAlpha(0.9f), LatchedInk),
            2 => (Color.Rgb(r: 28, g: 28, b: 32).WithAlpha(0.9f), Color.White),
            3 => (_theme.Accent.WithAlpha(0.75f), Color.White),
            _ => ((Color?)null, LiquidPane.OnGlass(_theme)),
        };
        Color inkMuted = family == 0 ? LiquidPane.OnGlassMuted(_theme) : ink.WithAlpha(0.72f);

        var row = new Row(spacing: 12f, crossAxisAlignment: CrossAxisAlignment.Center);
        if (tabs)
        {
            // Apple's pair: the tab bar, and a search circle of its own at the far end.
            var search = Pane(
                child: SizedBox.Square(
                    size: ToolbarHeight,
                    child: new Center {
                        Child = new IconGlyph(glyph: MaterialIcons.Search, size: 22f, color: ink),
                    }
                )
            );
            row.Children.Add(Pane(TabBar(ink: ink, radius: radius)));
            row.Children.Add(new Spacer());
            row.Children.Add(
                LiquidPane.Interactive(pane: search, onPressed: () => { }, semantics: "Search")
            );
        }
        else
        {
            var menu = Pane(
                child: SizedBox.Square(
                    size: ToolbarHeight,
                    child: new Center {
                        Child = new IconGlyph(glyph: MaterialIcons.Menu, size: 24f, color: ink),
                    }
                )
            );
            var field = Pane(
                child: new SizedBox(
                    height: ToolbarHeight,
                    child: new Padding(
                        padding: EdgeInsets.Symmetric(horizontal: 20f, vertical: 0f),
                        child: new Row(
                            spacing: 10f,
                            crossAxisAlignment: CrossAxisAlignment.Center
                        ) {
                            Children = {
                                new IconGlyph(glyph: MaterialIcons.Search, size: 22f, color: ink),
                                new Label(
                                    text: "Search",
                                    style: new TextStyle(Size: 17f),
                                    color: inkMuted
                                ),
                            },
                        }
                    )
                )
            );
            row.Children.Add(
                LiquidPane.Interactive(pane: menu, onPressed: () => { }, semantics: "Menu")
            );
            row.Children.Add(
                new Expanded(
                    LiquidPane.Interactive(pane: field, onPressed: () => { }, semantics: "Search")
                )
            );
        }

        // One group for the chrome: every lens goes down first, one capture. The scroll viewport
        // is under the glass, not inside the group — a lens never repaints a patch of itself, so
        // scrolling recaptures the chrome's region each frame, and that is the honest cost of
        // chrome over moving content.
        return new LiquidGroup(row);

        // A pane at the lab's current dials. Registered so the sliders reach it afterwards.
        LiquidPane Pane(Widget child)
        {
            var pane = new LiquidPane {
                Child = child,
                Radius = radius,
                Tint = tint,
                Tinted = _tinted.Peek(),
                Elevation = _elevation.Peek(),
                Bevel = _bevel.Peek(),
            };
            _panes.Add(pane);
            return pane;
        }

        Widget FlatSearch()
        {
            var palette = AdwPalette.For(_theme);
            return new Row(spacing: 12f, crossAxisAlignment: CrossAxisAlignment.Center) {
                Children = {
                    new AdwButton(onPressed: () => { }) {
                        IconName = MaterialIcons.Menu,
                        Circular = true,
                    },
                    new Expanded(
                        new DecoratedBox {
                            Fill = palette.CardBg,
                            Radius = AdwMetrics.Pill,
                            BorderColor = palette.CardShade,
                            BorderWidth = 1f,
                            Child = new SizedBox(
                                height: AdwMetrics.ButtonHeight,
                                child: new Padding(
                                    padding: EdgeInsets.Symmetric(horizontal: 14f, vertical: 0f),
                                    child: new Row(
                                        spacing: 10f,
                                        crossAxisAlignment: CrossAxisAlignment.Center
                                    ) {
                                        Children = {
                                            new IconGlyph(
                                                glyph: MaterialIcons.Search,
                                                size: 18f,
                                                color: palette.CardFg
                                            ),
                                            new Label(
                                                text: "Search",
                                                style: AdwTypography.Body,
                                                color: _theme.TextSecondary
                                            ),
                                        },
                                    }
                                )
                            ),
                        }
                    ),
                },
            };
        }

        Widget FlatTabs()
        {
            return new Row(spacing: 12f, crossAxisAlignment: CrossAxisAlignment.Center) {
                Children = {
                    new AdwToggleGroup(
                        toggles: [
                            .. TabNames.Select((name, i) =>
                                new AdwToggle(IconName: TabIcons[i], Tooltip: name)
                            ),
                        ],
                        active: _tab.Peek(),
                        onActive: i => _tab.Value = i
                    ) { Round = true },
                    new Spacer(),
                    new AdwButton(onPressed: () => { }) {
                        IconName = MaterialIcons.Search,
                        Circular = true,
                    },
                },
            };
        }
    }

    /// <summary>
    ///     The tab bar's content: four glyphs over a flat accent pill that slides to the selected
    ///     one — the pill is a fill inside the glass, never glass of its own. Folded, only the
    ///     selected tab remains, its width eased; a press on it opens the bar again, as on iOS.
    /// </summary>
    private Widget TabBar(Color ink, float radius)
    {
        var accent = _theme.Accent;
        Color onAccent = AdwPalette.For(_theme).AccentFg;
        float pillRadius = LiquidPane.Inner(outer: radius, padding: PillInset);

        return new AnimatedSize(child: new Watch(() => _minimised.Value ? Folded() : Open()));

        // A cell less the inset on every side: on the inset layer, (layer − pill) / (n − 1) is
        // exactly one cell, so X = i / (n − 1) puts the pill's centre on cell i's centre.
        Widget Pill(int i) => new DecoratedBox {
            Fill = accent,
            Radius = pillRadius,
            Child = new SizedBox(
                width: TabWidth - (2f * PillInset),
                height: ToolbarHeight - (2f * PillInset),
                child: new Center {
                    Child = new IconGlyph(glyph: TabIcons[i], size: 24f, color: onAccent),
                }
            ),
        };

        Widget Folded()
        {
            int i = _tab.Peek();
            return new Pressable {
                Child = new SizedBox(
                    width: TabWidth,
                    height: ToolbarHeight,
                    child: new Center { Child = Pill(i) }
                ),
                OnPressed = () => _minimised.Value = false,
                Role = SemanticsRole.Tab,
                SelectedState = true,
                SemanticsLabel = TabNames[i],
                FocusRadius = pillRadius,
            };
        }

        Widget Open()
        {
            int n = TabNames.Length;
            // The pill rides its own layer under the glyphs (see Pill for the geometry). The
            // glyph row is the only thing a tab press rebuilds; the pill just slides.
            var slide = new AnimatedAlign(
                alignment: new Alignment(X: _tab.Peek() / (n - 1f), Y: 0.5f),
                child: new DecoratedBox {
                    Fill = accent,
                    Radius = pillRadius,
                    Child = new SizedBox(
                        width: TabWidth - (2f * PillInset),
                        height: ToolbarHeight - (2f * PillInset)
                    ),
                }
            );
            var glyphs = new Watch(() =>
            {
                int selected = _tab.Value;
                var row = new Row(mainAxisSize: MainAxisSize.Min);
                for (int i = 0; i < n; i++)
                {
                    int index = i;
                    row.Children.Add(
                        new Pressable {
                            Child = new SizedBox(
                                width: TabWidth,
                                height: ToolbarHeight,
                                child: new Center {
                                    Child = new IconGlyph(
                                        glyph: TabIcons[i],
                                        size: 24f,
                                        color: i == selected ? onAccent : ink
                                    ),
                                }
                            ),
                            OnPressed = () =>
                            {
                                _tab.Value = index;
                                slide.Alignment = new Alignment(X: index / (n - 1f), Y: 0.5f);
                            },
                            Role = SemanticsRole.Tab,
                            SelectedState = i == selected,
                            SemanticsLabel = TabNames[i],
                            FocusRadius = pillRadius,
                        }
                    );
                }

                return row;
            });

            return new SizedBox(
                width: TabWidth * n,
                height: ToolbarHeight,
                child: new Stack {
                    Children = {
                        new Padding(padding: EdgeInsets.All(PillInset), child: slide),
                        glyphs,
                    },
                }
            );
        }
    }
}
