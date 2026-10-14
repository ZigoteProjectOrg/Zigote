using Xunit;
using Zigote.Core;
using Zigote.Core.Events;
using Zigote.UI.TextShaping;

namespace Zigote.Tests;

/// <summary>
///     The IDE-style editing commands of <see cref="AdwCodeEditor" />: multi-cursor (⌘D, ⌘⇧L,
///     add-cursor-below), bracket pairs, line commands, word movement and incremental find. Headless,
///     driven through the same OnKey / OnTextInput entry points the app uses.
/// </summary>
public class CodeEditorEditingTests
{
    private const uint Backspace = 42, Tab = 43, Return = 40, Escape = 41;
    private const uint Right = 79, Left = 80, Down = 81, Up = 82, End = 77, Home = 74;

    private static AdwCodeEditor Editor(string text)
    {
        var editor = new AdwCodeEditor(text);
        // Laid out, so edits keep the visual rows (which caret movement reads) in step.
        editor.Measure(Constraints.Tight(width: 600f, height: 400f));
        editor.Layout(Offset.Zero);
        return editor;
    }

    private static void Key(AdwCodeEditor e, uint scancode, Modifiers mods = Modifiers.None) =>
        e.OnKey(
            keyChar: '\0',
            scancode: scancode,
            down: true,
            mods: mods
        );

    private static void Chord(AdwCodeEditor e, char key, Modifiers mods = Modifiers.Cmd) =>
        e.OnKey(
            keyChar: key,
            scancode: 0,
            down: true,
            mods: mods
        );

    private static void Type(AdwCodeEditor e, string text)
    {
        foreach (char c in text) e.OnTextInput(c.ToString());
    }

    [Fact]
    public void NextOccurrence_AddsCursorsOnWholeWords_AndTypingReplacesThemAll()
    {
        var e = Editor("foo foobar foo\nfoo");
        Chord(e: e, key: 'd'); // selects the word under the caret
        Chord(e: e, key: 'd');
        Chord(e: e, key: 'd');
        Chord(
            e: e,
            key: 'd'
        ); // wraps back onto the first: no fourth cursor, "foobar" is not a word match
        Type(e: e, text: "x");
        Assert.Equal(expected: "x foobar x\nx", actual: e.Text);

        e.Undo(); // one step for all three carets
        Assert.Equal(expected: "foo foobar foo\nfoo", actual: e.Text);
    }

    [Fact]
    public void SelectAllOccurrences_ThenEscape_ReturnsToOneCursor()
    {
        var e = Editor("a1 a1 a1");
        Chord(e: e, key: 'l', mods: Modifiers.Cmd | Modifiers.Shift);
        Type(e: e, text: "b");
        Assert.Equal(expected: "b b b", actual: e.Text);

        Key(e: e, scancode: Escape);
        Type(e: e, text: "!");
        Assert.Equal(expected: "b!", actual: e.Text[..2]);
        Assert.Equal(expected: 6, actual: e.Text.Length);
    }

    [Fact]
    public void AddCursorBelow_EditsEveryLine_IncludingSameLineShifts()
    {
        var e = Editor("ab\ncd\nef");
        Key(e: e, scancode: Down, mods: Modifiers.Cmd | Modifiers.Alt);
        Key(e: e, scancode: Down, mods: Modifiers.Cmd | Modifiers.Alt);
        Type(e: e, text: "x");
        Key(e: e, scancode: Right);
        Type(e: e, text: "y");
        Key(e: e, scancode: Backspace);
        Key(e: e, scancode: Backspace);
        Assert.Equal(expected: "xb\nxd\nxf", actual: e.Text);
    }

    [Fact]
    public void Pairs_AutoClose_TypeOver_SurroundAndDeleteTogether()
    {
        var e = Editor("");
        Type(e: e, text: "f(");
        Assert.Equal(expected: "f()", actual: e.Text);
        Type(e: e, text: "a)"); // the ) steps over the one already there
        Assert.Equal(expected: "f(a)", actual: e.Text);

        Type(e: e, text: "[");
        Key(e: e, scancode: Backspace); // an empty pair goes as one
        Assert.Equal(expected: "f(a)", actual: e.Text);

        Type(e: e, text: " don't"); // an apostrophe inside a word is not a quote
        Assert.Equal(expected: "f(a) don't", actual: e.Text);

        var s = Editor("word");
        Chord(e: s, key: 'd');
        Type(e: s, text: "\"");
        Assert.Equal(expected: "\"word\"", actual: s.Text);
        Type(e: s, text: "x"); // the wrapped text is still selected
        Assert.Equal(expected: "\"x\"", actual: s.Text);
    }

