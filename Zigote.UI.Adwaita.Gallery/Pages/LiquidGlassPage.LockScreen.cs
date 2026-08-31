using Zigote.UI.Material;
using Zigote.UI.Semantics;

namespace AdwaitaGallery.Pages;

public sealed partial class LiquidGlassPage
{
    // Five of the showcase pieces, chosen for range: a bright sky, a dark coat, a busy print —
    // the scrim has to earn its keep on each.
    private static readonly int[] Wallpapers = [4, 0, 3, 5, 7];

    /// <summary>Thumbnail side: a 44 px target is the smallest a finger reliably hits.</summary>
    private const float ThumbSize = 44f;

    /// <summary>Notification card radius — iOS's lock-screen card, on a 320 px phone.</summary>
    private const float CardCorner = 22f;

    /// <summary>The collapsed stack's slivers: thin enough to read as edges, not as
    ///     cards.</summary>
    private const float SliverHeight = 8f;

    private readonly Signal<int> _wallpaper = new(0);
    private readonly Signal<bool> _stacked = new(false);

    /// <summary>The lock-screen phone, the wallpaper picker under it, and the caption.</summary>
    private Widget LockScreenScene()
    {
        return new Column(
            spacing: Spacing.Lg,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Stretch
        ) {
            Children = {
                // Picking a wallpaper rebuilds the phone; the scene's own state is in signals on
                // the page, so the stack stays collapsed and the torch stays lit across it.
                new Watch(() => Phone(
                    art: ArtSource.Showcase[Wallpapers[_wallpaper.Value]],
                    scene: LockScreen()
                )),
                new Watch(WallpaperPicker),
                Demo.Caption(
                    "The scrim is per pixel: it dims a bright sky and lifts a dark coat, so the "
                    + "cards read on every wallpaper."
                ),
            },
        };
    }

    // ── Wallpaper picker ──────────────────────────────────────────────────────

    /// <summary>A radio row of thumbnails; the accent ring marks the one on the phone.</summary>
    private Widget WallpaperPicker()
    {
        int selected = _wallpaper.Value;
        var row = new Row(
            spacing: 10f,
            mainAxisSize: MainAxisSize.Min,
            mainAxisAlignment: MainAxisAlignment.Center
        );
        for (int i = 0; i < Wallpapers.Length; i++)
        {
            int index = i;
            var art = ArtSource.Showcase[Wallpapers[i]];
            bool on = i == selected;
            row.Children.Add(
                new Pressable {
                    Role = SemanticsRole.RadioButton,
                    Checked = on,
                    SemanticsLabel = $"Wallpaper by {art.Artist}",
                    FocusRadius = 12f,
                    OnPressed = () => _wallpaper.Value = index,
                    Child = new DecoratedBox {
                        Radius = 12f,
                        // Transparent, not a hairline, when off: the ring marks the selection,
                        // and a CardShade hairline is invisible over the dark page anyway.
                        BorderColor = on ? _theme.Accent : Color.Rgba(r: 0, g: 0, b: 0, a: 0f),
                        BorderWidth = 2f,
                        Child = new Padding(
                            padding: EdgeInsets.All(2f),
                            child: new ClipRRect(
                                radius: LiquidPane.Inner(outer: 12f, padding: 2f),
                                child: SizedBox.Square(
                                    size: ThumbSize,
                                    child: Backdrop(art: art, radius: 10f, maxDecode: 96)
                                )
                            )
                        ),
                    },
                }
            );
        }

        return row;
    }

    // ── Scene 4: lock screen ──────────────────────────────────────────────────

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
                    // A snap, not an eased height: the group lays every pane's lens down at its
                    // full bounds before any content paints, so an AnimatedSize here would show
                    // the two lower cards' glass a beat before their content slid into view.
                    // The clock above does not move either way — only this box changes size.
                    new Watch(() => Notifications(_stacked.Value)),
                    new Spacer(),
                    new Padding(
                        padding: EdgeInsets.Symmetric(horizontal: 14f, vertical: 0f),
                        child: new Row(mainAxisAlignment: MainAxisAlignment.SpaceBetween) {
                            Children = {
                                GlassCircle(
                                    icon: MaterialIcons.FlashlightOn,
                                    label: "Flashlight",
                                    size: 58f,
                                    latch: "Flashlight"
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

    /// <summary>
    ///     The notification area in either state. The top card is the button that flips it.
    ///     Collapsed, the two cards behind it are flat slivers rather than panes: panes in a
    ///     group must never overlap, and glass never stacks on glass — the slivers only suggest
    ///     the cards, they are not them.
    /// </summary>
    private Widget Notifications(bool stacked)
    {
        var top = Notification(
            icon: MaterialIcons.Message,
            badge: BadgeGreen,
            app: "MESSAGES",
            when: "2 min ago",
            title: "Sara",
            body: "Heading out, see you in 10 minutes!",
            count: stacked ? 3 : null
        );
        var column = new Column(
            spacing: stacked ? 3f : 14f,
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Stretch
        ) {
            Children = {
                LiquidPane.Interactive(
                    pane: top,
                    onPressed: () => _stacked.Value = !_stacked.Value,
                    semantics: stacked ? "Expand notifications" : "Collapse notifications"
                ),
            },
        };

        if (stacked)
        {
            column.Children.Add(Sliver(inset: 12f, alpha: 0.22f));
            column.Children.Add(Sliver(inset: 24f, alpha: 0.14f));
            return column;
        }

        column.Children.Add(
            Notification(
                icon: MaterialIcons.CalendarToday,
                badge: BadgeRed,
                app: "CALENDAR",
                when: "in 5 min",
                title: "Standup at 10:00",
                body: "Daily team sync — Conference Room A."
            )
        );
        column.Children.Add(
            Notification(
                icon: MaterialIcons.Mail,
                badge: BadgeBlue,
                app: "MAIL",
                when: "18 min ago",
                title: "Invoice #2043",
                body: "Your monthly statement is ready to view."
            )
        );
        return column;
    }

    /// <summary>A flat edge of a card behind the stack — white haze, no lens.</summary>
    private static Widget Sliver(float inset, float alpha)
    {
        return new Padding(
            padding: EdgeInsets.Symmetric(horizontal: inset, vertical: 0f),
            child: new DecoratedBox {
                Fill = Color.White.WithAlpha(alpha),
                Radius = CardCorner,
                Child = new SizedBox(height: SliverHeight),
            }
        );
    }

    /// <summary>
    ///     One notification card. <paramref name="count" /> adds the collapsed stack's tally
    ///     chip — flat, like everything else riding the pane.
    /// </summary>
    private static LiquidPane Notification(
        string icon, Color badge, string app, string when, string title, string body,
        int? count = null)
    {
        Color on = SceneInk;
        Color muted = SceneInkMuted;
        var header = new Row(spacing: 6f) {
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
            },
        };
        if (count is { } n)
        {
            header.Children.Add(
                new DecoratedBox {
                    Fill = muted,
                    Radius = AdwMetrics.Pill,
                    Child = new Padding(
                        padding: EdgeInsets.Symmetric(horizontal: 7f, vertical: 1f),
                        child: new Label(
                            text: n.ToString(),
                            style: new TextStyle(Size: 11f, Weight: FontWeight.Bold),
                            color: LatchedInk
                        )
                    ),
                }
            );
        }

        header.Children.Add(new Label(text: when, style: AdwTypography.Caption, color: muted));

        return new LiquidPane {
            Radius = CardCorner,
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
                                    header,
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
