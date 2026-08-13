namespace Zigote.UI.TextShaping;

/// <summary>
///     Allocation-light tokenizer for editor syntax coloring. It scans a line once and appends spans
///     directly, avoiding parser-combinator allocations on the scroll/paint path.
/// </summary>
public enum CodeLanguage
{
    CSharp,
    Wgsl,
    Zig,
    Json,
}

public sealed class BuiltInCodeTokenizer(CodeLanguage language) : ILineTokenizer
{
    private static readonly HashSet<string> CSharpKeywords = [
        "abstract", "as", "async", "await", "base", "break", "case", "catch", "checked", "class",
        "const", "continue", "default", "delegate", "do", "else", "enum", "event", "explicit",
        "extern", "false", "finally", "fixed", "for", "foreach", "get", "global", "goto", "if",
        "implicit", "in", "init", "interface", "internal", "is", "lock", "nameof", "new", "not",
        "null", "operator", "or", "out", "override", "params", "partial", "private", "protected",
        "public", "readonly", "record", "ref", "required", "return", "sealed", "set", "sizeof",
        "stackalloc", "static", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "unchecked", "unsafe", "using", "value", "var", "virtual", "volatile", "when", "where",
        "while", "with", "yield",
    ];

    private static readonly HashSet<string> CSharpTypes = [
        "bool", "byte", "sbyte", "char", "decimal", "double", "dynamic", "float", "int", "uint",
        "long", "ulong", "short", "ushort", "object", "string", "void", "nint", "nuint", "Span",
        "List", "Dictionary", "Action", "Func", "Task", "Color", "Vec2", "Vec3", "Vec4", "Mat4",
        "Quat", "Rect", "Size", "Offset",
    ];

    private static readonly HashSet<string> ShaderKeywords = [
        "alias", "break", "case", "const", "continue", "continuing", "default", "discard", "else",
        "enable", "false", "fn", "for", "if", "let", "loop", "override", "return", "struct",
        "switch", "true", "var", "while", "private", "function", "workgroup", "uniform", "storage",
        "read", "write", "read_write", "fragment", "vertex", "compute",
    ];

    private static readonly HashSet<string> ShaderTypes = [
        "bool", "i32", "u32", "f32", "f16", "vec2", "vec3", "vec4", "vec2f", "vec3f", "vec4f",
        "vec2i", "vec3i", "vec4i", "vec2u", "vec3u", "vec4u", "mat2x2", "mat3x3", "mat4x4",
        "mat2x2f", "mat3x3f", "mat4x4f", "array", "atomic", "sampler", "texture_2d",
    ];

    public int Tokenize(string line, int state, List<Token> output)
    {
        int i = 0;
        if (language == CodeLanguage.CSharp && state == 1)
        {
            int close = line.IndexOf(value: "*/", comparisonType: StringComparison.Ordinal);
            if (close < 0)
            {
                if (line.Length > 0)
                    output.Add(new Token(start: 0, length: line.Length, kind: TokenKind.Comment));
                return 1;
            }

            output.Add(new Token(start: 0, length: close + 2, kind: TokenKind.Comment));
            i = close + 2;
            state = 0;
        }

        while (i < line.Length)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                {
                    output.Add(
                        new Token(start: i, length: line.Length - i, kind: TokenKind.Comment)
                    );
                    break;
                }

                if (language == CodeLanguage.CSharp && line[i + 1] == '*')
                {
                    int close = line.IndexOf(
                        value: "*/",
                        startIndex: i + 2,
                        comparisonType: StringComparison.Ordinal
                    );
                    if (close < 0)
                    {
                        output.Add(
                            new Token(start: i, length: line.Length - i, kind: TokenKind.Comment)
                        );
                        return 1;
                    }

                    output.Add(new Token(start: i, length: close + 2 - i, kind: TokenKind.Comment));
                    i = close + 2;
                    continue;
                }
            }

            if (c is '"' or '\'' || (language == CodeLanguage.CSharp && c == '@' &&
                                     i + 1 < line.Length && line[i + 1] == '"'))
            {
                int start = i;
                bool verbatim = c == '@';
                char quote = verbatim ? '"' : c;
                i += verbatim ? 2 : 1;
                while (i < line.Length)
                {
                    if (verbatim && line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    if (!verbatim && line[i] == '\\' && i + 1 < line.Length)
                    {
                        i += 2;
                        continue;
                    }

                    if (line[i++] == quote) break;
                }

                var kind = TokenKind.String;
                if (language == CodeLanguage.Json)
                {
                    int lookahead = i;
                    while (lookahead < line.Length && char.IsWhiteSpace(line[lookahead]))
                        lookahead++;
                    if (lookahead < line.Length && line[lookahead] == ':') kind = TokenKind.Type;
                }

                output.Add(new Token(start: start, length: i - start, kind: kind));
                continue;
            }

            if (char.IsDigit(c))
            {
                int start = i++;
                while (i < line.Length &&
                       (char.IsLetterOrDigit(line[i]) || line[i] is '_' or '.')) i++;
                output.Add(new Token(start: start, length: i - start, kind: TokenKind.Number));
                continue;
            }

            if (char.IsLetter(c) || c is '_' or '@')
            {
                int start = i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                string word = line[start..i];
                var kind = ClassifyIdentifier(word);
                output.Add(new Token(start: start, length: i - start, kind: kind));
                continue;
            }

            if ("+-*/%=<>!&|^~?".Contains(c))
            {
                int start = i++;
                while (i < line.Length && "+-*/%=<>!&|^~?".Contains(line[i])) i++;
                output.Add(new Token(start: start, length: i - start, kind: TokenKind.Operator));
                continue;
            }

            if ("()[]{},:;.".Contains(c))
                output.Add(new Token(start: i, length: 1, kind: TokenKind.Punctuation));
            i++;
        }

        return state;
    }

    private TokenKind ClassifyIdentifier(string word)
    {
        if (language == CodeLanguage.Json)
            return word is "true" or "false" or "null" ? TokenKind.Keyword : TokenKind.Default;
        var keywords = language == CodeLanguage.CSharp ? CSharpKeywords : ShaderKeywords;
        var types = language == CodeLanguage.CSharp ? CSharpTypes : ShaderTypes;
        if (keywords.Contains(word)) return TokenKind.Keyword;
        if (types.Contains(word) || char.IsUpper(word[0])) return TokenKind.Type;
        return TokenKind.Default;
    }
}
