using Xunit;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Widgets;

namespace Zigote.Tests;

/// <summary>
///     Pins the Adwaita theme maths that has no visual test: the linear-blend alpha correction and
///     the contrast-driven standalone accent. Both are easy to "simplify" back to the CSS numbers.
/// </summary>
public class AdwaitaThemeTests
{
    private static float Lin(float c) => MathF.Pow(x: MathF.Max(x: c, y: 0f), y: 2.2f);

    /// <summary>Composite in linear space (what the shader does) and re-encode.</summary>
    private static float Composite(Color fg, Color bg)
    {
        float l = ((1f - fg.A) * Lin(bg.G)) + (fg.A * Lin(fg.G));
        return MathF.Pow(x: l, y: 1f / 2.2f);
    }

    // libadwaita 1.10 states every neutral fill as color-mix(currentColor N%, transparent), so the
    // ladder is the same in both appearances — only currentColor flips.
    [Theory]
    [InlineData(false, 0.10f)] // light button fill
    [InlineData(false, 0.30f)] // light button :active
    [InlineData(true, 0.10f)] // dark button fill
    [InlineData(true, 0.30f)] // dark button :active
    public void OverlayAlphasReproduceTheStylesheetColour(bool dark, float cssAlpha)
    {
        var p = dark ? AdwPalette.Dark : AdwPalette.Light;
        var surface = p.WindowBg;
        var fill = cssAlpha == 0.10f ? p.ButtonFill : p.ButtonFillActive;

        // What GNOME's CSS means: blend in sRGB space.
        float tint = dark ? 1f : 6f / 255f;
        float expected = ((1f - cssAlpha) * surface.G) + (cssAlpha * tint);

        // Overlay() averages the three solved channels, so the green-only reference here lands
        // within a fraction of a level rather than exactly.
        Assert.InRange(
            actual: Composite(fg: fill, bg: surface),
            low: expected - 0.005f,
            high: expected + 0.005f
        );
    }

    [Fact]
    public void DarkNeutralFillIsNotBlownOutByLinearBlending()
    {
        // The pre-fix value (a literal .10 white) composited to ~#5e5e60 instead of ~#383839.
        float rendered = Composite(fg: AdwPalette.Dark.ButtonFill, bg: AdwPalette.Dark.WindowBg) *
                         255f;
        Assert.InRange(actual: rendered, low: 50f, high: 62f);
    }

    /// <summary>
    ///     The standalone colours are <c>oklab(from @x min(l, 0.5) a b)</c> in light and
    ///     <c>max(l, 0.85)</c> in dark. Pinned against the values libadwaita publishes for them: if
    ///     the Oklab conversion drifts (a transfer function swapped for the renderer's 2.2 gamma,
    ///     say), every link, alert response and status label drifts with it.
    /// </summary>
    [Theory]
    [InlineData(
        false,
        "accent",
        0x04,
        0x61,
        0xbe
    )]
    [InlineData(
        true,
        "accent",
        0x81,
        0xd0,
        0xff
    )]
    [InlineData(
        false,
        "destructive",
        0xc3,
        0x00,
        0x00
    )]
    [InlineData(
        true,
        "destructive",
        0xff,
        0x93,
        0x8c
    )]
    [InlineData(
        false,
        "success",
        0x00,
        0x7c,
        0x3d
    )]
    [InlineData(
        true,
        "success",
        0x78,
        0xe9,
        0xab
    )]
    [InlineData(
        false,
        "warning",
        0x90,
        0x54,
        0x00
    )]
    [InlineData(
        true,
        "warning",
        0xff,
        0xc2,
        0x52
    )]
    public void StandaloneColoursMatchLibadwaita(bool dark, string which, int r, int g, int b)
    {
        var p = dark ? AdwPalette.Dark : AdwPalette.Light;
        var actual = which switch {
            "accent" => p.Accent,
            "destructive" => p.Destructive,
            "success" => p.Success,
            _ => p.Warning,
        };

        // One 8-bit level of slack: the published hex is itself a rounding of the same maths.
        Assert.InRange(actual: actual.R * 255f, low: r - 1.5f, high: r + 1.5f);
        Assert.InRange(actual: actual.G * 255f, low: g - 1.5f, high: g + 1.5f);
        Assert.InRange(actual: actual.B * 255f, low: b - 1.5f, high: b + 1.5f);
    }

