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
    private static float Lin(float c)
    {
        return MathF.Pow(MathF.Max(c, 0f), 2.2f);
    }

    /// <summary>Composite in linear space (what the shader does) and re-encode.</summary>
    private static float Composite(Color fg, Color bg)
    {
        var l = (1f - fg.A) * Lin(bg.G) + fg.A * Lin(fg.G);
        return MathF.Pow(l, 1f / 2.2f);
    }

    [Theory]
    [InlineData(false, 0.08f)] // light button fill
    [InlineData(false, 0.16f)] // light button :active
    [InlineData(true, 0.10f)] // dark button fill
    [InlineData(true, 0.20f)] // dark button :active
    public void OverlayAlphasReproduceTheStylesheetColour(bool dark, float cssAlpha)
    {
        var p = dark ? AdwPalette.Dark : AdwPalette.Light;
        var surface = dark ? p.WindowBg : p.WindowBg;
        var fill = cssAlpha switch {
            0.08f or 0.10f => p.ButtonFill,
            _ => p.ButtonFillActive,
        };

        // What GNOME's CSS means: blend in sRGB space.
        var tint = dark ? 1f : 6f / 255f;
        var expected = (1f - cssAlpha) * surface.G + cssAlpha * tint;

        // Overlay() averages the three solved channels, so the green-only reference here lands
        // within a fraction of a level rather than exactly.
        Assert.InRange(Composite(fill, surface), expected - 0.005f, expected + 0.005f);
    }

    [Fact]
    public void DarkNeutralFillIsNotBlownOutByLinearBlending()
    {
        // The pre-fix value (a literal .10 white) composited to ~#5e5e60 instead of ~#383839.
        var rendered = Composite(AdwPalette.Dark.ButtonFill, AdwPalette.Dark.WindowBg) * 255f;
        Assert.InRange(rendered, 50f, 62f);
    }

    [Fact]
    public void EveryStandaloneAccentClearsWcagAa()
    {
        foreach (var accent in Enum.GetValues<AdwAccent>())
        foreach (var dark in new[] {
                     false,
                     true,
                 })
        {
            var theme = AdwTheme.Create(accent, dark);
            var surface = dark ? AdwPalette.Dark.WindowBg : AdwPalette.Light.WindowBg;
            Assert.True(
                Ratio(theme.PrimaryDark, surface) >= 4.5f,
                $"{accent} {(dark ? "dark" : "light")} standalone accent is below AA"
            );
        }

        return;

        static float Ratio(Color a, Color b)
        {
            static float L(Color c)
            {
                static float Ch(float v)
                {
                    return v <= 0.03928f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
                }

                return 0.2126f * Ch(c.R) + 0.7152f * Ch(c.G) + 0.0722f * Ch(c.B);
            }

            float la = L(a), lb = L(b);
            return (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
        }
    }

    /// <summary>
    ///     A StatelessWidget that returns the same retained child must not tear it down on rebuild —
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
            Assert.Equal(accent, AdwAccentColors.Nearest(c.R, c.G, c.B));
            // A desktop that tweaks its accent slightly still lands on the same hue.
            Assert.Equal(accent, AdwAccentColors.Nearest(c.R + 0.01f, c.G - 0.01f, c.B + 0.01f));
        }

        Assert.Null(AdwAccentColors.Nearest(-1f, -1f, -1f));
    }

    [Fact]
    public void RebuildKeepsARetainedChildAttached()
    {
        var w = new RetainedRoot();
        w.Measure(
            new Constraints(
                0,
                100,
                0,
                100
            )
        );
        var first = w.Root;

        w.Invalidate();
        w.Measure(
            new Constraints(
                0,
                100,
                0,
                100
            )
        );

        Assert.Same(first, w.Root);
        Assert.Equal(0, w.Root.DetachCount);
    }

    // The other half of this contract — that a retained root whose CONTENTS changed still gets the
    // attach cascade, so newly-inserted descendants receive an Owner — is not covered here: it is
    // only observable with a live App, which needs the native window and cannot be built headless.
    private sealed class RetainedRoot : StatelessWidget
    {
        public CountingBox Root { get; } = new();

        protected override Widget Build(BuildContext context)
        {
            return Root;
        }
    }

    internal sealed class CountingBox : Widget
    {
        public int DetachCount { get; private set; }

        public override Size Measure(Constraints c)
        {
            return Size.Zero;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                0f,
                0f
            );
        }

        public override void Paint(PaintList paint)
        {
        }

        public override void Detach()
        {
            DetachCount++;
            base.Detach();
        }
    }
}