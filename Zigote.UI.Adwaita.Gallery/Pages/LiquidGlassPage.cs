using Zigote.Http;
using Zigote.UI.Material;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Liquid Glass — the material study. Three scenes built from <see cref="LiquidPane" />: a
///     toolbar floating over an article, with the slider that blends its panes from clear to
///     tinted; then a control centre and a lock screen over photographs. The design system's
///     split in one page — Adwaita decides where things go, glass is what the floating layer is
///     made of.
///     <para>
///         Everything glass here is a real lens over the real picture. Each scene's panes sit in
///         one <see cref="LiquidGroup" />, so a screenful of glass costs one backdrop copy and one
///         render-pass break rather than one per pane — and this page is still an honest showing
///         of what that costs. The flat fills <i>inside</i> the panes (the toggle dots, the app
///         badges) follow the other rule: glass never stacks on glass.
///     </para>
/// </summary>
public sealed class LiquidGlassPage : ComposedWidget
{
    private const float PhoneW = 320f;
    private const float PhoneH = 640f;
    private const float PhoneRadius = 30f;

    // The article card and the toolbar riding it — a hero card, so rounder than a boxed list,
    // and a fixed-height viewport so its content has somewhere to scroll under the glass.
    private const float ArticleRadius = 24f;
    private const float ArticlePadding = 20f;
    private const float ArticleHeight = 480f;

    // The picture fills the card at rest, so the toolbar starts out over it and its bottom edge
    // sweeps up through the lens on the first scroll, the headline right behind — shorter than
    // the card less its padding and neither could ever reach the toolbar's band.
    private const float ArticleImageHeight = ArticleHeight - ArticlePadding;
    private const float ToolbarHeight = 56f;
    private const float BodySize = 15f;
    private const float BodyLineHeight = 1.45f;

    // The scenes pin the glass family dark (see Phone), so their ink is fixed too — asking the
    // gallery theme would answer for the page around the phone, not the wallpaper inside it.
    private static readonly Color SceneInk = Color.White;
    private static readonly Color SceneInkMuted = Color.White.WithAlpha(0.72f);

    private static readonly Color SunYellow = Color.Rgb(r: 255, g: 204, b: 0);
    private static readonly Color BadgeGreen = Color.Rgb(r: 52, g: 199, b: 89);
    private static readonly Color BadgeRed = Color.Rgb(r: 255, g: 59, b: 48);
    private static readonly Color BadgeBlue = Color.Rgb(r: 10, g: 132, b: 255);

    // Toggle state lives on the page, not in the Build — a theme or accent flip rebuilds the
    // whole page, and the radios the user switched off must not come back on.
    private readonly Signal<bool> _airplane = new(false);
    private readonly Signal<bool> _hotspot = new(true);
    private readonly Signal<bool> _wifi = new(true);
    private readonly Signal<bool> _bluetooth = new(true);

    // Where the toolbar's slider left the material: 0 is clear, 1 is tinted. On the page for the
    // same reason as the toggles; the panes take it live, so a drag never rebuilds anything.
    private float _tinted = 0.5f;

    private ThemeData _theme = ThemeData.Dark;

