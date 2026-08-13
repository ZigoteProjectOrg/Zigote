using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;

namespace Zigote.UI.Theme;

/// <summary>
///     The design tokens every widget reads — colours, typography, shape, spacing and interaction.
///     <para>
///         The language is a <b>modern, layered, dark editor</b>: opaque blue-grey surfaces stacked by
///         elevation (window → sidebar/content → panel → card → control), a small accent-tinted
///         palette,
///         the Inter type ramp (see <see cref="Typography" />), the 8-pt <see cref="Spacing" /> grid,
///         and
///         soft low-opacity shadows (<see cref="Elevation" />). Translucency ("Liquid Glass") is
///         opt-in —
///         see <see cref="UseLiquidGlass" />.
///     </para>
///     <para>
///         Surfaces come in two granularities: the legacy trio <see cref="Background" />/
///         <see cref="Surface" />
///         /<see cref="SurfaceAlt" /> that every control already reads, and the explicit editor-chrome
///         tokens
///         (<see cref="Window" />, <see cref="TitleBar" />, <see cref="Toolbar" />,
///         <see cref="Sidebar" />,
///         <see cref="Content" />, <see cref="Panel" />, <see cref="Control" /> …) used to re-skin the
///         shell.
///         Both resolve to the same blue-grey ramp.
///     </para>
///     Colours here are appearance-dependent (light vs dark); sizing/spacing/radius scales live in the
///     static token classes because they don't change between appearances.
///     Assign one to <see cref="App.Theme" /> (defaults are dark).
/// </summary>
public sealed class ThemeData
{
    // ── Presets ───────────────────────────────────────────────────────────────

    /// <summary>The default modern dark (blue-grey, layered) appearance.</summary>
    public static readonly ThemeData Dark = new();

    /// <summary>The modern light appearance.</summary>
    public static readonly ThemeData Light = new() {
        IsDark = false,

        // Legacy trio (mirror Window / Panel / Control below).
        Background = Color.Rgb(242, 244, 247),
        Surface = Color.Rgb(250, 251, 253),
        SurfaceAlt = Color.Rgb(255, 255, 255),
        Primary = Color.Rgb(0, 122, 255),
        PrimaryDark = Color.Rgb(0, 95, 200),
        Accent = Color.Rgb(0, 122, 255),
        AccentHover = Color.Rgb(35, 140, 255),
        AccentPressed = Color.Rgb(0, 95, 200),
        Error = Color.Rgb(255, 59, 48),
        Danger = Color.Rgb(255, 59, 48),
        Success = Color.Rgb(52, 199, 89),
        Warning = Color.Rgb(255, 149, 0),
        Info = Color.Rgb(0, 145, 255),
        OnBackground = Color.Rgb(28, 30, 34),
        OnSurface = Color.Rgb(28, 30, 34),
        OnPrimary = Color.Rgb(255, 255, 255),
        TextSecondary = Color.Rgb(82, 88, 98),
        TextMuted = Color.Rgb(126, 134, 146),
        TextDisabled = Color.Rgb(168, 174, 184),
        Hint = Color.Rgb(126, 134, 146),
        Disabled = Color.Rgb(168, 174, 184),
        Separator = Color.Rgba(
            0,
            0,
            0,
            0.08f
        ),
        Border = Color.Rgba(
            0,
            0,
            0,
            0.12f
        ),

        // Editor-chrome surfaces.
        Window = Color.Rgb(242, 244, 247),
        TitleBar = Color.Rgb(248, 249, 251),
        Toolbar = Color.Rgb(235, 237, 241),
        Sidebar = Color.Rgb(239, 242, 246),
        Content = Color.Rgb(247, 248, 250),
        Panel = Color.Rgb(250, 251, 253),
        PanelRaised = Color.Rgb(255, 255, 255),
        PanelSunken = Color.Rgb(232, 235, 240),
        Card = Color.Rgb(255, 255, 255),
        CardRaised = Color.Rgb(255, 255, 255),
        Control = Color.Rgb(255, 255, 255),
        ControlHover = Color.Rgb(246, 248, 251),
        ControlPressed = Color.Rgb(228, 231, 236),
        ControlDisabled = Color.Rgb(235, 237, 241),
        ViewportBackground = Color.Rgb(222, 226, 232),
        GraphBackground = Color.Rgb(232, 236, 242),
        OverlayBackground = Color.Rgba(
            255,
            255,
            255,
            0.70f
        ),
        SelectionTint = Color.Rgba(
            0,
            122,
            255,
            0.20f
        ),
        SelectionStrong = Color.Rgb(0, 122, 255),
        Fill1 = new Color(
            0f,
            0f,
            0f,
            0.08f
        ),
        Fill2 = new Color(
            0f,
            0f,
            0f,
            0.06f
        ),
        Fill3 = new Color(
            0f,
            0f,
            0f,
            0.045f
        ),
        Fill4 = new Color(
            0f,
            0f,
            0f,
            0.03f
        ),
        Fill5 = new Color(
            0f,
            0f,
            0f,
            0.02f
        ),
        Label1 = new Color(
            0f,
            0f,
            0f,
            0.85f
        ),
        Label2 = new Color(
            0f,
            0f,
            0f,
            0.50f
        ),
        Label3 = new Color(
            0f,
            0f,
            0f,
            0.26f
        ),
        Label4 = new Color(
            0f,
            0f,
            0f,
            0.12f
        ),
    };

