using Zigote.Http;
using Zigote.UI.Adwaita;

namespace AdwaitaGallery.Pages;

/// <summary>
///     Liquid Glass — the material study. Four scenes built from <see cref="LiquidPane" />: a
///     toolbar floating over an article, with a material lab under it that dials every property
///     of the glass live; an app window whose header bar and view switcher are Adwaita in
///     structure and glass in material; then a control centre and a lock screen over local wallpapers.
///     The design system's split in one page — Adwaita decides where things go, glass is what the
///     floating layer is made of.
///     <para>
///         Every glass surface is a lens over the content beneath it. Each scene's panes sit in
///         one <see cref="LiquidGroup" />, so a screenful of glass costs one backdrop copy and one
///         render-pass break rather than one per pane — and this page is still an honest showing
///         of what that costs. The flat fills <i>inside</i> the panes (the toggle dots, the app
///         badges, the selected tab's pill) follow the other rule: glass never stacks on glass.
///     </para>
///     <para>
///         One file per scene (the partials), one page. Every piece of state a user can change —
///         a toggle they switched off, a fader they dragged, the wallpaper they picked — lives on
///         the page in a <see cref="Signal{T}" />, not in a Build: a theme or accent flip rebuilds
///         the whole page, and nothing the user did may come back undone.
///     </para>
/// </summary>
public sealed partial class LiquidGlassPage : ComposedWidget
{
    private const float PhoneW = 320f;
    private const float PhoneH = 640f;
    private const float PhoneRadius = 30f;

    // Phone scenes keep white foreground ink independently of the surrounding gallery theme.
    private static readonly Color SceneInk = LiquidPane.OnGlass(ThemeData.Dark);
    private static readonly Color SceneInkMuted = LiquidPane.OnGlassMuted(ThemeData.Dark);

    private static readonly Color BadgeGreen = Color.Rgb(r: 52, g: 199, b: 89);
    private static readonly Color BadgeRed = Color.Rgb(r: 255, g: 59, b: 48);
    private static readonly Color BadgeBlue = Color.Rgb(r: 10, g: 132, b: 255);

    /// <summary>
    ///     A latched control's glass: the pane goes solid white with dark ink, the way an iOS
    ///     control-centre circle reads when it is on. Still the same pane — the tint is dialled up
    ///     live, the lens underneath it stays.
    /// </summary>
    private static readonly Color LatchedGlass = Color.White.WithAlpha(0.92f);

    /// <summary>Ink for content riding latched (white) glass, whatever the theme.</summary>
    private static readonly Color LatchedInk = LiquidPane.OnGlass(ThemeData.Light);

    // Every latching button on the page, by name: the flashlight, rotation lock, the utilities.
    // On the page rather than in a Build so a theme flip cannot switch them back off. Keys are
    // shared across scenes on purpose: the torch lit in the control centre is lit on the lock
    // screen — one phone, two faces.
    private readonly Dictionary<string, Signal<bool>> _latches = new(StringComparer.Ordinal);

    private ThemeData _theme = ThemeData.Dark;

    protected override Widget Build(BuildContext context)
    {
        _theme = ThemeProvider.Of(context);
        return new GalleryPage(
            title: "Liquid Glass",
            description:
            "Light, depth and motion. Explore a refractive material for controls "
            + "that float above your content.",
            iconName: ""
        ) {
            Children = {
                Demo.Titled(
                    title: "Chrome over content",
                    description:
                    "A toolbar floating over an article that scrolls under it. The card is content "
                    + "and stays flat; the toolbar is the navigation layer, so it is glass. The "
                    + "material lab under the card dials the glass live.",
                    child: Article()
                ),
                Demo.Titled(
                    title: "Adwaita structure, glass material",
                    description:
                    "An app window: GNOME's header bar and view switcher, made of glass and "
                    + "floating over the content instead of framing it. Flip the material to see "
                    + "the same structure the way Adwaita draws it.",
                    child: Window()
                ),
                Demo.Titled(
                    title: "Control centre",
                    description:
                    "A shared glass surface for connectivity, music and everyday controls.",
                    child: Phone(
                        wallpaper: 2,
                        scene: ControlCentre(),
                        height: 500f,
                        dimming: 0.32f
                    )
                ),
                Demo.Titled(
                    title: "Lock screen",
                    description:
                    "Notification cards whose adaptive scrim keeps them legible over whatever "
                    + "the picture does — pick a wallpaper and watch the glass adjust.",
                    child: LockScreenScene()
                ),
            },
        };
    }

