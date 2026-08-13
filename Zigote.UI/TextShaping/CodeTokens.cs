namespace Zigote.UI.TextShaping;

/// <summary>The lexical class of a source token, mapped to a theme colour by the consumer.</summary>
public enum TokenKind
{
    Default,
    Keyword,
    Type,
    String,
    Comment,
    Number,
    Operator,
    Punctuation,
}

/// <summary>A half-open token span <c>[Start, Start+Length)</c> within a single source line.</summary>
public readonly struct Token
{
    public int Start { get; init; }
    public int Length { get; init; }
    public TokenKind Kind { get; init; }

    public Token(int start, int length, TokenKind kind)
    {
        Start = start;
        Length = length;
        Kind = kind;
    }
}

/// <summary>
///     A line-oriented tokenizer for the <c>CodeEditor</c> widget. The widget owns no language
///     knowledge of its own — it delegates highlighting to an injected <see cref="ILineTokenizer" />.
///     Concrete tokenizers (C#, JSON, WGSL, Zig) live outside <c>Zigote.UI</c>; the
///     <c>Zigote.Modules.UI.CodeEditor</c> F# module implements them with FParsec combinators.
///     <para>
///         Multi-line constructs (e.g. <c>/* … */</c> block comments) are threaded through an opaque
///         integer state: pass <see cref="StateDefault" /> for the first line, then feed the returned
///         value into the next line's call.
///     </para>
/// </summary>
public interface ILineTokenizer
{
    /// <summary>No carried lexer state (the start of a document / normal code).</summary>
    const int StateDefault = 0;

    /// <summary>
    ///     Tokenize a single <paramref name="line" /> (no trailing newline), appending its tokens to
    ///     <paramref name="output" /> (the caller clears it first). <paramref name="state" /> carries
    ///     multi-line lexer status in; the return value carries it out.
    /// </summary>
    int Tokenize(string line, int state, List<Token> output);
}
