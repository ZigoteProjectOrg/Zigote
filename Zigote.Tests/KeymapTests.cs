using Xunit;
using Zigote.Core.Events;

namespace Zigote.Tests;

/// <summary>
///     The completed key model: <see cref="KeyCode" /> (physical, scancode-valued),
///     <see cref="KeyEvent" />
///     repeat/Key decode, and the configurable <see cref="Keymap" /> (parse, bind, resolve, rebind,
///     export/load). Pure policy — no native or UI.
/// </summary>
public class KeymapTests
{
    [Fact]
    public void KeyEvent_DecodesPhysicalKeyAndRepeat()
    {
        var held = new KeyEvent(
            true,
            'a',
            (uint)KeyCode.A,
            Modifiers.None,
            true
        );
        Assert.Equal(KeyCode.A, held.Key);
        Assert.True(held.Repeat);

        var press = new KeyEvent(
            true,
            'a',
            (uint)KeyCode.A,
            Modifiers.None
        );
        Assert.False(press.Repeat); // defaults to false
    }

    [Theory]
    [InlineData("Cmd+Shift+P", KeyCode.P, Modifiers.Cmd | Modifiers.Shift)]
    [InlineData("Ctrl+S", KeyCode.S, Modifiers.Ctrl)]
    [InlineData("alt+enter", KeyCode.Enter, Modifiers.Alt)]
    [InlineData("F5", KeyCode.F5, Modifiers.None)]
    [InlineData("Escape", KeyCode.Escape, Modifiers.None)]
    [InlineData("3", KeyCode.Digit3, Modifiers.None)]
    public void Chord_Parses(string text, KeyCode key, Modifiers mods)
    {
        Assert.True(KeyChord.TryParse(text, out var c));
        Assert.Equal(key, c.Key);
        Assert.Equal(mods, c.Modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Foo+Z")]
    [InlineData("NotAKey")]
    public void Chord_RejectsGarbage(string text)
    {
        Assert.False(KeyChord.TryParse(text, out _));
    }

    [Fact]
    public void Chord_RoundTripsThroughString()
    {
        var c = new KeyChord(KeyCode.P, Modifiers.Cmd | Modifiers.Shift);
        Assert.True(KeyChord.TryParse(c.ToString(), out var back));
        Assert.Equal(c, back);
    }

    [Fact]
    public void Chord_MatchesModifiersExactly()
    {
        var c = new KeyChord(KeyCode.S, Modifiers.Ctrl);
        Assert.True(c.Matches(KeyCode.S, Modifiers.Ctrl));
        Assert.False(c.Matches(KeyCode.S, Modifiers.Ctrl | Modifiers.Shift));
        Assert.False(c.Matches(KeyCode.S, Modifiers.None));
    }

    [Fact]
    public void Command_UsesPlatformModifier()
    {
        var c = KeyChord.Command(KeyCode.C);
        Assert.Equal(KeyChord.PlatformCommand, c.Modifiers);
        Assert.True(c.Matches(KeyCode.C, KeyChord.PlatformCommand));
    }

    [Fact]
    public void Keymap_BindResolveRebindUnbind()
    {
        var cmd = KeyChord.PlatformCommand;
        var km = new Keymap();
        km.Bind("edit.copy", KeyChord.Command(KeyCode.C));

        Assert.Equal("edit.copy", km.Resolve(KeyCode.C, cmd));
        Assert.True(km.IsBound("edit.copy", KeyCode.C, cmd));

        km.Rebind("edit.copy", new KeyChord(KeyCode.Y, Modifiers.Ctrl));
        Assert.Null(km.Resolve(KeyCode.C, cmd)); // old chord gone
        Assert.Equal("edit.copy", km.Resolve(KeyCode.Y, Modifiers.Ctrl));

        km.Unbind("edit.copy");
        Assert.Null(km.Resolve(KeyCode.Y, Modifiers.Ctrl));
    }

    [Fact]
    public void Keymap_ResolvesKeyEvent_DownOnly()
    {
        var km = new Keymap();
        km.Bind("app.save", new KeyChord(KeyCode.S, Modifiers.Ctrl));

        var down = new KeyEvent(
            true,
            's',
            (uint)KeyCode.S,
            Modifiers.Ctrl
        );
        var up = new KeyEvent(
            false,
            's',
            (uint)KeyCode.S,
            Modifiers.Ctrl
        );
        Assert.Equal("app.save", km.Resolve(down));
        Assert.Null(km.Resolve(up)); // key-up never resolves
    }

    [Fact]
    public void Keymap_ExportLoad_RoundTrips()
    {
        var km = new Keymap();
        km.Bind("a.x", new KeyChord(KeyCode.X, Modifiers.Ctrl));
        km.Bind("a.y", new KeyChord(KeyCode.F2));
        var dump = km.Export().ToList();

        var restored = new Keymap();
        restored.Load(dump);
        Assert.Equal("a.x", restored.Resolve(KeyCode.X, Modifiers.Ctrl));
        Assert.Equal("a.y", restored.Resolve(KeyCode.F2, Modifiers.None));
    }
}