    // ── Surfaces — legacy trio (every control reads these) ─────────────────────
    /// <summary>Window backdrop — the lowest layer (alias of <see cref="Window" />).</summary>
    public Color Background { get; init; } = Color.Rgb(16, 18, 22);

    /// <summary>Content surface — panels, scroll areas (alias of <see cref="Panel" />).</summary>
    public Color Surface { get; init; } = Color.Rgb(24, 28, 34);

    /// <summary>Raised surface — cards, controls, rows (alias of <see cref="Control" />).</summary>
    public Color SurfaceAlt { get; init; } = Color.Rgb(33, 38, 46);

    // ── Editor-chrome surfaces (explicit, for shell re-skinning) ───────────────
    public Color Window { get; init; } = Color.Rgb(16, 18, 22);
    public Color TitleBar { get; init; } = Color.Rgb(19, 22, 27);
    public Color Toolbar { get; init; } = Color.Rgb(22, 25, 30);
    public Color Sidebar { get; init; } = Color.Rgb(18, 21, 26);
    public Color Content { get; init; } = Color.Rgb(20, 23, 28);
    public Color Panel { get; init; } = Color.Rgb(24, 28, 34);
    public Color PanelRaised { get; init; } = Color.Rgb(30, 35, 42);
    public Color PanelSunken { get; init; } = Color.Rgb(13, 16, 20);
    public Color Card { get; init; } = Color.Rgb(24, 28, 34);
    public Color CardRaised { get; init; } = Color.Rgb(30, 35, 42);

    // ── Control fills (opaque, interaction-stateful) ──────────────────────────
    public Color Control { get; init; } = Color.Rgb(33, 38, 46);
    public Color ControlHover { get; init; } = Color.Rgb(42, 48, 58);
    public Color ControlPressed { get; init; } = Color.Rgb(27, 31, 38);
    public Color ControlDisabled { get; init; } = Color.Rgb(28, 31, 36);

    // ── Accent / status ───────────────────────────────────────────────────────
    public Color Primary { get; init; } = Color.Rgb(10, 132, 255); // system blue (dark)
    public Color PrimaryDark { get; init; } = Color.Rgb(0, 96, 223);
    public Color Accent { get; init; } = Color.Rgb(10, 132, 255);
    public Color AccentHover { get; init; } = Color.Rgb(45, 150, 255);
    public Color AccentPressed { get; init; } = Color.Rgb(0, 100, 210);

    public Color Error { get; init; } = Color.Rgb(255, 69, 58);
    public Color Danger { get; init; } = Color.Rgb(255, 69, 58);
    public Color Success { get; init; } = Color.Rgb(48, 209, 88);
    public Color Warning { get; init; } = Color.Rgb(255, 159, 10);
    public Color Info { get; init; } = Color.Rgb(100, 210, 255);

