using Xunit;
using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;

namespace Zigote.Tests;

/// <summary>
///     Covers the editable-control additions: word-boundary selection (double-click), read-only mode
///     on
///     <see cref="TextField" />/<see cref="CodeEditor" />, and the platform "command" modifier
///     (<see cref="Modifiers.Cmd" />/Ctrl) used by copy-paste shortcuts. All headless — no window.
/// </summary>
public class TextEditingTests
{
    private static string Word(string text, int pos)
    {
        var (s, e) = TextNavigation.WordAt(text, pos);
        return text.Substring(s, e - s);
    }

    [Theory]
    [InlineData("hello world", 2, "hello")] // inside a word
    [InlineData("hello world", 6, "world")] // start of a word
    [InlineData("hello world", 5, "hello")] // caret just after a word selects it
    [InlineData("hi", 2, "hi")] // caret at end of string
    [InlineData("a + b", 2, "+")] // lone operator surrounded by spaces
    [InlineData("foo_bar baz", 3, "foo_bar")] // underscore is a word char
    [InlineData("", 0, "")] // empty
    public void WordAt_SelectsExpectedSpan(string text, int pos, string expected)
    {
        Assert.Equal(expected, Word(text, pos));
    }

    [Fact]
    public void TextField_ReadOnly_IgnoresTextInputAndBackspace()
    {
        var tf = new TextField {
            Text = "abc",
            ReadOnly = true,
        };
        tf.OnTextInput("X");
        tf.OnKey(
            '\0',
            42,
            true,
            Modifiers.None
        ); // backspace scancode
        Assert.Equal("abc", tf.Text);
    }

    [Fact]
    public void TextField_Editable_AcceptsTextInput()
    {
        var tf = new TextField { Text = "" };
        tf.OnTextInput("hi");
        Assert.Equal("hi", tf.Text);
    }

    /// <summary>
    ///     Setting <see cref="TextField.Text" /> externally must leave the caret inside the new
    ///     text. This is the "controlled value" pattern — the F# <c>Ui.textField</c> assigns Text
    ///     on every reconcile, so an app that clears its draft after sending goes through here —
    ///     and a caret left past the end used to throw on the next keystroke.
    /// </summary>
    [Fact]
    public void TextField_ExternalTextShrink_ClampsCaretSoNextInputSucceeds()
    {
        var tf = new TextField { Text = "" };
        tf.OnTextInput("a long draft message"); // caret now at the end
        tf.Text = ""; // e.g. the app cleared the field after sending
        tf.OnTextInput("x"); // must not throw
        Assert.Equal("x", tf.Text);
    }

    [Fact]
    public void TextField_ExternalTextReplace_KeepsCaretWithinBounds()
    {
        var tf = new TextField { Text = "" };
        tf.OnTextInput("0123456789");
        tf.Text = "abc"; // shorter than the current caret offset
        tf.OnTextInput("!");
        Assert.Equal("abc!", tf.Text);
    }

    [Fact]
    public void TextField_ExternalTextShrink_DropsStaleSelection()
    {
        var tf = new TextField { Text = "" };
        tf.OnTextInput("select all of this");
        tf.OnKey(
            'a',
            4,
            true,
            Modifiers.Cmd
        ); // ⌘A / Ctrl+A
        tf.Text = "hi"; // selection referred to the old, longer text
        tf.OnTextInput("!"); // must not delete a phantom selection or throw
        Assert.Equal("hi!", tf.Text);
    }

    [Fact]
    public void TextField_NullText_IsTreatedAsEmpty()
    {
        var tf = new TextField { Text = "abc" };
        tf.Text = null!;
        Assert.Equal("", tf.Text);
        tf.OnTextInput("x");
        Assert.Equal("x", tf.Text);
    }

    [Fact]
    public void CodeEditor_ReadOnly_IgnoresTextInput()
    {
        var ce = new CodeEditor("abc") { ReadOnly = true };
        ce.OnTextInput("X");
        Assert.Equal("abc", ce.Text);
    }

    [Fact]
    public void CodeEditor_Editable_AcceptsTextInput()
    {
        var ce = new CodeEditor();
        ce.OnTextInput("hi");
        Assert.Equal("hi", ce.Text);
    }

    [Fact]
    public void HasCommand_AcceptsCtrlOrCmd_NotAltOrNone()
    {
        Assert.True(Modifiers.Cmd.HasCommand());
        Assert.True(Modifiers.Ctrl.HasCommand());
        Assert.True((Modifiers.Cmd | Modifiers.Shift).HasCommand());
        Assert.False(Modifiers.Alt.HasCommand());
        Assert.False(Modifiers.None.HasCommand());
    }

