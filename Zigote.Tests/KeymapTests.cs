using Xunit;
using Zigote.Core.Events;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

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
            down: true,
            keyChar: 'a',
            scancode: (uint)KeyCode.A,
            modifiers: Modifiers.None,
            repeat: true
        );
        Assert.Equal(expected: KeyCode.A, actual: held.Key);
        Assert.True(held.Repeat);

        var press = new KeyEvent(
            down: true,
            keyChar: 'a',
            scancode: (uint)KeyCode.A,
            modifiers: Modifiers.None
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
        Assert.True(KeyChord.TryParse(text: text, chord: out var c));
        Assert.Equal(expected: key, actual: c.Key);
        Assert.Equal(expected: mods, actual: c.Modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Foo+Z")]
    [InlineData("NotAKey")]
    public void Chord_RejectsGarbage(string text) =>
        Assert.False(KeyChord.TryParse(text: text, chord: out _));

    [Fact]
    public void Chord_RoundTripsThroughString()
    {
        var c = new KeyChord(Key: KeyCode.P, Modifiers: Modifiers.Cmd | Modifiers.Shift);
        Assert.True(KeyChord.TryParse(text: c.ToString(), chord: out var back));
        Assert.Equal(expected: c, actual: back);
    }

    [Fact]
    public void Chord_MatchesModifiersExactly()
    {
        var c = new KeyChord(Key: KeyCode.S, Modifiers: Modifiers.Ctrl);
        Assert.True(c.Matches(key: KeyCode.S, modifiers: Modifiers.Ctrl));
        Assert.False(c.Matches(key: KeyCode.S, modifiers: Modifiers.Ctrl | Modifiers.Shift));
        Assert.False(c.Matches(key: KeyCode.S, modifiers: Modifiers.None));
    }

    [Fact]
    public void Command_UsesPlatformModifier()
    {
        var c = KeyChord.Command(KeyCode.C);
        Assert.Equal(expected: KeyChord.PlatformCommand, actual: c.Modifiers);
        Assert.True(c.Matches(key: KeyCode.C, modifiers: KeyChord.PlatformCommand));
    }

    [Fact]
    public void Keymap_BindResolveRebindUnbind()
    {
        var cmd = KeyChord.PlatformCommand;
        var km = new Keymap();
        km.Bind(action: "edit.copy", chord: KeyChord.Command(KeyCode.C));

        Assert.Equal(expected: "edit.copy", actual: km.Resolve(key: KeyCode.C, modifiers: cmd));
        Assert.True(km.IsBound(action: "edit.copy", key: KeyCode.C, modifiers: cmd));

        km.Rebind(
            action: "edit.copy",
            chord: new KeyChord(Key: KeyCode.Y, Modifiers: Modifiers.Ctrl)
        );
        Assert.Null(km.Resolve(key: KeyCode.C, modifiers: cmd)); // old chord gone
        Assert.Equal(
            expected: "edit.copy",
            actual: km.Resolve(key: KeyCode.Y, modifiers: Modifiers.Ctrl)
        );

        km.Unbind("edit.copy");
        Assert.Null(km.Resolve(key: KeyCode.Y, modifiers: Modifiers.Ctrl));
    }

    [Fact]
    public void Keymap_ResolvesKeyEvent_DownOnly()
    {
        var km = new Keymap();
        km.Bind(action: "app.save", chord: new KeyChord(Key: KeyCode.S, Modifiers: Modifiers.Ctrl));

        var down = new KeyEvent(
            down: true,
            keyChar: 's',
            scancode: (uint)KeyCode.S,
            modifiers: Modifiers.Ctrl
        );
        var up = new KeyEvent(
            down: false,
            keyChar: 's',
            scancode: (uint)KeyCode.S,
            modifiers: Modifiers.Ctrl
        );
        Assert.Equal(expected: "app.save", actual: km.Resolve(down));
        Assert.Null(km.Resolve(up)); // key-up never resolves
    }

    [Fact]
    public void Keymap_ExportLoad_RoundTrips()
    {
        var km = new Keymap();
        km.Bind(action: "a.x", chord: new KeyChord(Key: KeyCode.X, Modifiers: Modifiers.Ctrl));
        km.Bind(action: "a.y", chord: new KeyChord(KeyCode.F2));
        var dump = km.Export().ToList();

        var restored = new Keymap();
        restored.Load(dump);
        Assert.Equal(
            expected: "a.x",
            actual: restored.Resolve(key: KeyCode.X, modifiers: Modifiers.Ctrl)
        );
        Assert.Equal(
            expected: "a.y",
            actual: restored.Resolve(key: KeyCode.F2, modifiers: Modifiers.None)
        );
    }

    [Theory]
    [InlineData(Modifiers.None, true, false)] // Space while typing in a search box is a space
    [InlineData(Modifiers.Shift, true, false)] // Shift+Space too
    [InlineData(Modifiers.Ctrl, true, true)] // Ctrl+F is a command, focus or not
    [InlineData(Modifiers.Alt, true, true)]
    [InlineData(Modifiers.None, false, true)] // nothing typing — Space is play/pause
    public void Shortcut_YieldsToFocusedEditor_OnlyWhenUnmodified(
        Modifiers mods, bool editorFocused, bool expected)
    {
        Widget? focused = editorFocused ? new Editor() : new SizedBox(width: 1f, height: 1f);
        Assert.Equal(
            expected: expected,
            actual: App.ShortcutOutranksFocus(modifiers: mods, focused: focused)
        );
    }

    private sealed class Editor : SizedBox, ITextInputClient;
}