    // ── Text on surfaces ──────────────────────────────────────────────────────
    public Color OnBackground { get; init; } = Color.Rgb(235, 238, 242);
    public Color OnSurface { get; init; } = Color.Rgb(235, 238, 242);
    public Color OnPrimary { get; init; } = Color.Rgb(255, 255, 255);

    /// <summary>Secondary text — labels, captions next to a primary value.</summary>
    public Color TextSecondary { get; init; } = Color.Rgb(176, 183, 193);

    /// <summary>Muted text — placeholders, disabled-ish hints (alias of <see cref="Hint" />).</summary>
    public Color TextMuted { get; init; } = Color.Rgb(120, 128, 140);

    /// <summary>Disabled text.</summary>
    public Color TextDisabled { get; init; } = Color.Rgb(82, 88, 96);

    public Color Hint { get; init; } = Color.Rgb(120, 128, 140);
    public Color Disabled { get; init; } = Color.Rgb(82, 88, 96);

    /// <summary>Hairline divider colour (translucent so it sits on any surface).</summary>
    public Color Separator { get; init; } = new(
        1f,
        1f,
        1f,
        0.08f
    );

    /// <summary>Container/control outline (translucent).</summary>
    public Color Border { get; init; } = new(
        1f,
        1f,
        1f,
        0.08f
    );

    // ── Surfaces — viewport / graph / overlay ─────────────────────────────────
    /// <summary>3D viewport clear colour (darkest, near-black).</summary>
    public Color ViewportBackground { get; init; } = Color.Rgb(12, 15, 20);

    /// <summary>Node-graph canvas background.</summary>
    public Color GraphBackground { get; init; } = Color.Rgb(17, 21, 27);

    /// <summary>Scrim behind modal overlays / sheets.</summary>
    public Color OverlayBackground { get; init; } = new(
        0f,
        0f,
        0f,
        0.50f
    );

    // ── Typography ────────────────────────────────────────────────────────────
    // Per-role styles live in Typography; these mirror the most-used sizes for legacy call sites.
    // Settable (not init-only) so WithFontScale can produce scaled copies — but NEVER mutate the
    // shared Dark/Light preset instances in place; clone first (WithFontScale does).
    public float FontSizeBody { get; set; } = Typography.Body.Size; // 13
    public float FontSizeCaption { get; set; } = Typography.Subheadline.Size; // 11
    public float FontSizeTitle { get; set; } = Typography.Title2.Size; // 17
    public float FontSizeH1 { get; set; } = Typography.LargeTitle.Size; // 26
    public float LineHeight { get; init; } = 1.3f;
    public FontWeight BodyWeight { get; init; } = FontWeight.Normal;

    /// <summary>
    ///     A copy of this theme with every font-size token multiplied by <paramref name="scale" />
    ///     — the "UI font size" preference. Widgets that read <c>theme.FontSize*</c> (all the
    ///     standard controls) scale; call sites on the static <see cref="Typography" /> ramp do not.
    /// </summary>
    public ThemeData WithFontScale(float scale)
    {
        if (Math.Abs(scale - 1f) < 0.001f) return this;
        var t = (ThemeData)MemberwiseClone();
        t.FontSizeBody = FontSizeBody * scale;
        t.FontSizeCaption = FontSizeCaption * scale;
        t.FontSizeTitle = FontSizeTitle * scale;
        t.FontSizeH1 = FontSizeH1 * scale;
        return t;
    }

    // ── Shape ─────────────────────────────────────────────────────────────────
    public float ButtonRadius { get; init; } = Radii.Md; // 6
    public float InputRadius { get; init; } = Radii.Md; // 6
    public float CardRadius { get; init; } = Radii.Lg; // 8

    /// <summary>
    ///     Corner radius of the transient toast surface (<see cref="Widgets.Controls.Snackbar" />).
    ///     Its own token rather than a reuse of <see cref="CardRadius" />: design systems disagree
    ///     about this shape more than any other — a Material snackbar is a rounded rectangle, an
    ///     Adwaita toast is a full capsule.
    /// </summary>
    public float ToastRadius { get; init; } = Radii.Lg; // 8

