using Xunit;
using Zigote.Core.Events;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Menu;

namespace Zigote.Tests;

/// <summary>
///     Menu shortcuts only reach the user on macOS for free (NSMenu owns key equivalents); everywhere
///     else they are painted text until <see cref="MenuAccelerators" /> binds them. This pins the two
///     halves of that: the ⌘-authored model parses to the platform command chord, and the flattening
///     that feeds App.Accelerators picks up submenu items while leaving inert rows alone.
/// </summary>
public class MenuAcceleratorTests
{
    [Fact]
    public void MacGlyphsAndWrittenFormParseToTheSameChord()
    {
        Assert.True(MenuAccelerators.TryParse("⌘⇧Z", out var glyph));
        Assert.True(MenuAccelerators.TryParse("Mod+Shift+Z", out var written));
        Assert.Equal(written, glyph);
        Assert.Equal(new KeyChord(KeyCode.Z, KeyChord.PlatformCommand | Modifiers.Shift), glyph);
    }

    [Fact]
    public void NonLetterKeysSurvive()
    {
        Assert.True(MenuAccelerators.TryParse("F5", out var f5));
        Assert.Equal(new KeyChord(KeyCode.F5), f5);
        // The globe modifier has no cross-platform meaning — stripped, not mistaken for the key.
        Assert.True(MenuAccelerators.TryParse("🌐E", out var globe));
        Assert.Equal(new KeyChord(KeyCode.E), globe);
        Assert.False(MenuAccelerators.TryParse("⌘", out _)); // modifier alone binds nothing
        Assert.False(MenuAccelerators.TryParse(null, out _));
    }

    /// <summary>A label the local platform can read: glyphs on macOS, "Ctrl+S" everywhere else.</summary>
    [Fact]
    public void DisplayIsPlatformSpelled()
    {
        var shown = MenuAccelerators.Display("⌘S");
        Assert.Equal(OperatingSystem.IsMacOS() ? "⌘S" : "Ctrl+S", shown);
        // Not a chord at all: passed through, so a hand-written label still renders.
        Assert.Equal("hold ⌥", MenuAccelerators.Display("hold ⌥"));
    }

    [Fact]
    public void CollectWalksSubmenusAndSkipsWhatCannotFire()
    {
        var fired = 0;
        var menus = new[] {
            new AppMenu(
                "File",
                [
                    new ContextMenuItem("Save", () => fired++, Shortcut: "⌘S"),
                    new ContextMenuItem("", null, true), // separator
                    new ContextMenuItem("No action", null, Shortcut: "⌘K"),
                    new ContextMenuItem("Disabled", () => fired += 100, Shortcut: "⌘D",
                        Enabled: false),
                    new ContextMenuItem(
                        "Recent",
                        null,
                        Children: [new ContextMenuItem("a.zigote", () => fired += 10, Shortcut: "⌘1")]
                    ),
                ]
            ),
        };

        var accel = MenuAccelerators.Collect(menus);
        Assert.Equal(2, accel.Count);

        foreach (var (chord, run) in accel)
            if (chord.Matches(KeyCode.S, KeyChord.PlatformCommand))
                run();
        Assert.Equal(1, fired);
    }
}
