using Xunit;
using Zigote.Core;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Tests;

/// <summary>
///     Swapping <see cref="ThemeProvider.Data" /> at runtime — an accent change, a light/dark flip,
///     GNOME publishing a new colour-scheme — has to reach every widget under it, whether it reads
///     the theme in Build (a ComposedWidget) or in Measure (a raw painting widget). A widget that
///     keeps painting the old colours is the visible half of the bug; a widget that keeps a
///     theme-derived colour in a retained box it only refreshes on rebuild is the invisible half.
/// </summary>
public class AdwThemeSwapTests
{
    /// <summary>Every distinct fill colour a subtree emits, in paint order.</summary>
    private static List<Color> Fills(Widget root, ThemeProvider provider, float w = 300f,
        float h = 120f)
    {
        provider.Measure(new Constraints(0f, w, 0f, h));
        provider.Layout(new Offset(0f, 0f));
        var paint = new PaintList();
        provider.Paint(paint);
        return [.. paint.DebugCommands
            .Where(c => c.Kind == (byte)PaintCommandKind.Rect)
            .Select(c => new Color(
                    c.ColorR,
                    c.ColorG,
                    c.ColorB,
                    c.ColorA
                )
            )];
    }

    private static (ThemeProvider Provider, Widget Subject) Tree(Widget subject)
    {
        var provider = new ThemeProvider(AdwTheme.Light, subject);
        return (provider, subject);
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("slider")]
    [InlineData("progress")]
    [InlineData("shortcut")]
    [InlineData("separator")]
    [InlineData("paned")]
    public void RawPaintingWidgetsRepaintInTheNewTheme(string kind)
    {
        Widget subject = kind switch {
            // OFF: an ON switch paints the accent track and a white knob, and the Adwaita accent is
            // the SAME colour in both appearances — a light/dark assertion on it proves nothing.
            "switch" => new AdwSwitch(),
            "slider" => new AdwSlider(0.5f),
            "progress" => new AdwProgressBar(0.5f),
            "shortcut" => new AdwShortcutLabel("<Primary>s"),
            "separator" => new AdwSeparator(),
            _ => new AdwPaned(new SizedBox(10f, 10f), new SizedBox(10f, 10f)),
        };
        var (provider, _) = Tree(subject);

        var light = Fills(subject, provider);
        provider.Data = AdwTheme.Dark;
        var dark = Fills(subject, provider);

        Assert.NotEmpty(light);
        Assert.NotEqual(light, dark);
    }

    [Fact]
    public void ComposedWidgetsRepaintInTheNewTheme()
    {
        var button = new AdwButton("Go", () => { });
        var (provider, _) = Tree(button);

        var light = Fills(button, provider);
        provider.Data = AdwTheme.Dark;
        var dark = Fills(button, provider);

        Assert.NotEmpty(light);
        Assert.NotEqual(light, dark);
    }

    /// <summary>
    ///     The accent is the case a light/dark test would miss: the appearance is unchanged, only
    ///     the hue moves, so anything keying off "is the theme dark" still looks right while every
    ///     accented surface is stale.
    /// </summary>
    [Fact]
    public void AnAccentOnlyChangeStillRepaints()
    {
        var slider = new AdwSlider(0.5f);
        var provider = new ThemeProvider(AdwTheme.Create(AdwAccent.Blue, true), slider);

        var blue = Fills(slider, provider);
        provider.Data = AdwTheme.Create(AdwAccent.Red, true);
        var red = Fills(slider, provider);

        Assert.NotEqual(blue, red);
    }

    /// <summary>
    ///     A widget that caches a theme-derived colour in a retained box (every Adwaita control with
    ///     a hover fade does) must refresh that box on a theme swap, not only on hover.
    /// </summary>
    [Fact]
    public void RetainedFillsAreRefreshedNotJustOnInteraction()
    {
        var group = new AdwToggleGroup(["One", "Two"]);
        var (provider, _) = Tree(group);

        var light = Fills(group, provider);
        provider.Data = AdwTheme.Dark;
        var dark = Fills(group, provider);

        Assert.NotEmpty(light);
        Assert.NotEqual(light, dark);
    }
}