    protected override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);
        return new GalleryPage(
            title: "Liquid Glass",
            description:
            "Glass is the material of the floating layer: chrome over content, never content "
            + "itself. Three scenes, every surface a live lens over what is behind it.",
            iconName: MaterialIcons.WaterDrop
        ) {
            Children = {
                Demo.Titled(
                    title: "Clear to tinted",
                    description:
                    "A toolbar floating over an article that scrolls under it. The card is content "
                    + "and stays flat; the toolbar is the navigation layer, so it is glass. The "
                    + "slider takes its panes from a clear lens to an opaque tint.",
                    child: Article()
                ),
                Demo.Titled(
                    title: "Control centre",
                    description:
                    "Toggles, a player, sliders and utilities — each tile its own pane, all of them "
                    + $"one capture. Backdrop art by {ArtSource.Showcase[1].Artist} (nekos.best).",
                    child: Phone(art: ArtSource.Showcase[1], scene: ControlCentre())
                ),
                Demo.Titled(
                    title: "Lock screen",
                    description:
                    "Notification cards whose adaptive scrim keeps them legible over whatever "
                    + $"the picture does. Backdrop art by {ArtSource.Showcase[4].Artist} "
                    + "(nekos.best).",
                    child: Phone(art: ArtSource.Showcase[4], scene: LockScreen())
                ),
            },
        };
    }

    // ── Scene 1: a toolbar over an article ────────────────────────────────────

    /// <summary>
    ///     The reference scene: a card that is a scroll viewport — a full-bleed picture, a
    ///     headline and the copy — with a menu button and a search field floating over its foot,
    ///     and under it the slider that moves both panes from clear to tinted. The content scrolls
    ///     <i>under</i> the toolbar, which is the point: drag the picture's edge under the lens and
    ///     the rim bends it, park the headline under it and clear shows the letters through while
    ///     tinted covers them. A lens with nothing under it shows nothing.
    /// </summary>
    private Widget Article()
    {
        var theme = _theme;
        var palette = AdwPalette.For(theme);
        var art = ArtSource.Showcase[2];
        Color ink = LiquidPane.OnGlass(theme);

        // The tinted end is a full scrim in the glass family's own tone — Apple's tint, which
        // "increases opacity": white under ink in the light theme, near-black under white in the
        // dark one. The theme's own haze (the Regular variant) sits well short of that, so the
        // slider would otherwise run from clear to slightly-less-clear.
        Color tinted = (theme.IsDark ? Color.Rgb(r: 28, g: 28, b: 32) : Color.White)
            .WithAlpha(0.9f);

        // The thin rim — the reference capsules bend the picture in a hairline at their edge —
        // and only the scrim moves with the slider. The pane objects are kept in the closure so
        // the slider writes the material straight into them — no Watch, no rebuild, the gel
        // state survives every drag.
        var menu = new LiquidPane {
            Elevation = 4f,
            Tint = tinted,
            Tinted = _tinted,
            Child = SizedBox.Square(
                size: ToolbarHeight,
                child: new Center {
                    Child = new IconGlyph(glyph: MaterialIcons.Menu, size: 24f, color: ink),
                }
            ),
        };
        var search = new LiquidPane {
            Elevation = 4f,
            Tint = tinted,
            Tinted = _tinted,
            Child = new SizedBox(
                height: ToolbarHeight,
                child: new Padding(
                    padding: EdgeInsets.Symmetric(horizontal: 20f, vertical: 0f),
                    child: new Row(spacing: 10f, crossAxisAlignment: CrossAxisAlignment.Center) {
                        Children = {
                            new IconGlyph(glyph: MaterialIcons.Search, size: 22f, color: ink),
                            new Label(
                                text: "Search",
                                style: new TextStyle(Size: 17f),
                                color: LiquidPane.OnGlassMuted(theme)
                            ),
                        },
                    }
                )
            ),
        };

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
                                    $"Drawn by {art.Artist}, served by nekos.best. The toolbar "
                                    + "floating over this card is chrome, so it is glass; the "
                                    + "card is content, so it stays flat — and that flatness is "
                                    + "what the lens has to refract.",
                                    style: body,
                                    color: palette.CardFg
                                ),
                                new SizedBox(height: 12f),
                                new Label(
                                    text:
                                    "Scroll the picture's edge under the toolbar and watch the "
                                    + "rim bend it. Park the headline under it: clear shows the "
                                    + "letters through the lens, tinted covers them and keeps "
                                    + "the controls readable over anything.",
                                    style: body,
                                    color: palette.CardFg
                                ),
                                new SizedBox(height: 12f),
                                new Label(
                                    text:
                                    "Every lens here is a live capture of what is behind it, "
                                    + "and the two panes share one — the toolbar is a group, so "
                                    + "the frame copies the scene once for both.",
                                    style: body,
                                    color: palette.CardFg
                                ),
                                // The last line must be able to clear the toolbar.
                                new SizedBox(height: ToolbarHeight + ArticlePadding),
                            },
                        }
                    ),
                },
            },
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
                                // One group for the toolbar: both lenses go down first, one
                                // capture. The scroll viewport is under the glass, not inside
                                // the group — a lens never repaints a patch of itself, so
                                // scrolling recaptures the toolbar's region each frame, and
                                // that is the honest cost of chrome over moving content. The
                                // toolbar is beside the viewport, not in it, so a wheel over
                                // the glass has to be handed to the article on purpose.
                                child: new ScrollsWith(
                                    target: content,
                                    child: new LiquidGroup(
                                        new Row(spacing: 12f) {
                                            Children = {
                                                LiquidPane.Interactive(
                                                    pane: menu,
                                                    onPressed: () => { },
                                                    semantics: "Menu"
                                                ),
                                                new Expanded(
                                                    LiquidPane.Interactive(
                                                        pane: search,
                                                        onPressed: () => { },
                                                        semantics: "Search"
                                                    )
                                                ),
                                            },
                                        }
                                    )
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
                Demo.Stage(
                    new Row(spacing: Spacing.Md, crossAxisAlignment: CrossAxisAlignment.Center) {
                        Children = {
                            new IconGlyph(
                                glyph: MaterialIcons.FilterNone,
                                size: 22f,
                                color: theme.TextSecondary
                            ),
                            new Expanded(
                                new AdwSlider(
                                    value: _tinted,
                                    onChanged: v =>
                                    {
                                        _tinted = v;
                                        menu.Tinted = v;
                                        search.Tinted = v;
                                    }
                                ) { SemanticsLabel = "Clear to tinted" }
                            ),
                            // The nearest this font has to Apple's square.filled.on.square: the
                            // same stacked squares as FilterNone, with the front one filled.
                            new IconGlyph(
                                glyph: MaterialIcons.AutoAwesomeMotion,
                                size: 22f,
                                color: theme.TextSecondary
                            ),
                        },
                    },
                    padding: Spacing.Md
                ),
                Demo.Caption(
                    "Clear is more transparent; tinted increases opacity, adding contrast to "
                    + "content and controls."
                ),
            },
        };
    }

    // ── The phone frame ───────────────────────────────────────────────────────

    /// <summary>A fixed phone canvas: the photo cover-cropped to fill it, the scene on top.</summary>
    private Widget Phone(ArtPiece art, Widget scene)
    {
        return new Center {
            Child = new DecoratedBox {
                Radius = PhoneRadius,
                BorderColor = AdwPalette.For(_theme).CardShade,
                BorderWidth = 1f,
                Child = new ClipRRect(
                    radius: PhoneRadius,
                    child: new SizedBox(
                        width: PhoneW,
                        height: PhoneH,
                        // The scene is a media context, so it pins the glass family: over a
                        // photograph the pane must be the dark family's milky lift with white
                        // ink whatever the gallery theme is — the light theme's heavier scrim
                        // is tuned for chrome over flat surfaces, and over a picture it reads
                        // as a grey slab. iOS does the same on the lock screen.
                        // One group per scene: a screenful of panes, one backdrop capture.
                        child: new Stack {
                            Children = {
                                Backdrop(
                                    art: art,
                                    radius: PhoneRadius,
                                    maxDecode: (int)(PhoneH * 1.2f)
                                ),
                                new ThemeProvider(data: ThemeData.Dark, child: new LiquidGroup(scene)),
                            },
                        }
                    )
                ),
            },
        };
    }

    /// <summary>
    ///     A picture cover-cropped to whatever holds it. Null on failure is AsyncImage's contract
    ///     for "give up quietly" — OrElse collapses every HttpError into it, which is right for a
    ///     decorative backdrop.
    /// </summary>
    private static Widget Backdrop(ArtPiece art, float radius, int maxDecode)
    {
        return new AsyncImage(
            loader: async ct =>
                (await ArtSource.Http.BytesAsync(HttpRequest.Get(art.Url), ct).ConfigureAwait(false))
                .OrElse(null!)
        ) {
            Fit = ImageFit.Cover,
            MaxDecodeSize = maxDecode,
            Radius = radius,
        };
    }

    // ── Scene 2: control centre ───────────────────────────────────────────────

    private Widget ControlCentre()
    {
        return new Padding(
            padding: EdgeInsets.All(16f),
            child: new Column(
                spacing: 14f,
                mainAxisAlignment: MainAxisAlignment.Center,
                crossAxisAlignment: CrossAxisAlignment.Stretch
            ) {
                Children = {
                    // NOT cross-stretch: with the whole phone height as the cross max, Stretch
                    // pins the cross-min to it and the cards' 150px boxes clamp up to full
                    // height — the cards become floor-to-ceiling slabs.
                    new Row(spacing: 14f) {
                        Children = {
                            new Expanded(ConnectivityCard()),
                            new Expanded(PlayerCard()),
                        },
                    },
                    new Row(spacing: 14f, crossAxisAlignment: CrossAxisAlignment.Center) {
                        Children = {
                            new Expanded(
                                new Column(
                                    spacing: 14f,
                                    crossAxisAlignment: CrossAxisAlignment.Stretch
                                ) {
                                    Children = {
                                        new Row(spacing: 14f) {
                                            Children = {
                                                GlassCircle(
                                                    icon: MaterialIcons.ScreenLockRotation,
                                                    label: "Rotation lock"
                                                ),
                                                GlassCircle(
                                                    icon: MaterialIcons.NotificationsNone,
                                                    label: "Notifications"
                                                ),
                                            },
                                        },
                                        FocusPill(),
                                    },
                                }
                            ),
                            Capsule(
                                icon: MaterialIcons.LightMode,
                                fill: 0.40f,
                                iconColor: SunYellow
                            ),
                            Capsule(
                                icon: MaterialIcons.VolumeUp,
                                fill: 0.36f,
                                iconColor: BadgeBlue
                            ),
                        },
                    },
                    UtilityRow(
                        (MaterialIcons.FlashlightOn, "Flashlight"),
                        (MaterialIcons.Timer, "Timer"),
                        (MaterialIcons.Calculate, "Calculator"),
                        (MaterialIcons.PhotoCamera, "Camera")
                    ),
                    UtilityRow(
                        (MaterialIcons.NightlightRound, "Dark mode"),
                        (MaterialIcons.MobileScreenShare, "Screen mirroring"),
                        (MaterialIcons.StickyNote2, "Notes"),
                        (MaterialIcons.ZoomIn, "Magnifier")
                    ),
                },
            }
        );
    }

    /// <summary>The 2×2 radio cluster. The dots are flat fills — glass never stacks on glass.</summary>
    private Widget ConnectivityCard()
    {
        return LiquidPane.Regular(
            child: new SizedBox(
                height: 150f,
                child: new Center {
                    Child = new Column(spacing: 12f, mainAxisSize: MainAxisSize.Min) {
                        Children = {
                            new Row(spacing: 12f, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    ToggleDot(
                                        icon: MaterialIcons.Flight,
                                        label: "Airplane mode",
                                        on: _airplane
                                    ),
                                    ToggleDot(
                                        icon: MaterialIcons.WifiTethering,
                                        label: "Hotspot",
                                        on: _hotspot
                                    ),
                                },
                            },
                            new Row(spacing: 12f, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    ToggleDot(icon: MaterialIcons.Wifi, label: "Wi-Fi", on: _wifi),
                                    ToggleDot(
                                        icon: MaterialIcons.Bluetooth,
                                        label: "Bluetooth",
                                        on: _bluetooth
                                    ),
                                },
                            },
                        },
                    },
                }
            ),
            radius: PhoneRadius,
            elevation: 4f
        );
    }

    private Widget ToggleDot(string icon, string label, Signal<bool> on)
    {
        var theme = _theme;
        return new Watch(() =>
            {
                bool active = on.Value;
                return new Pressable {
                    OnPressed = () => on.Value = !on.Value,
                    FocusRadius = 24f,
                    SemanticsLabel = $"{(active ? "Disable" : "Enable")} {label}",
                    Child = new DecoratedBox {
                        Fill = active ? theme.Accent : Color.White.WithAlpha(0.24f),
                        Radius = 24f,
                        Child = SizedBox.Square(
                            size: 48f,
                            child: new Center {
                                Child = new IconGlyph(
                                    glyph: icon,
                                    size: 22f,
                                    color: Color.White
                                ),
                            }
                        ),
                    },
                };
            }
        );
    }

    private Widget PlayerCard()
    {
        Color on = SceneInk;
        Color muted = SceneInkMuted;
        return LiquidPane.Regular(
            child: new SizedBox(
                height: 150f,
                child: new Padding(
                    padding: EdgeInsets.All(14f),
                    child: new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                        Children = {
                            new Row(crossAxisAlignment: CrossAxisAlignment.Start) {
                                Children = {
                                    new DecoratedBox {
                                        Fill = Color.Rgba(r: 30, g: 34, b: 44, a: 0.85f),
                                        Radius = 10f,
                                        Child = SizedBox.Square(
                                            size: 36f,
                                            child: new Center {
                                                Child = new IconGlyph(
                                                    glyph: MaterialIcons.MusicNote,
                                                    size: 18f,
                                                    color: Color.White
                                                ),
                                            }
                                        ),
                                    },
                                    new Spacer(),
                                    new IconGlyph(
                                        glyph: MaterialIcons.Smartphone,
                                        size: 16f,
                                        color: muted
                                    ),
                                },
                            },
                            new Spacer(),
                            new Label(
                                text: "Backseat Driver",
                                style: new TextStyle(Size: 14f, Weight: FontWeight.Bold),
                                color: on
                            ) { MaxLines = 1, Overflow = TextOverflow.Ellipsis },
                            new Label(text: "Kane Brown", style: AdwTypography.Caption,
                                color: muted),
                            new SizedBox(height: 10f),
                            new Padding(
                                padding: EdgeInsets.Symmetric(horizontal: 6f, vertical: 0f),
                                child: new Row(
                                    mainAxisAlignment: MainAxisAlignment.SpaceBetween
                                ) {
                                    Children = {
                                        new IconGlyph(
                                            glyph: MaterialIcons.FastRewind,
                                            size: 20f,
                                            color: on
                                        ),
                                        new IconGlyph(glyph: MaterialIcons.Pause, size: 22f,
                                            color: on),
                                        new IconGlyph(
                                            glyph: MaterialIcons.FastForward,
                                            size: 20f,
                                            color: on
                                        ),
                                    },
                                }
                            ),
                        },
                    }
                )
            ),
            radius: PhoneRadius,
            elevation: 4f
        );
    }

    private Widget FocusPill()
    {
        Color on = SceneInk;
        var pane = LiquidPane.Clear(
            child: new Padding(
                padding: EdgeInsets.Symmetric(horizontal: 18f, vertical: 13f),
                child: new Row(
                    spacing: 10f,
                    mainAxisSize: MainAxisSize.Min,
                    crossAxisAlignment: CrossAxisAlignment.Center
                ) {
                    Children = {
                        new IconGlyph(glyph: MaterialIcons.Nightlight, size: 17f, color: on),
                        new Label(
                            text: "Focus",
                            style: new TextStyle(Size: 15f, Weight: FontWeight.SemiBold),
                            color: on
                        ),
                        new IconGlyph(
                            glyph: MaterialIcons.UnfoldMore,
                            size: 15f,
                            color: SceneInkMuted
                        ),
                    },
                }
            )
        );
        return LiquidPane.Interactive(pane: pane, onPressed: () => { }, semantics: "Focus modes");
    }

    /// <summary>
    ///     A vertical slider capsule: a clear pane with a flat white fill riding its bottom — the
    ///     clip is what rounds the fill into the capsule's own corners. The lens itself is already
    ///     that shape, so the group drawing it outside the clip changes nothing.
    /// </summary>
    private Widget Capsule(string icon, float fill, Color iconColor)
    {
        const float w = 62f;
        const float h = 150f;
        return new ClipRRect(
            radius: w / 2f,
            child: new SizedBox(
                width: w,
                height: h,
                child: new Stack {
                    Children = {
                        LiquidPane.Clear(child: new SizedBox(width: w, height: h),
                            radius: w / 2f),
                        new Align(
                            alignment: Alignment.BottomCenter,
                            child: new DecoratedBox {
                                Fill = Color.White,
                                Child = new SizedBox(
                                    width: w,
                                    height: h * fill,
                                    child: new Center {
                                        Child = new IconGlyph(glyph: icon, size: 22f,
                                            color: iconColor),
                                    }
                                ),
                            }
                        ),
                    },
                }
            )
        );
    }

    private Widget UtilityRow(params (string Icon, string Label)[] buttons)
    {
        var row = new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween);
        foreach ((string icon, string label) in buttons)
            row.Children.Add(GlassCircle(icon: icon, label: label, size: 56f));
        return row;
    }

    /// <summary>A round clear pane as a button, with the gel response wired in.</summary>
    private Widget GlassCircle(string icon, string label, float size = 52f)
    {
        var pane = LiquidPane.Clear(
            child: SizedBox.Square(
                size: size,
                child: new Center {
                    Child = new IconGlyph(
                        glyph: icon,
                        size: size * 0.42f,
                        color: SceneInk
                    ),
                }
            )
        );
        return LiquidPane.Interactive(pane: pane, onPressed: () => { }, semantics: label);
    }

    /// <summary>
    ///     Text riding the photo directly, with no pane to scrim for it: a soft dark copy under
    ///     the white one is what keeps the clock alive over a bright wallpaper. Labels carry no
    ///     shadow of their own, so the copy is the shadow.
    /// </summary>
    private static Widget ShadowedLabel(string text, TextStyle style, Color color)
    {
        return new Stack {
            Children = {
                new Padding(
                    padding: EdgeInsets.Only(left: 0f, top: 1.5f),
                    child: new Label(
                        text: text,
                        style: style,
                        color: Color.Rgba(r: 0, g: 0, b: 8, a: 0.40f)
                    ) { Align = TextAlign.Center }
                ),
                new Label(text: text, style: style, color: color) { Align = TextAlign.Center },
            },
        };
    }

    // ── Scene 3: lock screen ──────────────────────────────────────────────────

    private Widget LockScreen()
    {
        return new Padding(
            padding: EdgeInsets.All(18f),
            child: new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                Children = {
                    new SizedBox(height: 32f),
                    ShadowedLabel(
                        text: "Saturday, 30 May",
                        style: new TextStyle(Size: 16f, Weight: FontWeight.Medium),
                        color: Color.White.WithAlpha(0.92f)
                    ),
                    ShadowedLabel(
                        text: "9:41",
                        style: new TextStyle(Size: 68f, Weight: FontWeight.Light,
                            LineHeight: 1.05f),
                        color: Color.White
                    ),
                    new SizedBox(height: 22f),
                    Notification(
                        icon: MaterialIcons.Message,
                        badge: BadgeGreen,
                        app: "MESSAGES",
                        when: "2 min ago",
                        title: "Sara",
                        body: "Heading out, see you in 10 minutes!"
                    ),
                    new SizedBox(height: 14f),
                    Notification(
                        icon: MaterialIcons.CalendarToday,
                        badge: BadgeRed,
                        app: "CALENDAR",
                        when: "in 5 min",
                        title: "Standup at 10:00",
                        body: "Daily team sync — Conference Room A."
                    ),
                    new SizedBox(height: 14f),
                    Notification(
                        icon: MaterialIcons.Mail,
                        badge: BadgeBlue,
                        app: "MAIL",
                        when: "18 min ago",
                        title: "Invoice #2043",
                        body: "Your monthly statement is ready to view."
                    ),
                    new Spacer(),
                    new Padding(
                        padding: EdgeInsets.Symmetric(horizontal: 14f, vertical: 0f),
                        child: new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween) {
                            Children = {
                                GlassCircle(
                                    icon: MaterialIcons.FlashlightOn,
                                    label: "Flashlight",
                                    size: 58f
                                ),
                                GlassCircle(
                                    icon: MaterialIcons.PhotoCamera,
                                    label: "Camera",
                                    size: 58f
                                ),
                            },
                        }
                    ),
                    new SizedBox(height: 6f),
                },
            }
        );
    }

    private Widget Notification(
        string icon, Color badge, string app, string when, string title, string body)
    {
        Color on = SceneInk;
        Color muted = SceneInkMuted;
        return new LiquidPane {
            Radius = 22f,
            Child = new Padding(
                padding: EdgeInsets.All(14f),
                child: new Row(spacing: 12f, crossAxisAlignment: CrossAxisAlignment.Start) {
                    Children = {
                        new DecoratedBox {
                            Fill = badge,
                            Radius = 10f,
                            Child = SizedBox.Square(
                                size: 38f,
                                child: new Center {
                                    Child = new IconGlyph(glyph: icon, size: 20f,
                                        color: Color.White),
                                }
                            ),
                        },
                        new Expanded(
                            new Column(
                                spacing: 2f,
                                mainAxisSize: MainAxisSize.Min,
                                crossAxisAlignment: CrossAxisAlignment.Stretch
                            ) {
                                Children = {
                                    new Row {
                                        Children = {
                                            new Label(
                                                text: app,
                                                style: new TextStyle(
                                                    fontSize: 11,
                                                    fontWeight: FontWeight.Bold,
                                                    letterSpacing: 1.1
                                                ),
                                                color: muted
                                            ),
                                            new Spacer(),
                                            new Label(text: when,
                                                style: AdwTypography.Caption, color: muted),
                                        },
                                    },
                                    new Label(
                                        text: title,
                                        style: new TextStyle(Size: 15f,
                                            Weight: FontWeight.SemiBold),
                                        color: on
                                    ) { MaxLines = 1, Overflow = TextOverflow.Ellipsis },
                                    new Label(
                                        text: body,
                                        style: AdwTypography.Body,
                                        color: on.WithAlpha(0.92f)
                                    ) { MaxLines = 2, Overflow = TextOverflow.Ellipsis },
                                },
                            }
                        ),
                    },
                }
            ),
        };
    }
}