    // ── Shared furniture ──────────────────────────────────────────────────────

    /// <summary>A clipped wallpaper canvas with dimming beneath the shared glass layer.</summary>
    private Widget Phone(int wallpaper, Widget scene, float height = PhoneH, float dimming = 0.16f)
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
                        height: height,
                        // White ink and the dark material family stay together. Wallpaper and
                        // dimming paint before the group so every pane captures the same scene.
                        child: new Stack {
                            Children = {
                                Wallpaper(wallpaper),
                                new MediaDimming(() => dimming),
                                new ThemeProvider(
                                    data: ThemeData.Dark,
                                    child: new LiquidGroup(scene)
                                ),
                            },
                        }
                    )
                ),
            },
        };
    }

    /// <summary>
    ///     A picture cover-cropped to whatever holds it. Null on failure is AdwAsyncImage's contract
    ///     for "give up quietly" — OrElse collapses every HttpError into it, which is right for a
    ///     decorative backdrop.
    /// </summary>
    private static Widget Backdrop(ArtPiece art, float radius, int maxDecode)
    {
        return new AdwAsyncImage(
            loader: async ct =>
                (await ArtSource.Http.BytesAsync(spec: HttpRequest.Get(art.Url), ct: ct)
                    .ConfigureAwait(false))
                .OrElse(null!)
        ) {
            Fit = AdwImageFit.Cover,
            MaxDecodeSize = maxDecode,
            Radius = radius,
        };
    }

    /// <summary>The page-lifetime signal behind a latching button, created on first use.</summary>
    private Signal<bool> Latch(string key)
    {
        if (!_latches.TryGetValue(key: key, value: out var signal))
            _latches[key] = signal = new Signal<bool>(false);
        return signal;
    }

    /// <summary>
    ///     The latched look, applied and wired in one place: solid white glass with dark ink —
    ///     iOS's "on" state — and <see cref="Pressable.Checked" /> exposed with it. Copying the
    ///     tint pair by hand grew a second copy the day the focus pill latched, so it lives here,
    ///     for the same reason <see cref="LiquidPane.Interactive" /> does.
    /// </summary>
    private static Pressable Latched(LiquidPane pane, bool on, Action onPressed, string semantics)
    {
        if (on)
        {
            pane.Tint = LatchedGlass;
            pane.Tinted = 1f;
        }

        var button = LiquidPane.Interactive(pane: pane, onPressed: onPressed, semantics: semantics);
        button.Checked = on;
        return button;
    }

    /// <summary>
    ///     A round regular pane as a button, with the gel response wired in. Given a
    ///     <paramref name="latch" /> it stays pressed — the <see cref="Latched" /> look — and the
    ///     next press clears it again. The pane is rebuilt on the flip rather than retinted in
    ///     place so the glyph's colour flips with it; the gel simply starts at rest on the new
    ///     pane.
    /// </summary>
    private Widget GlassCircle(string icon, string label, float size = 52f, string? latch = null)
    {
        var signal = latch is null ? null : Latch(latch);
        if (signal is null)
        {
            return LiquidPane.Interactive(
                pane: Circle(on: false),
                onPressed: () => { },
                semantics: label
            );
        }

        return new Watch(() => Latched(
                pane: Circle(on: signal.Value),
                on: signal.Value,
                onPressed: () => signal.Value = !signal.Value,
                semantics: label
            )
        );

        LiquidPane Circle(bool on)
        {
            return LiquidPane.Regular(
                radius: AdwMetrics.Pill,
                elevation: 3f,
                child: SizedBox.Square(
                    size: size,
                    child: new Center {
                        Child = new IconGlyph(
                            glyph: icon,
                            size: size * 0.42f,
                            color: on ? LatchedInk : SceneInk
                        ),
                    }
                )
            );
        }
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
                        color: Color.Rgba(
                            r: 0,
                            g: 0,
                            b: 8,
                            a: 0.40f
                        )
                    ) { Align = TextAlign.Center }
                ),
                new Label(text: text, style: style, color: color) { Align = TextAlign.Center },
            },
        };
    }
}
