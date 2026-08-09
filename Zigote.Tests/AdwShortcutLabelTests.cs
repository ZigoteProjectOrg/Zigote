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
            AdwShortcutLabel.Parse("<Control><Shift>n"),
            AdwShortcutLabel.Parse("<Shift><Control>n")
        );
        Assert.Equal(["Ctrl", "Alt", "Shift", "N"], AdwShortcutLabel.Parse("<Shift><Alt><Ctrl>n"));
    }

    [Theory]
    [InlineData("<Ctrl>s", "Ctrl", "S")]
    [InlineData("<Control>s", "Ctrl", "S")]
    [InlineData("<Primary>s", "Ctrl", "S")]
    public void ControlSpellingsAreEquivalent(string accel, string mod, string key)
    {
        Assert.Equal([mod, key], AdwShortcutLabel.Parse(accel));
    }

    [Fact]
    public void NamedKeysGetTheirPrintableCap()
    {
        Assert.Equal(["Ctrl", "Enter"], AdwShortcutLabel.Parse("<Ctrl>Return"));
        Assert.Equal(["←"], AdwShortcutLabel.Parse("Left"));
        Assert.Equal(["Ctrl", "+"], AdwShortcutLabel.Parse("<Ctrl>plus"));
        // F-keys keep their spelling; a bare letter is capitalised.
        Assert.Equal(["F11"], AdwShortcutLabel.Parse("F11"));
        Assert.Equal(["A"], AdwShortcutLabel.Parse("a"));
    }

    [Fact]
    public void AnUnknownModifierIsDroppedRatherThanShownAsABogusCap()
    {
        Assert.Equal(["Ctrl", "K"], AdwShortcutLabel.Parse("<Hyper><Ctrl>k"));
    }

    [Fact]
    public void AnEmptyAcceleratorParsesToNothing()
    {
        Assert.Empty(AdwShortcutLabel.Parse(""));
        Assert.Empty(AdwShortcutLabel.Parse("   "));
    }

    /// <summary>A modifier-only accelerator is legal input; it must not invent a key cap.</summary>
    [Fact]
    public void ModifierOnlyAcceleratorsYieldOnlyModifiers()
    {
        Assert.Equal(["Ctrl"], AdwShortcutLabel.Parse("<Ctrl>"));
    }
}
