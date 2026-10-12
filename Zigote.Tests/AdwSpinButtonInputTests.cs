using Xunit;
using Zigote.Core.Events;
using Zigote.UI.Adwaita;
using Zigote.UI.Semantics;

namespace Zigote.Tests;

/// <summary>
///     Typing into a spin button is a trust boundary: whatever arrives through the keyboard or a
///     paste has to end as a number inside the control's range, or not change the value at all.
/// </summary>
public class AdwSpinButtonInputTests
{
    private static void Key(AdwSpinButton spin, KeyCode key) =>
        spin.OnKey(keyChar: '\0', scancode: (uint)key, down: true, mods: default);

    private static string? Shown(AdwSpinButton spin)
    {
        var config = new SemanticsConfiguration();
        spin.DescribeSemantics(config);
        return config.Value;
    }

    [Fact]
    public void TypedNumberCommitsOnEnterAndReportsOnce()
    {
        var seen = new List<double>();
        var spin = new AdwSpinButton(value: 440, min: 415, max: 466, step: 1, onChanged: seen.Add);
        spin.OnTextInput("445");
        Assert.Equal(expected: 440, actual: spin.Value); // nothing changes until commit
        Key(spin: spin, key: KeyCode.Enter);
        Assert.Equal(expected: 445, actual: spin.Value);
        Assert.Equal(expected: [445d], actual: seen);
    }

    [Fact]
    public void OutOfRangeIsFlaggedWhileTypingAndClampedOnCommit()
    {
        var spin = new AdwSpinButton(value: 440, min: 415, max: 466);
        spin.OnTextInput("9999");
        var config = new SemanticsConfiguration();
        spin.DescribeSemantics(config);
        Assert.Contains(expectedSubstring: "Out of range", actualString: config.Hint);
        Key(spin: spin, key: KeyCode.Enter);
        Assert.Equal(expected: 466, actual: spin.Value);

        spin.OnTextInput("3");
        Key(spin: spin, key: KeyCode.Enter);
        Assert.Equal(expected: 415, actual: spin.Value);
    }

    [Fact]
    public void IntegerControlDropsLettersSignsAndSeparators()
    {
        var spin = new AdwSpinButton(value: 4, min: 1, max: 16);
        spin.OnTextInput("a-1.2e,x");
        Assert.Equal(expected: "12", actual: Shown(spin));
        Key(spin: spin, key: KeyCode.Enter);
        Assert.Equal(expected: 12, actual: spin.Value);
    }

    [Fact]
    public void DecimalsFollowTheStepAndAcceptACommaSeparator()
    {
        var spin = new AdwSpinButton(value: 0, min: -5, max: 5, step: 0.1);
        spin.OnTextInput("-1,2567.-");
        Assert.Equal(expected: "-1.2", actual: Shown(spin)); // one separator, one decimal, sign first only
        Key(spin: spin, key: KeyCode.Enter);
        Assert.Equal(expected: -1.2, actual: spin.Value, precision: 9);
    }

    [Fact]
    public void EmptyOrUnfinishedTextRevertsWithoutReporting()
    {
        int calls = 0;
        var spin = new AdwSpinButton(value: 0, min: -5, max: 5, step: 0.1, onChanged: _ => calls++);
        spin.OnTextInput("-");
        Key(spin: spin, key: KeyCode.Enter);
        spin.OnTextInput("7");
        Key(spin: spin, key: KeyCode.Backspace);
        Key(spin: spin, key: KeyCode.Enter);
        Assert.Equal(expected: 0, actual: spin.Value);
        Assert.Equal(expected: 0, actual: calls);
        Assert.Equal(expected: "0", actual: Shown(spin));
    }

    [Fact]
    public void SteppingTakesTheTypedNumberFirst()
    {
        var spin = new AdwSpinButton(value: 120, min: 20, max: 300);
        spin.OnTextInput("90");
        Key(spin: spin, key: KeyCode.Up);
        Assert.Equal(expected: 91, actual: spin.Value);
    }

    [Fact]
    public void AbsurdlyLongInputIsCapped()
    {
        var spin = new AdwSpinButton(value: 0, min: 0, max: 100);
        spin.OnTextInput(new string(c: '9', count: 400));
        Assert.Equal(expected: 15, actual: Shown(spin)!.Length);
        Key(spin: spin, key: KeyCode.Enter);
        Assert.Equal(expected: 100, actual: spin.Value);
    }
}
