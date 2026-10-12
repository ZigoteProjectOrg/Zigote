using Xunit;
using Zigote.Editor.Widgets;
using Zigote.UI.Widgets.Menu;

namespace Zigote.Tests;

/// <summary>
///     The palette's ranking, and the shortcut strings the editor menus introduced: off macOS a
///     menu shortcut is only live if <see cref="MenuAccelerators" /> can parse it.
/// </summary>
public class EditorCommandPaletteTests
{
    private static readonly PaletteCommand[] Commands = [
        new(Title: "Save Scene As", Group: "File", Run: () => { }),
        new(Title: "Console", Group: "View", Run: () => { }),
        new(Title: "Save", Group: "File", Run: () => { }),
        new(Title: "Sphere", Group: "Node", Run: () => { }),
    ];

    [Fact]
    public void EmptyQueryKeepsEverythingInOrder() => Assert.Equal(
        expected: Commands,
        actual: CommandPalette.Filter(commands: Commands, query: "  ")
    );

    [Fact]
    public void PrefixMatchesRankAheadOfContainsAndCaseIsIgnored() => Assert.Equal(
        expected: ["Save Scene As", "Save", "Console"],
        actual: CommandPalette.Filter(commands: Commands, query: "s")
            .Select(c => c.Title)
            .Where(t => t != "Sphere")
    );

    [Fact]
    public void EveryTermMustMatchTitleOrGroup() => Assert.Equal(
        expected: ["Save Scene As"],
        actual: CommandPalette.Filter(commands: Commands, query: "file scene").Select(c => c.Title)
    );

    [Fact]
    public void InitialsFindACommandAndWordStartsOutrankScatteredLetters()
    {
        Assert.Equal(
            expected: ["Save Scene As"],
            actual: CommandPalette.Filter(commands: Commands, query: "svsc").Select(c => c.Title)
        );
        Assert.Equal(expected: -1, actual: CommandPalette.Score(text: "Save", term: "sx"));
        Assert.True(
            CommandPalette.Score(text: "Save Scene", term: "ss") >
            CommandPalette.Score(text: "Glass", term: "ss")
        );
    }

    [Theory]
    [InlineData("⌘⇧P")]
    [InlineData("⌘/")]
    [InlineData("⌘1")]
    [InlineData("⌘D")]
    [InlineData("F5")]
    [InlineData("F11")]
    public void EditorMenuShortcutsParse(string shortcut) =>
        Assert.True(MenuAccelerators.TryParse(shortcut: shortcut, chord: out _));
}
