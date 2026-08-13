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
        Background = Color.Rgb(r: 242, g: 244, b: 247),
        Surface = Color.Rgb(r: 250, g: 251, b: 253),
        SurfaceAlt = Color.Rgb(r: 255, g: 255, b: 255),
        Primary = Color.Rgb(r: 0, g: 122, b: 255),
        PrimaryDark = Color.Rgb(r: 0, g: 95, b: 200),
        Accent = Color.Rgb(r: 0, g: 122, b: 255),
        AccentHover = Color.Rgb(r: 35, g: 140, b: 255),
        AccentPressed = Color.Rgb(r: 0, g: 95, b: 200),
        Error = Color.Rgb(r: 255, g: 59, b: 48),
        Danger = Color.Rgb(r: 255, g: 59, b: 48),
        Success = Color.Rgb(r: 52, g: 199, b: 89),
        Warning = Color.Rgb(r: 255, g: 149, b: 0),
        Info = Color.Rgb(r: 0, g: 145, b: 255),
        OnBackground = Color.Rgb(r: 28, g: 30, b: 34),
        OnSurface = Color.Rgb(r: 28, g: 30, b: 34),
        OnPrimary = Color.Rgb(r: 255, g: 255, b: 255),
        TextSecondary = Color.Rgb(r: 82, g: 88, b: 98),
        TextMuted = Color.Rgb(r: 126, g: 134, b: 146),
        TextDisabled = Color.Rgb(r: 168, g: 174, b: 184),
        Hint = Color.Rgb(r: 126, g: 134, b: 146),
        Disabled = Color.Rgb(r: 168, g: 174, b: 184),
        Separator = Color.Rgba(
            r: 0,
            g: 0,
            b: 0,
            a: 0.08f
        ),
        Border = Color.Rgba(
            r: 0,
            g: 0,
            b: 0,
            a: 0.12f
        ),

        // Editor-chrome surfaces.
        Window = Color.Rgb(r: 242, g: 244, b: 247),
        TitleBar = Color.Rgb(r: 248, g: 249, b: 251),
        Toolbar = Color.Rgb(r: 235, g: 237, b: 241),
        Sidebar = Color.Rgb(r: 239, g: 242, b: 246),
        Content = Color.Rgb(r: 247, g: 248, b: 250),
        Panel = Color.Rgb(r: 250, g: 251, b: 253),
        PanelRaised = Color.Rgb(r: 255, g: 255, b: 255),
        PanelSunken = Color.Rgb(r: 232, g: 235, b: 240),
        Card = Color.Rgb(r: 255, g: 255, b: 255),
        CardRaised = Color.Rgb(r: 255, g: 255, b: 255),
        Control = Color.Rgb(r: 255, g: 255, b: 255),
        ControlHover = Color.Rgb(r: 246, g: 248, b: 251),
        ControlPressed = Color.Rgb(r: 228, g: 231, b: 236),
        ControlDisabled = Color.Rgb(r: 235, g: 237, b: 241),
        ViewportBackground = Color.Rgb(r: 222, g: 226, b: 232),
        GraphBackground = Color.Rgb(r: 232, g: 236, b: 242),
        OverlayBackground = Color.Rgba(
            r: 255,
            g: 255,
            b: 255,
            a: 0.70f
        ),
        SelectionTint = Color.Rgba(
            r: 0,
            g: 122,
            b: 255,
            a: 0.20f
        ),
        SelectionStrong = Color.Rgb(r: 0, g: 122, b: 255),
        Fill1 = new Color(
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0.08f
        ),
        Fill2 = new Color(
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0.06f
        ),
        Fill3 = new Color(
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0.045f
        ),
        Fill4 = new Color(
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0.03f
        ),
        Fill5 = new Color(
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0.02f
        ),
        Label1 = new Color(
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0.85f
        ),
        Label2 = new Color(
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0.50f
        ),
        Label3 = new Color(
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0.26f
        ),
        Label4 = new Color(
            r: 0f,
            g: 0f,
            b: 0f,
            a: 0.12f
        ),
    };

    // ── Surfaces — legacy trio (every control reads these) ─────────────────────
    /// <summary>Window backdrop — the lowest layer (alias of <see cref="Window" />).</summary>
    public Color Background { get; init; } = Color.Rgb(r: 16, g: 18, b: 22);

    /// <summary>Content surface — panels, scroll areas (alias of <see cref="Panel" />).</summary>
    public Color Surface { get; init; } = Color.Rgb(r: 24, g: 28, b: 34);

    /// <summary>Raised surface — cards, controls, rows (alias of <see cref="Control" />).</summary>
    public Color SurfaceAlt { get; init; } = Color.Rgb(r: 33, g: 38, b: 46);

    // ── Editor-chrome surfaces (explicit, for shell re-skinning) ───────────────
    public Color Window { get; init; } = Color.Rgb(r: 16, g: 18, b: 22);
    public Color TitleBar { get; init; } = Color.Rgb(r: 19, g: 22, b: 27);
    public Color Toolbar { get; init; } = Color.Rgb(r: 22, g: 25, b: 30);
    public Color Sidebar { get; init; } = Color.Rgb(r: 18, g: 21, b: 26);
    public Color Content { get; init; } = Color.Rgb(r: 20, g: 23, b: 28);
    public Color Panel { get; init; } = Color.Rgb(r: 24, g: 28, b: 34);
    public Color PanelRaised { get; init; } = Color.Rgb(r: 30, g: 35, b: 42);
    public Color PanelSunken { get; init; } = Color.Rgb(r: 13, g: 16, b: 20);
    public Color Card { get; init; } = Color.Rgb(r: 24, g: 28, b: 34);
    public Color CardRaised { get; init; } = Color.Rgb(r: 30, g: 35, b: 42);

    // ── Control fills (opaque, interaction-stateful) ──────────────────────────
    public Color Control { get; init; } = Color.Rgb(r: 33, g: 38, b: 46);
    public Color ControlHover { get; init; } = Color.Rgb(r: 42, g: 48, b: 58);
    public Color ControlPressed { get; init; } = Color.Rgb(r: 27, g: 31, b: 38);
    public Color ControlDisabled { get; init; } = Color.Rgb(r: 28, g: 31, b: 36);

    // ── Accent / status ───────────────────────────────────────────────────────
    public Color Primary { get; init; } = Color.Rgb(r: 10, g: 132, b: 255); // system blue (dark)
    public Color PrimaryDark { get; init; } = Color.Rgb(r: 0, g: 96, b: 223);
    public Color Accent { get; init; } = Color.Rgb(r: 10, g: 132, b: 255);
    public Color AccentHover { get; init; } = Color.Rgb(r: 45, g: 150, b: 255);
    public Color AccentPressed { get; init; } = Color.Rgb(r: 0, g: 100, b: 210);

    public Color Error { get; init; } = Color.Rgb(r: 255, g: 69, b: 58);
    public Color Danger { get; init; } = Color.Rgb(r: 255, g: 69, b: 58);
    public Color Success { get; init; } = Color.Rgb(r: 48, g: 209, b: 88);
    public Color Warning { get; init; } = Color.Rgb(r: 255, g: 159, b: 10);
    public Color Info { get; init; } = Color.Rgb(r: 100, g: 210, b: 255);

    // ── Text on surfaces ──────────────────────────────────────────────────────
    public Color OnBackground { get; init; } = Color.Rgb(r: 235, g: 238, b: 242);
    public Color OnSurface { get; init; } = Color.Rgb(r: 235, g: 238, b: 242);
    public Color OnPrimary { get; init; } = Color.Rgb(r: 255, g: 255, b: 255);

    /// <summary>Secondary text — labels, captions next to a primary value.</summary>
    public Color TextSecondary { get; init; } = Color.Rgb(r: 176, g: 183, b: 193);

    /// <summary>Muted text — placeholders, disabled-ish hints (alias of <see cref="Hint" />).</summary>
    public Color TextMuted { get; init; } = Color.Rgb(r: 120, g: 128, b: 140);

    /// <summary>Disabled text.</summary>
    public Color TextDisabled { get; init; } = Color.Rgb(r: 82, g: 88, b: 96);

    public Color Hint { get; init; } = Color.Rgb(r: 120, g: 128, b: 140);
    public Color Disabled { get; init; } = Color.Rgb(r: 82, g: 88, b: 96);

    /// <summary>Hairline divider colour (translucent so it sits on any surface).</summary>
    public Color Separator { get; init; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.08f
    );

    /// <summary>Container/control outline (translucent).</summary>
    public Color Border { get; init; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.08f
    );

    // ── Surfaces — viewport / graph / overlay ─────────────────────────────────
    /// <summary>3D viewport clear colour (darkest, near-black).</summary>
    public Color ViewportBackground { get; init; } = Color.Rgb(r: 12, g: 15, b: 20);

    /// <summary>Node-graph canvas background.</summary>
    public Color GraphBackground { get; init; } = Color.Rgb(r: 17, g: 21, b: 27);

    /// <summary>Scrim behind modal overlays / sheets.</summary>
    public Color OverlayBackground { get; init; } = new(
        r: 0f,
        g: 0f,
        b: 0f,
        a: 0.50f
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
        r: 10 / 255f,
        g: 132 / 255f,
        b: 255 / 255f,
        a: 0.28f
    );

    /// <summary>Strong (solid) selection fill.</summary>
    public Color SelectionStrong { get; init; } = Color.Rgb(r: 10, g: 132, b: 255);

    // ── Liquid Glass (opt-in) ─────────────────────────────────────────────────
    // The flat language is the default. Liquid Glass is the engine's GPU-accelerated translucency
    // material, available behind an explicit opt-in (per-control UseGlass, or this flag) for chrome
    // that wants vibrancy. It is NOT used by default controls.

    public Color GlassTint { get; init; } = new(r: 0.86f, g: 0.90f, b: 1f);
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
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.10f
    );

    public Color Fill2 { get; init; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.07f
    );

    public Color Fill3 { get; init; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.05f
    );

    public Color Fill4 { get; init; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.035f
    );

    public Color Fill5 { get; init; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.025f
    );

    public Color Label1 { get; init; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.85f
    );

    public Color Label2 { get; init; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.55f
    );

    public Color Label3 { get; init; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.30f
    );

    public Color Label4 { get; init; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.15f
    );

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

    /// <summary>Explicit factory mirroring <see cref="Dark" />.</summary>
    public static ThemeData MacDark() => Dark;

    /// <summary>Explicit factory mirroring <see cref="Light" />.</summary>
    public static ThemeData MacLight() => Light;

    /// <summary>Resolve a system colour pair to the variant for this theme's appearance.</summary>
    public Color System(SystemColors.Pair p) => IsDark ? p.Dark : p.Light;
}
