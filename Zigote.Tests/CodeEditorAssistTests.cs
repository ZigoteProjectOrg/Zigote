using Xunit;
using Zigote.Core;
using Zigote.Core.Events;

namespace Zigote.Tests;

/// <summary>
///     The language-host assists of <see cref="AdwCodeEditor" />: the completion popup (trigger,
///     filter, accept, dismiss), markers and programmatic caret moves. Headless; the provider answers
///     synchronously, which the editor treats like any other completed task.
/// </summary>
public class CodeEditorAssistTests
{
    private const uint Return = 40, Escape = 41, Down = 81;

    private static AdwCodeEditor Editor(string text, params string[] words)
    {
        var editor = new AdwCodeEditor(text) {
            CompletionProvider = (_, _, _) =>
                Task.FromResult<IReadOnlyList<CodeCompletion>>([.. words.Select(w => new CodeCompletion(w, CodeCompletionKind.Method))]),
        };
        editor.Measure(Constraints.Tight(width: 600f, height: 400f));
        editor.Layout(Offset.Zero);
        return editor;
    }

    private static void Type(AdwCodeEditor e, string text)
    {
        foreach (char c in text) e.OnTextInput(c.ToString());
    }

    private static void Key(AdwCodeEditor e, uint scancode) => e.OnKey('\0', scancode, true, Modifiers.None);

    [Fact]
    public void Typing_a_word_opens_filters_and_Enter_inserts_the_selection()
    {
        var e = Editor("", "Alpha", "Alpine", "Beta");
        Type(e, "Al");
        Assert.True(e.IsCompletionOpen);
        Key(e, Down);                 // Alpha → Alpine
        Key(e, Return);
        Assert.Equal(expected: "Alpine", actual: e.Text);
        Assert.False(e.IsCompletionOpen);
    }

    [Fact]
    public void A_dot_opens_member_completion_and_Escape_closes_it()
    {
        var e = Editor("x", "Length", "Count");
        e.GoTo(0, 1);
        Type(e, ".");
        Assert.True(e.IsCompletionOpen);
        Key(e, Escape);
        Assert.False(e.IsCompletionOpen);
        Assert.Equal(expected: "x.", actual: e.Text);
    }

    [Fact]
    public void A_non_word_character_closes_the_popup_and_is_typed_normally()
    {
        var e = Editor("", "Alpha");
        Type(e, "A");
        Assert.True(e.IsCompletionOpen);
        Type(e, " ");
        Assert.False(e.IsCompletionOpen);
        Assert.Equal(expected: "A ", actual: e.Text);
    }

    [Fact]
    public void GoTo_clamps_and_moves_the_caret()
    {
        var e = Editor("one\ntwo");
        e.GoTo(5, 99);
        Assert.Equal(expected: (1, 3), actual: e.Caret);
    }
}

/// <summary>Parameter hints and host-supplied edits (what a formatter applies).</summary>
public class CodeEditorSignatureAndEditTests
{
    private static AdwCodeEditor Editor(string text)
    {
        var editor = new AdwCodeEditor(text) {
            // Inside "f(…)": one overload; the active parameter is the number of commas before the caret.
            SignatureProvider = (doc, offset, _) =>
            {
                int open = doc.LastIndexOf('(', Math.Max(0, offset - 1));
                int close = doc.IndexOf(')', open + 1);
                CodeSignatureHelp? help = open < 0 || (close >= 0 && offset > close) ? null : new CodeSignatureHelp(
                    [new CodeSignature("f(int a, int b)", [(2, 5), (9, 5)]) { ActiveParameter = doc[open..offset].Count(c => c == ',') }],
                    0);
                return Task.FromResult(help);
            },
        };
        editor.Measure(Constraints.Tight(width: 600f, height: 400f));
        editor.Layout(Offset.Zero);
        return editor;
    }

    [Fact]
    public void Typing_paren_opens_the_hint_comma_advances_it_and_leaving_the_call_closes_it()
    {
        var e = Editor("");
        e.OnTextInput("f");
        e.OnTextInput("(");
        Assert.True(e.IsSignatureHelpOpen);
        Assert.Equal(expected: 0, actual: e.ActiveSignature!.ActiveParameter);
        e.OnTextInput("1");
        e.OnTextInput(",");
        Assert.Equal(expected: 1, actual: e.ActiveSignature!.ActiveParameter);
        e.OnTextInput(")"); // types over the auto-closed paren: caret leaves the call
        Assert.False(e.IsSignatureHelpOpen);
    }

    [Fact]
    public void Escape_closes_the_hint()
    {
        var e = Editor("");
        e.OnTextInput("f");
        e.OnTextInput("(");
        e.OnKey('\0', 41, true, Modifiers.None);
        Assert.False(e.IsSignatureHelpOpen);
    }

    [Fact]
    public void ApplyEdits_is_one_undo_step_and_carries_the_caret()
    {
        var e = new AdwCodeEditor("a  =1;\nb=2;");
        e.Measure(Constraints.Tight(width: 600f, height: 400f));
        e.Layout(Offset.Zero);
        e.GoTo(1, 3);                                   // before ';' on line 2
        e.ApplyEdits([new CodeTextEdit(1, 2, " "), new CodeTextEdit(4, 0, " "), new CodeTextEdit(8, 0, " "), new CodeTextEdit(9, 0, " ")]);
        Assert.Equal(expected: "a = 1;\nb = 2;", actual: e.Text);
        Assert.Equal(expected: (1, 5), actual: e.Caret); // still before ';'
        e.Undo();
        Assert.Equal(expected: "a  =1;\nb=2;", actual: e.Text);
    }
}
