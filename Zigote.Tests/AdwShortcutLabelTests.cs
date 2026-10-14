using Xunit;
using Zigote.UI.Adwaita;

namespace Zigote.Tests;

/// <summary>
///     Accelerator parsing is the whole of <see cref="AdwShortcutLabel" />, and the part libadwaita
///     1.10 corrected: modifiers are shown in a fixed natural order rather than the order they were
///     typed, so two spellings of the same shortcut never render differently.
/// </summary>
public class AdwShortcutLabelTests
{
    // The caps are platform-specific (macOS uses glyphs and maps <Primary> to Command), so the
    // spelling under test is pinned rather than taken from the machine running the tests.
    private static List<string> Parse(string accel, bool mac = false) =>
        AdwShortcutLabel.Parse(accelerator: accel, mac: mac);

    [Fact]
    public void MacUsesGlyphsAndMapsPrimaryToCommand()
    {
        Assert.Equal(
            expected: ["⌃", "⌥", "⇧", "N"],
            actual: Parse(accel: "<Shift><Alt><Ctrl>n", mac: true)
        );
        Assert.Equal(expected: ["⌘", "S"], actual: Parse(accel: "<Primary>s", mac: true));
        Assert.Equal(expected: ["⌃", "↩"], actual: Parse(accel: "<Ctrl>Return", mac: true));
    }

    [Fact]
    public void ModifiersAreEmittedInNaturalOrderRegardlessOfHowTheyWereTyped()
    {
        // Ctrl, Alt, Shift, Super — whatever order the accelerator lists them in.
        Assert.Equal(
            expected: Parse("<Control><Shift>n"),
            actual: Parse("<Shift><Control>n")
        );
        Assert.Equal(
            expected: ["Ctrl", "Alt", "Shift", "N"],
            actual: Parse("<Shift><Alt><Ctrl>n")
        );
    }

    [Theory]
    [InlineData("<Ctrl>s", "Ctrl", "S")]
    [InlineData("<Control>s", "Ctrl", "S")]
    [InlineData("<Primary>s", "Ctrl", "S")]
    public void ControlSpellingsAreEquivalent(string accel, string mod, string key) => Assert.Equal(
        expected: [mod, key],
        actual: Parse(accel)
    );

    [Fact]
    public void NamedKeysGetTheirPrintableCap()
    {
        Assert.Equal(expected: ["Ctrl", "Enter"], actual: Parse("<Ctrl>Return"));
        Assert.Equal(expected: ["←"], actual: Parse("Left"));
        Assert.Equal(expected: ["Ctrl", "+"], actual: Parse("<Ctrl>plus"));
        // F-keys keep their spelling; a bare letter is capitalised.
        Assert.Equal(expected: ["F11"], actual: Parse("F11"));
        Assert.Equal(expected: ["A"], actual: Parse("a"));
    }

    [Fact]
    public void AnUnknownModifierIsDroppedRatherThanShownAsABogusCap() => Assert.Equal(
        expected: ["Ctrl", "K"],
        actual: Parse("<Hyper><Ctrl>k")
    );

    [Fact]
    public void AnEmptyAcceleratorParsesToNothing()
    {
        Assert.Empty(Parse(""));
        Assert.Empty(Parse("   "));
    }

    /// <summary>A modifier-only accelerator is legal input; it must not invent a key cap.</summary>
    [Fact]
    public void ModifierOnlyAcceleratorsYieldOnlyModifiers() => Assert.Equal(
        expected: ["Ctrl"],
        actual: Parse("<Ctrl>")
    );
}
