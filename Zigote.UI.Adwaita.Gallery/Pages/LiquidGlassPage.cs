using Zigote.UI.Material;
using Zigote.UI.Net;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Liquid Glass — the material study. Two phone scenes, a control centre and a lock screen,
///     built entirely from <see cref="LiquidPane" /> floating over a photograph: the design
///     system's split in one page — Adwaita decides where things go, glass is what the floating
///     layer is made of.
///     <para>
///         Everything glass here is a real lens over the real picture — each pane is a
///         render-pass break and a full-scene backdrop copy, so this page is also an honest
///         showing of what a screenful of glass costs. The flat fills <i>inside</i> the panes
///         (the toggle dots, the app badges) follow the other rule: glass never stacks on glass.
///     </para>
/// </summary>
public sealed class LiquidGlassPage : ComposedWidget
{
    private const float PhoneW = 320f;
    private const float PhoneH = 640f;
    private const float PhoneRadius = 30f;

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

    private ThemeData _theme = ThemeData.Dark;

    protected override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);
        return new GalleryPage(
            title: "Liquid Glass",
            description:
            "Glass is the material of the floating layer: chrome over content, never content "
            + "itself. Two scenes, every surface a live lens over the picture behind it.",
            iconName: MaterialIcons.WaterDrop
        ) {
            Children = {
                Demo.Titled(
                    title: "Control centre",
                    description:
                    "Toggles, a player, sliders and utilities — each tile its own pane. "
                    + $"Backdrop art by {ArtSource.Showcase[1].Artist} (nekos.best).",
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

    // ── The phone frame ───────────────────────────────────────────────────────

    /// <summary>A fixed phone canvas: the photo cover-cropped to fill it, the scene on top.</summary>
    private Widget Phone(ArtPiece art, Widget scene)
    {
        var backdrop = new AsyncImage(
            loader: async ct => await NetworkCache.FetchAsync(url: art.Url, ct: ct)
                .ConfigureAwait(false)
        ) {
            Fit = ImageFit.Cover,
            MaxDecodeSize = (int)(PhoneH * 1.2f),
            Radius = PhoneRadius,
        };

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
                        child: new Stack {
                            Children = {
                                backdrop,
                                new ThemeProvider(data: ThemeData.Dark, child: scene),
                            },
                        }
                    )
                ),
            },
        };
    }

    // ── Scene 1: control centre ───────────────────────────────────────────────

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
                                                GlassCircle(MaterialIcons.ScreenLockRotation),
                                                GlassCircle(MaterialIcons.NotificationsNone),
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
                        MaterialIcons.FlashlightOn,
                        MaterialIcons.Timer,
                        MaterialIcons.Calculate,
                        MaterialIcons.PhotoCamera
                    ),
                    UtilityRow(
                        MaterialIcons.NightlightRound,
                        MaterialIcons.MobileScreenShare,
                        MaterialIcons.StickyNote2,
                        MaterialIcons.ZoomIn
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
                                    ToggleDot(icon: MaterialIcons.Flight, on: _airplane),
                                    ToggleDot(icon: MaterialIcons.WifiTethering, on: _hotspot),
                                },
                            },
                            new Row(spacing: 12f, mainAxisSize: MainAxisSize.Min) {
                                Children = {
                                    ToggleDot(icon: MaterialIcons.Wifi, on: _wifi),
                                    ToggleDot(icon: MaterialIcons.Bluetooth, on: _bluetooth),
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

    private Widget ToggleDot(string icon, Signal<bool> on)
    {
        var theme = _theme;
        return new Watch(() =>
            {
                bool active = on.Value;
                return new Pressable {
                    OnPressed = () => on.Value = !on.Value,
                    FocusRadius = 24f,
                    SemanticsLabel = $"{(active ? "Disable" : "Enable")} {icon}",
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
    ///     clip is what rounds the fill into the capsule's own corners.
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

    private Widget UtilityRow(params string[] icons)
    {
        var row = new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween);
        foreach (string icon in icons) row.Children.Add(GlassCircle(icon: icon, size: 56f));
        return row;
    }

    /// <summary>A round clear pane as a button, with the gel response wired in.</summary>
    private Widget GlassCircle(string icon, float size = 52f)
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
        return LiquidPane.Interactive(pane: pane, onPressed: () => { }, semantics: icon);
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

    // ── Scene 2: lock screen ──────────────────────────────────────────────────

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
                                GlassCircle(icon: MaterialIcons.FlashlightOn, size: 58f),
                                GlassCircle(icon: MaterialIcons.PhotoCamera, size: 58f),
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
