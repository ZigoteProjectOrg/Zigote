using PaintCommandKind = Zigote.Core.Native.ZgPaintOp;
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
    // The inspect socket's `theme dark|light` must land on the Adwaita palette, not the base
    // framework's ThemeData.Dark/Light — that mix (Adwaita widgets, foreign surfaces) is what
    // every Adwaita app showed before the base class handled it.
    [Theory]
    [InlineData("dark", true)]
    [InlineData(" Light ", false)]
    public void ThemeByNameInstallsTheAdwaitaPalette(string name, bool dark)
    {
        var app = new AdwaitaApp(theme: dark ? AdwTheme.Light : AdwTheme.Dark);
        Assert.True(app.SetThemeByName(name));
        Assert.Equal(expected: dark, actual: app.Theme.IsDark);
        Assert.Equal(
            expected: (dark ? AdwTheme.Dark : AdwTheme.Light).Background,
            actual: app.Theme.Background
        );
        Assert.False(app.SetThemeByName("sepia"));
    }

    private sealed class RecordingApp : AdwaitaApp
    {
        public readonly List<ThemeData> Seen = [];

        protected override void OnThemeChanged(ThemeData theme)
        {
            base.OnThemeChanged(theme);
            Seen.Add(theme);
        }
    }

    // Secondary windows are re-themed from this hook. It has to fire on the assignment itself:
    // when they were polled once a frame instead, they trailed the main window by one.
    [Fact]
    public void ThemeAssignmentNotifiesSynchronously()
    {
        var app = new RecordingApp();
        app.Seen.Clear(); // the constructor's own assignment
        app.Theme = AdwTheme.Dark;
        Assert.Same(expected: AdwTheme.Dark, actual: Assert.Single(app.Seen));
    }

    /// <summary>Every distinct fill colour a subtree emits, in paint order.</summary>
    private static List<Color> Fills(Widget root, ThemeProvider provider, float w = 300f,
        float h = 120f)
    {
        provider.Measure(
            new Constraints(
                minWidth: 0f,
                maxWidth: w,
                minHeight: 0f,
                maxHeight: h
            )
        );
        provider.Layout(new Offset(x: 0f, y: 0f));
        var paint = new PaintList();
        provider.Paint(paint);
        return [
            .. paint.DebugCommands
                .Where(c => c.Kind == PaintCommandKind.Rect)
                .Select(c => new Color(
                        r: c.ColorR,
                        g: c.ColorG,
                        b: c.ColorB,
                        a: c.ColorA
                    )
                ),
        ];
    }

    private static (ThemeProvider Provider, Widget Subject) Tree(Widget subject)
    {
        var provider = new ThemeProvider(data: AdwTheme.Light, child: subject);
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
            _ => new AdwPaned(
                first: new SizedBox(width: 10f, height: 10f),
                second: new SizedBox(width: 10f, height: 10f)
            ),
        };
        var (provider, _) = Tree(subject);

        var light = Fills(root: subject, provider: provider);
        provider.Data = AdwTheme.Dark;
        var dark = Fills(root: subject, provider: provider);

        Assert.NotEmpty(light);
        Assert.NotEqual(expected: light, actual: dark);
    }

    [Fact]
    public void ComposedWidgetsRepaintInTheNewTheme()
    {
        var button = new AdwButton(label: "Go", onPressed: () => { });
        var (provider, _) = Tree(button);

        var light = Fills(root: button, provider: provider);
        provider.Data = AdwTheme.Dark;
        var dark = Fills(root: button, provider: provider);

        Assert.NotEmpty(light);
        Assert.NotEqual(expected: light, actual: dark);
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
        var provider = new ThemeProvider(
            data: AdwTheme.Create(accent: AdwAccent.Blue, dark: true),
            child: slider
        );

        var blue = Fills(root: slider, provider: provider);
        provider.Data = AdwTheme.Create(accent: AdwAccent.Red, dark: true);
        var red = Fills(root: slider, provider: provider);

        Assert.NotEqual(expected: blue, actual: red);
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

        var light = Fills(root: group, provider: provider);
        provider.Data = AdwTheme.Dark;
        var dark = Fills(root: group, provider: provider);

        Assert.NotEmpty(light);
        Assert.NotEqual(expected: light, actual: dark);
    }
}