    [Theory]
    [InlineData(
        "e\u0301x",
        2,
        0,
        2
    )] // combining acute stays attached to e
    [InlineData(
        "👨‍👩‍👧‍👦x",
        11,
        0,
        11
    )] // family ZWJ sequence is one caret unit
    [InlineData(
        "👍🏽x",
        4,
        0,
        4
    )] // emoji modifier stays attached
    public void GraphemeNavigation_DoesNotEnterTextElements(
        string text, int position, int expectedPrevious, int expectedNext)
    {
        Assert.Equal(expectedPrevious, TextNavigation.PreviousGraphemeBoundary(text, position));
        Assert.Equal(expectedNext, TextNavigation.NextGraphemeBoundary(text, 0));
    }

    [Fact]
    public void TextField_BackspaceAtEndDeletesOnlyLastCharacter()
    {
        // Regression: PreviousGraphemeBoundary(text, text.Length) used to collapse to 0, so backspacing
        // at the end of a multi-character string wiped the whole field.
        var typed = new TextField();
        typed.OnTextInput("abc");
        typed.OnKey(
            '\0',
            42,
            true,
            Modifiers.None
        );
        Assert.Equal("ab", typed.Text);
    }

    [Fact]
    public void TextField_BackspaceDeletesWholeGrapheme()
    {
        var field = new TextField();
        field.OnTextInput("e\u0301");
        field.OnKey(
            '\0',
            42,
            true,
            Modifiers.None
        );
        Assert.Equal(string.Empty, field.Text);
    }

    [Fact]
    public void CodeEditor_BackspaceDeletesWholeEmojiSequence()
    {
        var editor = new CodeEditor();
        editor.OnTextInput("👍🏽");
        editor.OnKey(
            '\0',
            42,
            true,
            Modifiers.None
        );
        Assert.Equal(string.Empty, editor.Text);
    }

    [Fact]
    public void ImeComposition_IsTransientUntilCommitted()
    {
        var field = new TextField();
        field.OnTextComposition("に", 1, 0);
        Assert.Equal(string.Empty, field.Text);

        field.OnTextInput("に");
        Assert.Equal("に", field.Text);

        var editor = new CodeEditor();
        editor.OnTextComposition("文", 1, 0);
        Assert.Equal(string.Empty, editor.Text);
        editor.OnTextInput("文");
        Assert.Equal("文", editor.Text);
    }

    // ── Multi-line TextField ────────────────────────────────────────────────────

    [Fact]
    public void Multiline_EnterInsertsNewline_SingleLineEnterSubmits()
    {
        var ml = new TextField { Multiline = true };
        ml.OnTextInput("ab");
        ml.OnKey(
            '\0',
            40,
            true,
            Modifiers.None
        ); // Return
        ml.OnTextInput("cd");
        Assert.Equal("ab\ncd", ml.Text);

        var submitted = false;
        var sl = new TextField { OnSubmitted = _ => submitted = true };
        sl.OnTextInput("ab");
        sl.OnKey(
            '\0',
            40,
            true,
            Modifiers.None
        );
        Assert.True(submitted);
        Assert.Equal("ab", sl.Text); // single-line Enter does not insert a break
    }

    [Fact]
    public void Multiline_CommandEnterSubmitsInsteadOfNewline()
    {
        var submitted = false;
        var ml = new TextField {
            Multiline = true,
            OnSubmitted = _ => submitted = true,
        };
        ml.OnTextInput("x");
        ml.OnKey(
            '\r',
            40,
            true,
            Modifiers.Cmd
        );
        Assert.True(submitted);
        Assert.Equal("x", ml.Text);
    }

    [Fact]
    public void Multiline_BackspaceMergesLines()
    {
        var ml = new TextField { Multiline = true };
        ml.OnTextInput("a");
        ml.OnKey(
            '\0',
            40,
            true,
            Modifiers.None
        ); // newline → "a\n"
        ml.OnTextInput("b"); // "a\nb"
        Assert.Equal("a\nb", ml.Text);
        ml.OnKey(
            '\0',
            42,
            true,
            Modifiers.None
        ); // backspace deletes 'b'
        ml.OnKey(
            '\0',
            42,
            true,
            Modifiers.None
        ); // backspace deletes the newline, merging lines
        Assert.Equal("a", ml.Text);
    }

    [Fact]
    public void Multiline_MeasureClampsHeightToMaxLines()
    {
        var c = new Constraints(
            0,
            200,
            0,
            1000
        );
        var capped = new TextField {
            Multiline = true,
            MinLines = 1,
            MaxLines = 3,
            Text = "a\nb\nc\nd\ne",
        };
        var tall = new TextField {
            Multiline = true,
            MinLines = 1,
            MaxLines = 10,
            Text = "a\nb\nc\nd\ne",
        };
        var cappedH = capped.Measure(c).Height;
        var tallH = tall.Measure(c).Height;
        Assert.True(tallH > cappedH, $"5 visible rows ({tallH}) should exceed 3 ({cappedH})");
    }

    [Fact]
    public void Multiline_ImeComposition_IsTransientUntilCommitted()
    {
        var ml = new TextField { Multiline = true };
        ml.OnTextInput("a");
        ml.OnKey(
            '\0',
            40,
            true,
            Modifiers.None
        ); // new line
        ml.OnTextComposition("に", 1, 0);
        Assert.Equal("a\n", ml.Text); // composition not committed into the document
        ml.OnTextInput("に");
        Assert.Equal("a\nに", ml.Text);
    }

