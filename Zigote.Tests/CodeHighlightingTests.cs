using Xunit;
using Zigote.Modules.UI.CodeEditor;
using Zigote.UI.TextShaping;

namespace Zigote.Tests;

/// <summary>
///     Exercises the F# (<c>Zigote.Modules.UI.CodeEditor</c>) XParsec parsers from C#: the per-line
///     syntax-highlighting tokenizers (the extracted-from-<c>SyntaxHighlighter</c> logic) and the
///     standalone JSON-to-AST parser. All CPU-only — no native library, no window.
/// </summary>
public class CodeHighlightingTests
{
    private static (string Text, TokenKind Kind)[] Lex(ILineTokenizer t, string line, int state = 0)
    {
        var buf = new List<Token>();
        t.Tokenize(line: line, state: state, output: buf);
        return buf
            .Select(x => (
                line.Substring(
                    startIndex: x.Start,
                    length: Math.Min(val1: x.Length, val2: line.Length - x.Start)
                ),
                x.Kind)
            )
            .ToArray();
    }

    [Fact]
    public void CSharp_ClassifiesKeywordsTypesNumbersAndComment()
    {
        var lex = Lex(t: Highlighting.CSharp, line: "public int Count = 42; // note");

        Assert.Contains(expected: ("public", TokenKind.Keyword), collection: lex);
        Assert.Contains(expected: ("int", TokenKind.Type), collection: lex); // primitive type set
        Assert.Contains(
            expected: ("Count", TokenKind.Type),
            collection: lex
        ); // PascalCase heuristic
        Assert.Contains(expected: ("=", TokenKind.Operator), collection: lex);
        Assert.Contains(expected: ("42", TokenKind.Number), collection: lex);
        Assert.Contains(expected: (";", TokenKind.Punctuation), collection: lex);
        Assert.Contains(expected: ("// note", TokenKind.Comment), collection: lex);
    }

    [Fact]
    public void CSharp_StringsCharsAndVerbatimAreStrings()
    {
        var lex = Lex(t: Highlighting.CSharp, line: "var s = @\"a\"\"b\"; var c = '\\n';");

        Assert.Contains(
            expected: ("@\"a\"\"b\"", TokenKind.String),
            collection: lex
        ); // verbatim with "" escape stays one token
        Assert.Contains(expected: ("'\\n'", TokenKind.String), collection: lex);
    }

    [Fact]
    public void CSharp_BlockCommentStateThreadsAcrossLines()
    {
        var buf = new List<Token>();
        int s = ILineTokenizer.StateDefault;

        s = Highlighting.CSharp.Tokenize(line: "code /* open", state: s, output: buf);
        Assert.Equal(expected: 1, actual: s); // entered block comment

        buf.Clear();
        s = Highlighting.CSharp.Tokenize(line: "still inside", state: s, output: buf);
        Assert.Equal(expected: 1, actual: s);
        Assert.All(
            collection: buf,
            action: t => Assert.Equal(expected: TokenKind.Comment, actual: t.Kind)
        ); // whole line is comment

        // Empty line while inside a block comment must terminate cleanly (no infinite loop) and stay in-block.
        buf.Clear();
        s = Highlighting.CSharp.Tokenize(line: "", state: s, output: buf);
        Assert.Equal(expected: 1, actual: s);

        buf.Clear();
        s = Highlighting.CSharp.Tokenize(line: "close */ more", state: s, output: buf);
        Assert.Equal(expected: 0, actual: s); // block closed
        Assert.Contains(collection: buf, filter: t => t.Kind == TokenKind.Comment);
    }

    [Fact]
    public void Json_KeysAreTypesValuesAreTyped()
    {
        var lex = Lex(t: Highlighting.Json, line: "{ \"key\": 42, \"flag\": true, \"s\": \"hi\" }");

        Assert.Contains(
            expected: ("\"key\"", TokenKind.Type),
            collection: lex
        ); // string before ':' = key
        Assert.Contains(expected: ("\"hi\"", TokenKind.String), collection: lex); // string value
        Assert.Contains(expected: ("42", TokenKind.Number), collection: lex);
        Assert.Contains(expected: ("true", TokenKind.Keyword), collection: lex);
    }

    [Fact]
    public void ForExtension_MapsAndReturnsNullForUnknown()
    {
        Assert.Same(expected: Highlighting.CSharp, actual: Highlighting.ForExtension(".cs"));
        Assert.Same(
            expected: Highlighting.Json,
            actual: Highlighting.ForExtension("json")
        ); // no leading dot
        Assert.Same(
            expected: Highlighting.Wgsl,
            actual: Highlighting.ForExtension(".frag")
        ); // GLSL-family → WGSL lexer
        Assert.Same(expected: Highlighting.Zig, actual: Highlighting.ForExtension(".zig"));
        Assert.Null(Highlighting.ForExtension(".txt"));
        Assert.Null(Highlighting.ForExtension(""));
    }
}