    [Fact]
    public void Enter_IndentsAfterBracket_AndBraceOutdentsItself()
    {
        var e = Editor("");
        Type(e: e, text: "if {");
        Key(e: e, scancode: Return);
        Assert.Equal(expected: "if {\n    \n}", actual: e.Text);

        var o = Editor("    x");
        Key(e: o, scancode: End);
        Key(e: o, scancode: Return); // keeps the indent
        Type(e: o, text: "(");
        Key(e: o, scancode: End);
        Key(e: o, scancode: Return);
        Key(e: o, scancode: Return);
        Assert.Equal(expected: "    x\n    ()\n    \n    ", actual: o.Text);
        Key(e: o, scancode: Tab);
        Type(e: o, text: "}");
        Assert.Equal(expected: "    x\n    ()\n    \n    }", actual: o.Text);
    }

    [Fact]
    public void ToggleComment_AlignsAtShallowestIndent_AndRoundTrips()
    {
        var e = Editor("a\n    b\n\nc");
        Chord(e: e, key: 'a');
        e.ToggleLineComment();
        Assert.Equal(expected: "// a\n//     b\n\n// c", actual: e.Text);
        e.ToggleLineComment();
        Assert.Equal(expected: "a\n    b\n\nc", actual: e.Text);
    }

    [Fact]
    public void LineCommands_MoveDuplicateDeleteIndent()
    {
        var e = Editor("one\ntwo\nthree");
        Key(e: e, scancode: Down, mods: Modifiers.Alt);
        Assert.Equal(expected: "two\none\nthree", actual: e.Text);
        Key(e: e, scancode: Up, mods: Modifiers.Alt);
        Assert.Equal(expected: "one\ntwo\nthree", actual: e.Text);

        Key(e: e, scancode: Down, mods: Modifiers.Alt | Modifiers.Shift);
        Assert.Equal(expected: "one\none\ntwo\nthree", actual: e.Text);
        Type(e: e, text: "X"); // the caret rode down onto the copy
        Assert.Equal(expected: "one\nXone\ntwo\nthree", actual: e.Text);

        Chord(e: e, key: 'k', mods: Modifiers.Cmd | Modifiers.Shift);
        Assert.Equal(expected: "one\ntwo\nthree", actual: e.Text);

        Chord(e: e, key: 'a');
        Key(e: e, scancode: Tab);
        Assert.Equal(expected: "    one\n    two\n    three", actual: e.Text);
        Key(e: e, scancode: Tab, mods: Modifiers.Shift);
        Assert.Equal(expected: "one\ntwo\nthree", actual: e.Text);
    }

    [Fact]
    public void WordKeys_MoveAndDeleteByWord_AndHomeIsSmart()
    {
        var e = Editor("    foo.bar baz");
        Key(e: e, scancode: End);
        Key(e: e, scancode: Backspace, mods: Modifiers.Alt);
        Assert.Equal(expected: "    foo.bar ", actual: e.Text);
        Key(e: e, scancode: Left, mods: Modifiers.Alt); // before "bar"
        Key(e: e, scancode: Left, mods: Modifiers.Alt); // before "."
        Type(e: e, text: "!");
        Assert.Equal(expected: "    foo!.bar ", actual: e.Text);

        Key(e: e, scancode: Home); // first non-blank
        Type(e: e, text: "a");
        Key(e: e, scancode: Home);
        Key(e: e, scancode: Home); // then column 0
        Type(e: e, text: "b");
        Assert.Equal(expected: "b    afoo!.bar ", actual: e.Text);
    }

    [Fact]
    public void Find_TypesIntoTheQuery_StepsMatches_AndFeedsSelectAll()
    {
        var e = Editor("Bar bar\nbaz bar");
        Chord(e: e, key: 'f');
        Type(e: e, text: "bar"); // smart case: matches "Bar" too
        Assert.Equal(
            expected: "Bar bar\nbaz bar",
            actual: e.Text
        ); // typed into the bar, not the text
        Key(e: e, scancode: Return);
        Key(e: e, scancode: Escape);
        Type(e: e, text: "X"); // the second match is selected
        Assert.Equal(expected: "Bar X\nbaz bar", actual: e.Text);

        Chord(e: e, key: 'f');
        Type(e: e, text: "ba");
        Chord(e: e, key: 'l', mods: Modifiers.Cmd | Modifiers.Shift);
        Type(e: e, text: "_");
        Assert.Equal(expected: "_r X\n_z _r", actual: e.Text);
    }

    [Theory]
    [InlineData(
        "foo bar",
        7,
        -1,
        4
    )]
    [InlineData(
        "foo bar",
        4,
        -1,
        0
    )] // skips the space, then the word
    [InlineData(
        "foo.bar",
        7,
        -1,
        4
    )]
    [InlineData(
        "foo.bar",
        4,
        -1,
        3
    )] // punctuation is its own stop
    [InlineData(
        "foo  bar",
        0,
        +1,
        3
    )]
    [InlineData(
        "foo  bar",
        3,
        +1,
        8
    )]
    [InlineData(
        "éx y",
        0,
        +1,
        3
    )] // never lands inside a grapheme
    public void
        WordBoundary_StopsWhereIdesDo(string text, int index, int direction, int expected) =>
        Assert.Equal(
            expected: expected,
            actual: TextNavigation.WordBoundary(text: text, index: index, direction: direction)
        );
}
