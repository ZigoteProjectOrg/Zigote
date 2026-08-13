using System.Globalization;
using System.Text;

namespace Zigote.Generators;

/// <summary>
///     One parsed ARB catalog: the locale, optional <c>@@class</c>/<c>@@namespace</c> globals (read
///     from the template file), and the key → ICU-template map. <c>@key</c> metadata entries and
///     non-string values are skipped, matching the runtime ARB loader's behaviour.
/// </summary>
internal sealed class ArbCatalog
{
    private ArbCatalog(string locale, string? className, string? ns,
        Dictionary<string, string> messages)
    {
        Locale = locale;
        ClassName = className;
        Namespace = ns;
        Messages = messages;
    }

    public string Locale { get; }
    public string? ClassName { get; }
    public string? Namespace { get; }

    /// <summary>Key → ICU message template, in file order.</summary>
    public IReadOnlyDictionary<string, string> Messages { get; }

    public static ArbCatalog Parse(string path, string json)
    {
        var reader = new JsonReader(json);
        var root = reader.ReadObject();

        string? locale = null, className = null, ns = null;
        var messages = new Dictionary<string, string>();

        foreach (var pair in root)
        {
            string key = pair.Key;
            object? value = pair.Value;
            if (key == "@@locale")
                locale = value as string;
            else if (key == "@@class")
                className = value as string;
            else if (key == "@@namespace")
                ns = value as string;
            else if (key.StartsWith(value: "@", comparisonType: StringComparison.Ordinal))
            {
                // @@x globals and @key metadata — not messages.
            }
            else if (value is string s) messages[key] = s;
        }

        locale ??= LocaleFromFileName(path)
                   ?? throw new FormatException("no @@locale and no _xx filename suffix");

        return new ArbCatalog(
            locale: locale,
            className: className,
            ns: ns,
            messages: messages
        );
    }

    private static string? LocaleFromFileName(string path)
    {
        // "…/gallery_en.arb" → "en"; "…/app_zh-Hant.arb" → "zh-Hant".
        string name = path;
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name.Substring(slash + 1);
        if (name.EndsWith(value: ".arb", comparisonType: StringComparison.OrdinalIgnoreCase))
            name = name.Substring(startIndex: 0, length: name.Length - 4);
        int underscore = name.LastIndexOf('_');
        return underscore >= 0 && underscore < name.Length - 1
            ? name.Substring(underscore + 1)
            : null;
    }
}

/// <summary>
///     A minimal, allocation-tolerant JSON reader for ARB files (objects, strings, numbers, bools,
///     nulls, arrays). Kept self-contained so the generator has no JSON package dependency. Values
///     that are not strings are surfaced as opaque non-string objects (the caller skips them).
/// </summary>
internal sealed class JsonReader(string text)
{
    private int _i;

    public List<KeyValuePair<string, object?>> ReadObject()
    {
        SkipWhitespace();
        Expect('{');
        var result = new List<KeyValuePair<string, object?>>();

        SkipWhitespace();
        if (Peek() == '}')
        {
            _i++;
            return result;
        }

        while (true)
        {
            SkipWhitespace();
            string key = ReadString();
            SkipWhitespace();
            Expect(':');
            object? value = ReadValue();
            result.Add(new KeyValuePair<string, object?>(key: key, value: value));

            SkipWhitespace();
            char c = Next();
            if (c == ',') continue;
            if (c == '}') return result;
            throw Error($"expected ',' or '}}', found '{c}'");
        }
    }

    private object? ReadValue()
    {
        SkipWhitespace();
        char c = Peek();
        switch (c)
        {
            case '"': return ReadString();
            case '{': return ReadObject();
            case '[': return ReadArray();
            case 't':
                ExpectWord("true");
                return true;
            case 'f':
                ExpectWord("false");
                return false;
            case 'n':
                ExpectWord("null");
                return null;
            default: return ReadNumber();
        }
    }

    private List<object?> ReadArray()
    {
        Expect('[');
        var result = new List<object?>();
        SkipWhitespace();
        if (Peek() == ']')
        {
            _i++;
            return result;
        }

        while (true)
        {
            result.Add(ReadValue());
            SkipWhitespace();
            char c = Next();
            if (c == ',') continue;
            if (c == ']') return result;
            throw Error($"expected ',' or ']', found '{c}'");
        }
    }