    [Fact]
    public void Multiline_PaintEmitsAllVisibleLines()
    {
        var ml = new TextField {
            Multiline = true,
            Text = "one\ntwo\nthree",
            MaxLines = 8,
        };
        ml.Measure(Constraints.Loose(160f, 300f));
        ml.Layout(Offset.Zero);
        var paint = new PaintList();
        ml.Paint(paint);
        var textCommands =
            paint.DebugCommands.Count(cmd => cmd.Kind == (byte)PaintCommandKind.Text);
        Assert.True(textCommands >= 3, $"expected one draw per visible line, got {textCommands}");
    }

    [Fact]
    public void CodeEditor_SoftWrapCreatesAdditionalVisualRows()
    {
        var editor = new CodeEditor("alpha beta gamma delta") { SoftWrap = true };
        editor.Measure(Constraints.Tight(90f, 300f));
        editor.Layout(Offset.Zero);
        var paint = new PaintList();
        editor.Paint(paint);

        var textCommands = paint.DebugCommands.Count(c => c.Kind == (byte)PaintCommandKind.Text);
        Assert.True(
            textCommands >= 3,
            $"expected wrapped row draws plus gutter, got {textCommands}"
        );
    }

    // ── CodeEditor undo / redo / save ───────────────────────────────────────────

    [Fact]
    public void CodeEditor_UndoRevertsTypingThenRedoReapplies()
    {
        var editor = new CodeEditor();
        editor.OnTextInput("hello");
        Assert.Equal("hello", editor.Text);

        editor.Undo();
        Assert.Equal(string.Empty, editor.Text);

        editor.Redo();
        Assert.Equal("hello", editor.Text);
    }

    [Fact]
    public void CodeEditor_ConsecutiveTypingCoalescesIntoOneUndoStep()
    {
        // Headless time is fixed at 0, so same-kind edits within the coalesce window collapse — one
        // Undo should revert a whole typed run, not a single character.
        var editor = new CodeEditor();
        editor.OnTextInput("a");
        editor.OnTextInput("b");
        editor.OnTextInput("c");
        Assert.Equal("abc", editor.Text);

        editor.Undo();
        Assert.Equal(string.Empty, editor.Text);
    }

    [Fact]
    public void CodeEditor_NewlineIsASeparateUndoStepFromTyping()
    {
        var editor = new CodeEditor();
        editor.OnTextInput("ab");
        editor.OnKey(
            '\0',
            40,
            true,
            Modifiers.None
        ); // Return — distinct (Other) edit
        editor.OnTextInput("cd");
        Assert.Equal("ab\ncd", editor.Text);

        editor.Undo(); // undo the "cd" typing
        Assert.Equal("ab\n", editor.Text);
        editor.Undo(); // undo the newline
        Assert.Equal("ab", editor.Text);
    }

    [Fact]
    public void CodeEditor_UndoIsNoopWhenReadOnly()
    {
        var editor = new CodeEditor("frozen") { ReadOnly = true };
        editor.OnKey(
            'z',
            0,
            true,
            Modifiers.Cmd
        ); // ⌘Z
        Assert.Equal("frozen", editor.Text);
    }

    [Fact]
    public void CodeEditor_LoadingNewDocumentClearsUndoHistory()
    {
        var editor = new CodeEditor();
        editor.OnTextInput("draft");
        editor.Text = "loaded from disk"; // setter resets history
        editor.Undo(); // nothing to undo back into the previous document
        Assert.Equal("loaded from disk", editor.Text);
    }

    [Fact]
    public void CodeEditor_CommandZAndShiftZDriveUndoRedo()
    {
        var editor = new CodeEditor();
        editor.OnTextInput("x");
        editor.OnKey(
            'z',
            0,
            true,
            Modifiers.Cmd
        ); // ⌘Z → undo
        Assert.Equal(string.Empty, editor.Text);
        editor.OnKey(
            'z',
            0,
            true,
            Modifiers.Cmd | Modifiers.Shift
        ); // ⌘⇧Z → redo
        Assert.Equal("x", editor.Text);
    }

    [Fact]
    public void CodeEditor_CommandSInvokesOnSubmit()
    {
        var saved = 0;
        var editor = new CodeEditor("contents") { OnSubmit = () => saved++ };
        editor.OnKey(
            's',
            0,
            true,
            Modifiers.Cmd
        ); // ⌘S
        Assert.Equal(1, saved);
    }

    [Fact]
    public void CodeEditor_EditFiresOnChangedForDirtyTracking()
    {
        var changes = 0;
        var editor = new CodeEditor { OnChanged = _ => changes++ };
        editor.OnTextInput("z");
        Assert.True(changes >= 1, "an edit must fire OnChanged so a host can flag unsaved changes");
    }
}
