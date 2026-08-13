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
        Assert.True(MenuAccelerators.TryParse(shortcut: "⌘⇧Z", chord: out var glyph));
        Assert.True(MenuAccelerators.TryParse(shortcut: "Mod+Shift+Z", chord: out var written));
        Assert.Equal(expected: written, actual: glyph);
        Assert.Equal(
            expected: new KeyChord(
                Key: KeyCode.Z,
                Modifiers: KeyChord.PlatformCommand | Modifiers.Shift
            ),
            actual: glyph
        );
    }

    [Fact]
    public void NonLetterKeysSurvive()
    {
        Assert.True(MenuAccelerators.TryParse(shortcut: "F5", chord: out var f5));
        Assert.Equal(expected: new KeyChord(KeyCode.F5), actual: f5);
        // The globe modifier has no cross-platform meaning — stripped, not mistaken for the key.
        Assert.True(MenuAccelerators.TryParse(shortcut: "🌐E", chord: out var globe));
        Assert.Equal(expected: new KeyChord(KeyCode.E), actual: globe);
        Assert.False(
            MenuAccelerators.TryParse(shortcut: "⌘", chord: out _)
        ); // modifier alone binds nothing
        Assert.False(MenuAccelerators.TryParse(shortcut: null, chord: out _));
    }

    /// <summary>A label the local platform can read: glyphs on macOS, "Ctrl+S" everywhere else.</summary>
    [Fact]
    public void DisplayIsPlatformSpelled()
    {
        string? shown = MenuAccelerators.Display("⌘S");
        Assert.Equal(expected: OperatingSystem.IsMacOS() ? "⌘S" : "Ctrl+S", actual: shown);
        // Not a chord at all: passed through, so a hand-written label still renders.
        Assert.Equal(expected: "hold ⌥", actual: MenuAccelerators.Display("hold ⌥"));
    }

    [Fact]
    public void CollectWalksSubmenusAndSkipsWhatCannotFire()
    {
        int fired = 0;
        var menus = new[] {
            new AppMenu(
                Title: "File",
                Items: [
                    new ContextMenuItem(Label: "Save", OnSelect: () => fired++, Shortcut: "⌘S"),
                    new ContextMenuItem(Label: "", OnSelect: null, Separator: true), // separator
                    new ContextMenuItem(Label: "No action", OnSelect: null, Shortcut: "⌘K"),
                    new ContextMenuItem(
                        Label: "Disabled",
                        OnSelect: () => fired += 100,
                        Shortcut: "⌘D",
                        Enabled: false
                    ),
                    new ContextMenuItem(
                        Label: "Recent",
                        OnSelect: null,
                        Children: [
                            new ContextMenuItem(
                                Label: "a.zigote",
                                OnSelect: () => fired += 10,
                                Shortcut: "⌘1"
                            ),
                        ]
                    ),
                ]
            ),
        };

        var accel = MenuAccelerators.Collect(menus);
        Assert.Equal(expected: 2, actual: accel.Count);

        foreach (var (chord, run) in accel)
        {
            if (chord.Matches(key: KeyCode.S, modifiers: KeyChord.PlatformCommand))
                run();
        }

        Assert.Equal(expected: 1, actual: fired);
    }
}