    // ── Spacing (default control padding) ─────────────────────────────────────
    public float Padding { get; init; } = Spacing.Md; // 12

    // ── Focus ring ────────────────────────────────────────────────────────────
    /// <summary>Width of the keyboard focus ring stroke.</summary>
    public float FocusRingWidth { get; init; } = 3f;

    /// <summary>Gap between a control's bounds and its focus ring.</summary>
    public float FocusRingOffset { get; init; } = 2f;

    /// <summary>
    ///     Focus-ring colour (system accent at reduced opacity), resolved from <see cref="Primary" />
    ///     .
    /// </summary>
    public Color FocusRing => Primary.WithAlpha(0.5f);

    /// <summary>Solid selection highlight (selected menu items, list rows).</summary>
    public Color Selection => Primary;

    /// <summary>Translucent selection wash (selected rows that keep their content legible).</summary>
    public Color SelectionTint { get; init; } = new(
        10 / 255f,
        132 / 255f,
        255 / 255f,
        0.28f
    );

    /// <summary>Strong (solid) selection fill.</summary>
    public Color SelectionStrong { get; init; } = Color.Rgb(10, 132, 255);

    // ── Liquid Glass (opt-in) ─────────────────────────────────────────────────
    // The flat language is the default. Liquid Glass is the engine's GPU-accelerated translucency
    // material, available behind an explicit opt-in (per-control UseGlass, or this flag) for chrome
    // that wants vibrancy. It is NOT used by default controls.

    public Color GlassTint { get; init; } = new(0.86f, 0.90f, 1f);
    public float GlassTintStrength { get; init; } = 0.08f;
    public float GlassThickness { get; init; } = 10f;
    public float GlassPinch { get; init; } = 0.08f;
    public float GlassGlowX { get; init; } = 0.5f;
    public float GlassGlowY { get; init; } = -0.85f;
    public float GlassRadius { get; init; } = 11f;

    // ── Fill / Label tokens ───────────────────────────────────────────────────
    // Fills are translucent neutral control backgrounds (Fill1 most opaque → Fill5 faintest).
    // Labels are translucent text levels (Label1 full → Label4 faint). Defaults are dark; the Light
    // preset overrides them. Pick the right level instead of hardcoding greys.

    /// <summary>True for dark appearances — selects the Dark variant from <see cref="SystemColors" />.</summary>
    public bool IsDark { get; init; } = true;

    public Color Fill1 { get; init; } = new(
        1f,
        1f,
        1f,
        0.10f
    );

    public Color Fill2 { get; init; } = new(
        1f,
        1f,
        1f,
        0.07f
    );

    public Color Fill3 { get; init; } = new(
        1f,
        1f,
        1f,
        0.05f
    );

    public Color Fill4 { get; init; } = new(
        1f,
        1f,
        1f,
        0.035f
    );

    public Color Fill5 { get; init; } = new(
        1f,
        1f,
        1f,
        0.025f
    );

    public Color Label1 { get; init; } = new(
        1f,
        1f,
        1f,
        0.85f
    );

    public Color Label2 { get; init; } = new(
        1f,
        1f,
        1f,
        0.55f
    );

    public Color Label3 { get; init; } = new(
        1f,
        1f,
        1f,
        0.30f
    );

    public Color Label4 { get; init; } = new(
        1f,
        1f,
        1f,
        0.15f
    );

    /// <summary>Explicit factory mirroring <see cref="Dark" />.</summary>
    public static ThemeData MacDark()
    {
        return Dark;
    }

    /// <summary>Explicit factory mirroring <see cref="Light" />.</summary>
    public static ThemeData MacLight()
    {
        return Light;
    }

    /// <summary>Resolve a system colour pair to the variant for this theme's appearance.</summary>
    public Color System(SystemColors.Pair p)
    {
        return IsDark ? p.Dark : p.Light;
    }
}