    [Fact]
    public void EveryStandaloneAccentClearsWcagAa()
    {
        foreach (var accent in Enum.GetValues<AdwAccent>())
        foreach (bool dark in new[] {
                     false,
                     true,
                 })
        {
            var theme = AdwTheme.Create(accent: accent, dark: dark);
            var surface = dark ? AdwPalette.Dark.WindowBg : AdwPalette.Light.WindowBg;
            Assert.True(
                condition: Ratio(a: theme.PrimaryDark, b: surface) >= 4.5f,
                userMessage: $"{accent} {(dark ? "dark" : "light")} standalone accent is below AA"
            );
        }

        return;

        static float Ratio(Color a, Color b)
        {
            static float L(Color c)
            {
                static float Ch(float v) => v <= 0.03928f
                    ? v / 12.92f
                    : MathF.Pow(x: (v + 0.055f) / 1.055f, y: 2.4f);

                return (0.2126f * Ch(c.R)) + (0.7152f * Ch(c.G)) + (0.0722f * Ch(c.B));
            }

            float la = L(a), lb = L(b);
            return (MathF.Max(x: la, y: lb) + 0.05f) / (MathF.Min(x: la, y: lb) + 0.05f);
        }
    }

    /// <summary>
    ///     A ComposedWidget that returns the same retained child must not tear it down on rebuild —
    ///     that is what used to clear focus on every property change.
    /// </summary>
    /// <summary>
    ///     Sandboxed apps get the accent as a raw sRGB triple from
    ///     <c>org.freedesktop.portal.Settings</c> rather than by name, so every hue has to survive
    ///     the round trip back to its enum — and "no accent set" must not snap to the nearest hue.
    /// </summary>
    [Fact]
    public void PortalAccentTriplesMapBackToTheirNamedHue()
    {
        foreach (var accent in Enum.GetValues<AdwAccent>())
        {
            var c = AdwAccentColors.Bg(accent);
            Assert.Equal(expected: accent, actual: AdwAccentColors.Nearest(r: c.R, g: c.G, b: c.B));
            // A desktop that tweaks its accent slightly still lands on the same hue.
            Assert.Equal(
                expected: accent,
                actual: AdwAccentColors.Nearest(r: c.R + 0.01f, g: c.G - 0.01f, b: c.B + 0.01f)
            );
        }

        Assert.Null(AdwAccentColors.Nearest(r: -1f, g: -1f, b: -1f));
    }

    [Fact]
    public void RebuildKeepsARetainedChildAttached()
    {
        var w = new RetainedRoot();
        w.Measure(
            new Constraints(
                minWidth: 0,
                maxWidth: 100,
                minHeight: 0,
                maxHeight: 100
            )
        );
        var first = w.Root;

        w.Invalidate();
        w.Measure(
            new Constraints(
                minWidth: 0,
                maxWidth: 100,
                minHeight: 0,
                maxHeight: 100
            )
        );

        Assert.Same(expected: first, actual: w.Root);
        Assert.Equal(expected: 0, actual: w.Root.DetachCount);
    }

    // The other half of this contract — that a retained root whose CONTENTS changed still gets the
    // attach cascade, so newly-inserted descendants receive an Owner — is not covered here: it is
    // only observable with a live App, which needs the native window and cannot be built headless.
    private sealed class RetainedRoot : ComposedWidget
    {
        public CountingBox Root { get; } = new();

        protected override Widget Build(BuildContext context) => Root;
    }

    internal sealed class CountingBox : Widget
    {
        public int DetachCount { get; private set; }

        public override Size Measure(Constraints c) => Size.Zero;

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: 0f,
                height: 0f
            );
        }

        public override void Paint(PaintList paint) { }

        public override void Detach()
        {
            DetachCount++;
            base.Detach();
        }
    }
}
