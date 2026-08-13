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
    [Fact]
    public void ModifiersAreEmittedInNaturalOrderRegardlessOfHowTheyWereTyped()
    {
        // Ctrl, Alt, Shift, Super — whatever order the accelerator lists them in.
        Assert.Equal(
            expected: AdwShortcutLabel.Parse("<Control><Shift>n"),
            actual: AdwShortcutLabel.Parse("<Shift><Control>n")
        );
        Assert.Equal(
            expected: ["Ctrl", "Alt", "Shift", "N"],
            actual: AdwShortcutLabel.Parse("<Shift><Alt><Ctrl>n")
        );
    }

    [Theory]
    [InlineData("<Ctrl>s", "Ctrl", "S")]
    [InlineData("<Control>s", "Ctrl", "S")]
    [InlineData("<Primary>s", "Ctrl", "S")]
    public void ControlSpellingsAreEquivalent(string accel, string mod, string key) => Assert.Equal(
        expected: [mod, key],
        actual: AdwShortcutLabel.Parse(accel)
    );

    [Fact]
    public void NamedKeysGetTheirPrintableCap()
    {
        Assert.Equal(expected: ["Ctrl", "Enter"], actual: AdwShortcutLabel.Parse("<Ctrl>Return"));
        Assert.Equal(expected: ["←"], actual: AdwShortcutLabel.Parse("Left"));
        Assert.Equal(expected: ["Ctrl", "+"], actual: AdwShortcutLabel.Parse("<Ctrl>plus"));
        // F-keys keep their spelling; a bare letter is capitalised.
        Assert.Equal(expected: ["F11"], actual: AdwShortcutLabel.Parse("F11"));
        Assert.Equal(expected: ["A"], actual: AdwShortcutLabel.Parse("a"));
    }

    [Fact]
    public void AnUnknownModifierIsDroppedRatherThanShownAsABogusCap() => Assert.Equal(
        expected: ["Ctrl", "K"],
        actual: AdwShortcutLabel.Parse("<Hyper><Ctrl>k")
    );

    [Fact]
    public void AnEmptyAcceleratorParsesToNothing()
    {
        Assert.Empty(AdwShortcutLabel.Parse(""));
        Assert.Empty(AdwShortcutLabel.Parse("   "));
    }

    /// <summary>A modifier-only accelerator is legal input; it must not invent a key cap.</summary>
    [Fact]
    public void ModifierOnlyAcceleratorsYieldOnlyModifiers() => Assert.Equal(
        expected: ["Ctrl"],
        actual: AdwShortcutLabel.Parse("<Ctrl>")
    );
}
