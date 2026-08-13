using Xunit;
using Zigote.Modules.UI.CodeEditor;
using Zigote.UI.TextShaping;

namespace Zigote.Tests;

/// <summary>
///     Exercises the F# (<c>Zigote.Modules.UI.CodeEditor</c>) FParsec parsers from C#: the per-line
///     syntax-highlighting tokenizers (the extracted-from-<c>SyntaxHighlighter</c> logic) and the
///     standalone JSON-to-AST parser. All CPU-only — no native library, no window.
/// </summary>
public class CodeHighlightingTests
{
    private static (string Text, TokenKind Kind)[] Lex(ILineTokenizer t, string line, int state = 0)
    {
        var buf = new List<Token>();
        t.Tokenize(line, state, buf);
        return buf
            .Select(x => (line.Substring(x.Start, Math.Min(x.Length, line.Length - x.Start)),
                x.Kind)
            )
            .ToArray();
    }

    [Fact]
    public void CSharp_ClassifiesKeywordsTypesNumbersAndComment()
    {
        var lex = Lex(Highlighting.CSharp, "public int Count = 42; // note");

        Assert.Contains(("public", TokenKind.Keyword), lex);
        Assert.Contains(("int", TokenKind.Type), lex); // primitive type set
        Assert.Contains(("Count", TokenKind.Type), lex); // PascalCase heuristic
        Assert.Contains(("=", TokenKind.Operator), lex);
        Assert.Contains(("42", TokenKind.Number), lex);
        Assert.Contains((";", TokenKind.Punctuation), lex);
        Assert.Contains(("// note", TokenKind.Comment), lex);
    }

    [Fact]
    public void CSharp_StringsCharsAndVerbatimAreStrings()
    {
        var lex = Lex(Highlighting.CSharp, "var s = @\"a\"\"b\"; var c = '\\n';");

        Assert.Contains(
            ("@\"a\"\"b\"", TokenKind.String),
            lex
        ); // verbatim with "" escape stays one token
        Assert.Contains(("'\\n'", TokenKind.String), lex);
    }

    [Fact]
    public void CSharp_BlockCommentStateThreadsAcrossLines()
    {
        var buf = new List<Token>();
        var s = ILineTokenizer.StateDefault;

        s = Highlighting.CSharp.Tokenize("code /* open", s, buf);
        Assert.Equal(1, s); // entered block comment

        buf.Clear();
        s = Highlighting.CSharp.Tokenize("still inside", s, buf);
        Assert.Equal(1, s);
        Assert.All(buf, t => Assert.Equal(TokenKind.Comment, t.Kind)); // whole line is comment

        // Empty line while inside a block comment must terminate cleanly (no infinite loop) and stay in-block.
        buf.Clear();
        s = Highlighting.CSharp.Tokenize("", s, buf);
        Assert.Equal(1, s);

        buf.Clear();
        s = Highlighting.CSharp.Tokenize("close */ more", s, buf);
        Assert.Equal(0, s); // block closed
        Assert.Contains(buf, t => t.Kind == TokenKind.Comment);
    }

    [Fact]
    public void Json_KeysAreTypesValuesAreTyped()
    {
        var lex = Lex(Highlighting.Json, "{ \"key\": 42, \"flag\": true, \"s\": \"hi\" }");

        Assert.Contains(("\"key\"", TokenKind.Type), lex); // string before ':' = key
        Assert.Contains(("\"hi\"", TokenKind.String), lex); // string value
        Assert.Contains(("42", TokenKind.Number), lex);
        Assert.Contains(("true", TokenKind.Keyword), lex);
    }

    [Fact]
    public void ForExtension_MapsAndReturnsNullForUnknown()
    {
        Assert.Same(Highlighting.CSharp, Highlighting.ForExtension(".cs"));
        Assert.Same(Highlighting.Json, Highlighting.ForExtension("json")); // no leading dot
        Assert.Same(
            Highlighting.Wgsl,
            Highlighting.ForExtension(".frag")
        ); // GLSL-family → WGSL lexer
        Assert.Same(Highlighting.Zig, Highlighting.ForExtension(".zig"));
        Assert.Null(Highlighting.ForExtension(".txt"));
        Assert.Null(Highlighting.ForExtension(""));
    }
}