    private string ReadString()
    {
        Expect('"');
        var sb = new StringBuilder();
        while (true)
        {
            char c = Next();
            if (c == '"') return sb.ToString();
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            char esc = Next();
            switch (esc)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    int code = 0;
                    for (int k = 0; k < 4; k++) code = (code * 16) + HexDigit(Next());
                    sb.Append((char)code);
                    break;
                default: throw Error($"bad escape '\\{esc}'");
            }
        }
    }

    private double ReadNumber()
    {
        int start = _i;
        while (_i < text.Length && (char.IsDigit(text[_i]) ||
                                    text[_i] is '-' or '+' or '.' or 'e' or 'E'))
            _i++;
        string span = text.Substring(startIndex: start, length: _i - start);
        return double.TryParse(
            s: span,
            style: NumberStyles.Float,
            provider: CultureInfo.InvariantCulture,
            result: out double d
        )
            ? d
            : throw Error($"bad number '{span}'");
    }

    private static int HexDigit(char c)
    {
        return c switch {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => throw new FormatException($"bad hex digit '{c}'"),
        };
    }

    private void SkipWhitespace()
    {
        while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++;
    }

    private char Peek() => _i < text.Length ? text[_i] : throw Error("unexpected end");

    private char Next() => _i < text.Length ? text[_i++] : throw Error("unexpected end");

    private void Expect(char c)
    {
        if (Next() != c) throw Error($"expected '{c}'");
    }

    private void ExpectWord(string word)
    {
        foreach (char c in word) Expect(c);
    }

    private FormatException Error(string message) => new($"{message} (at offset {_i})");
}

/// <summary>
///     Extracts the arguments an ICU message template consumes, with inferred C# parameter types:
///     <c>plural</c>/<c>selectordinal</c>/<c>number → double</c>, <c>date</c>/
///     <c>
///         time →
///         DateTime
///     </c>
///     , <c>select → string</c>, bare placeholder → <c>object</c>. Mirrors the runtime
///     <c>MessageFormat</c> grammar (apostrophe escaping, nested submessages) closely enough to see
///     every referenced argument; a typed kind wins over a bare reference to the same name.
/// </summary>
internal static class IcuArgs
{
    public static void Scan(string template, List<LocalizationsGenerator.IcuArg> into)
    {
        int i = 0;
        ScanBody(
            s: template,
            i: ref i,
            end: template.Length,
            into: into
        );
    }

    private static void ScanBody(string s, ref int i, int end,
        List<LocalizationsGenerator.IcuArg> into)
    {
        while (i < end)
        {
            char c = s[i];
            if (c == '\'')
            {
                i++;
                if (i < end && s[i] == '\'')
                {
                    i++; // '' — literal apostrophe
                    continue;
                }

                // Quoted literal run — skip to the closing apostrophe.
                while (i < end && s[i] != '\'') i++;
                if (i < end) i++;
                continue;
            }

            if (c == '}')
                // Let the caller (a submessage scan) consume it.
                return;

            if (c != '{')
            {
                i++;
                continue;
            }

            // Placeholder: {name} or {name, type[, style-or-submessages]}
            i++; // consume '{'
            int nameStart = i;
            while (i < end && s[i] != ',' && s[i] != '}') i++;
            if (i >= end) return;
            string name = s.Substring(startIndex: nameStart, length: i - nameStart).Trim();

            if (s[i] == '}')
            {
                i++;
                AddArg(into: into, name: name, kind: null);
                continue;
            }

            i++; // consume ','
            int typeStart = i;
            while (i < end && s[i] != ',' && s[i] != '}') i++;
            if (i >= end) return;
            string kind = s.Substring(startIndex: typeStart, length: i - typeStart).Trim();
            AddArg(into: into, name: name, kind: kind);

            if (s[i] == '}')
            {
                i++; // {n, number} — no body
                continue;
            }

            i++; // consume ',' before the body

            if (kind is "plural" or "selectordinal" or "select")
            {
                // Body = sequence of "selector {submessage}" — recurse into each submessage so
                // nested placeholders are collected too.
                while (i < end && s[i] != '}')
                {
                    if (s[i] == '{')
                    {
                        i++; // enter submessage
                        ScanBody(
                            s: s,
                            i: ref i,
                            end: end,
                            into: into
                        );
                        if (i < end && s[i] == '}') i++; // close submessage
                    }
                    else
                        i++;
                }

                if (i < end) i++; // close the placeholder
            }
            else
            {
                // number/date/time style token — skip to the closing brace.
                int depth = 1;
                while (i < end && depth > 0)
                {
                    if (s[i] == '{') depth++;
                    else if (s[i] == '}') depth--;
                    i++;
                }
            }
        }
    }

    private static void AddArg(List<LocalizationsGenerator.IcuArg> into, string name, string? kind)
    {
        if (name.Length == 0 || name == "#") return;

        string csType = kind switch {
            "plural" or "selectordinal" or "number" => "double",
            "date" or "time" => "global::System.DateTime",
            "select" => "string",
            _ => "object",
        };

        for (int idx = 0; idx < into.Count; idx++)
        {
            if (into[idx].Name != name) continue;
            // A typed occurrence refines an earlier bare "object" reference.
            if (into[idx].CsType == "object" && csType != "object")
                into[idx] = new LocalizationsGenerator.IcuArg(name: name, csType: csType);
            return;
        }

        into.Add(new LocalizationsGenerator.IcuArg(name: name, csType: csType));
    }
}
