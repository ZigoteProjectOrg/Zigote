using System.Text;

namespace Zigote.UI.Localizations;

/// <summary>
///     A compiled ICU-style message pattern. Supports named placeholders <c>{name}</c>, typed
///     arguments <c>{n, number, percent}</c> / <c>{d, date, medium}</c>, and the selection forms
///     <c>{count, plural, …}</c>, <c>{pos, selectordinal, …}</c> and <c>{gender, select, …}</c> — with
///     <c>offset:</c>, explicit <c>=N</c> cases, the <c>#</c> token, nested submessages, and ICU
///     apostrophe escaping (<c>'{'</c> quotes, <c>''</c> = a literal apostrophe).
///     <para>Parse once (construct), format many times against a <see cref="Locale" /> + arguments.</para>
/// </summary>
public sealed class MessageFormat
{
    private readonly MessagePart[] _parts;

    public MessageFormat(string pattern)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        var i = 0;
        _parts = Parser.ParseMessage(pattern, ref i, false);
    }

    public string Pattern { get; }

    /// <summary>Format the message for a locale and named arguments.</summary>
    public string Format(Locale locale, IReadOnlyDictionary<string, object?> arguments)
    {
        var sb = new StringBuilder(Pattern.Length + 16);
        var ctx = new MessageContext(locale, arguments, null);
        foreach (var p in _parts) p.Append(sb, in ctx);
        return sb.ToString();
    }

    /// <summary>Format with inline <c>(name, value)</c> argument tuples.</summary>
    public string Format(Locale locale, params (string Name, object? Value)[] arguments)
    {
        return Format(locale, ToDictionary(arguments));
    }

    /// <summary>Parse-and-format in one call (no reuse — prefer caching a <see cref="MessageFormat" />).</summary>
    public static string Format(string pattern, Locale locale,
        IReadOnlyDictionary<string, object?> arguments)
    {
        return new MessageFormat(pattern).Format(locale, arguments);
    }

    internal static Dictionary<string, object?> ToDictionary(
        (string Name, object? Value)[] arguments)
    {
        var dict = new Dictionary<string, object?>(arguments.Length, StringComparer.Ordinal);
        foreach (var (name, value) in arguments) dict[name] = value;
        return dict;
    }

    // ── Runtime context ──────────────────────────────────────────────────────

    private readonly struct MessageContext(
        Locale locale,
        IReadOnlyDictionary<string, object?> args,
        double? pound)
    {
        public readonly Locale Locale = locale;
        public readonly IReadOnlyDictionary<string, object?> Args = args;

        /// <summary>The number the enclosing plural's <c>#</c> token renders, or null outside a plural.</summary>
        public readonly double? Pound = pound;

        public object? Arg(string name)
        {
            return Args.TryGetValue(name, out var v) ? v : null;
        }

        public MessageContext WithPound(double value)
        {
            return new MessageContext(Locale, Args, value);
        }
    }

    // ── AST ──────────────────────────────────────────────────────────────────

    private abstract class MessagePart
    {
        public abstract void Append(StringBuilder sb, in MessageContext ctx);
    }

    private sealed class LiteralPart(string text) : MessagePart
    {
        public override void Append(StringBuilder sb, in MessageContext ctx)
        {
            sb.Append(text);
        }
    }

    private sealed class PoundPart : MessagePart
    {
        public static readonly PoundPart Instance = new();

        public override void Append(StringBuilder sb, in MessageContext ctx)
        {
            if (ctx.Pound is { } n) sb.Append(LocaleFormatting.For(ctx.Locale).Number(n));
            else sb.Append('#'); // '#' outside a plural is a literal, per ICU
        }
    }

    private sealed class SimpleArgPart(string name, string? type, string? style) : MessagePart
    {
        public override void Append(StringBuilder sb, in MessageContext ctx)
        {
            var value = ctx.Arg(name);
            if (value is null)
            {
                sb.Append('{').Append(name).Append('}'); // visible, debuggable missing-arg marker
                return;
            }

            if (type is null)
            {
                AppendPlain(sb, value, ctx.Locale);
                return;
            }

            var fmt = LocaleFormatting.For(ctx.Locale);
            switch (type)
            {
                case "number":
                    AppendNumber(
                        sb,
                        fmt,
                        value,
                        style
                    );
                    break;
                case "date":
                    sb.Append(
                        fmt.Date(
                            ToDateTime(value),
                            ParseDateStyle(style, out var datePattern),
                            datePattern
                        )
                    );
                    break;
                case "time":
                    sb.Append(
                        fmt.Time(
                            ToDateTime(value),
                            ParseDateStyle(style, out var timePattern),
                            timePattern
                        )
                    );
                    break;
                default:
                    AppendPlain(sb, value, ctx.Locale);
                    break;
            }
        }

        private static void AppendPlain(StringBuilder sb, object value, Locale locale)
        {
            if (Numbers.IsNumeric(value) && Numbers.TryToDouble(value, out var d))
                sb.Append(LocaleFormatting.For(locale).Number(d));
            else if (value is IFormattable f)
                sb.Append(f.ToString(null, LocaleFormatting.For(locale).Culture));
            else
                sb.Append(value);
        }

        private static void AppendNumber(StringBuilder sb, LocaleFormatting fmt, object value,
            string? style)
        {
            Numbers.TryToDouble(value, out var d);
            switch (style)
            {
                case null or "" or "decimal":
                    sb.Append(fmt.Number(d));
                    break;
                case "integer":
                    sb.Append(fmt.Integer((long)Math.Round(d, MidpointRounding.AwayFromZero)));
                    break;
                case "percent":
                    sb.Append(fmt.Percent(d));
                    break;
                case "currency":
                    sb.Append(fmt.Currency(Numbers.ToDecimal(value)));
                    break;
                default:
                    sb.Append(fmt.Number(d, style)); // a raw .NET numeric format string
                    break;
            }
        }

        private static DateStyle ParseDateStyle(string? style, out string? pattern)
        {
            pattern = null;
            switch (style)
            {
                case null or "" or "medium": return DateStyle.Medium;
                case "short": return DateStyle.Short;
                case "long": return DateStyle.Long;
                case "full": return DateStyle.Full;
                default:
                    pattern = style; // a raw .NET date/time format string
                    return DateStyle.Medium;
            }
        }

        private static DateTime ToDateTime(object value)
        {
            return value switch {
                DateTime dt => dt,
                DateTimeOffset dto => dto.LocalDateTime,
                _ => DateTime.TryParse(
                    value.ToString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var p
                )
                    ? p
                    : default,
            };
        }
    }

    private sealed class SelectPart(
        string name,
        Dictionary<string, MessagePart[]> cases) : MessagePart
    {
        public override void Append(StringBuilder sb, in MessageContext ctx)
        {
            var key = ctx.Arg(name)?.ToString() ?? "other";
            var sub = cases.GetValueOrDefault(key) ?? cases.GetValueOrDefault("other");
            if (sub is null) return;
            foreach (var p in sub) p.Append(sb, in ctx);
        }
    }

    private sealed class PluralPart(
        string name,
        double offset,
        bool ordinal,
        Dictionary<double, MessagePart[]> explicitCases,
        Dictionary<PluralCategory, MessagePart[]> keywordCases) : MessagePart
    {
        public override void Append(StringBuilder sb, in MessageContext ctx)
        {
            Numbers.TryToDouble(ctx.Arg(name), out var value);

            MessagePart[]? sub = null;
            if (explicitCases.Count > 0 && explicitCases.TryGetValue(value, out var exact))
                sub = exact;

            var pound = value - offset;
            if (sub is null)
            {
                var op = PluralOperands.FromDouble(pound);
                var cat = ordinal
                    ? PluralRules.Ordinal(ctx.Locale.Language, op)
                    : PluralRules.Cardinal(ctx.Locale.Language, op);
                sub = keywordCases.GetValueOrDefault(cat)
                      ?? keywordCases.GetValueOrDefault(PluralCategory.Other);
            }

            if (sub is null) return;
            var inner = ctx.WithPound(pound);
            foreach (var p in sub) p.Append(sb, in inner);
        }
    }

    // ── Parser ───────────────────────────────────────────────────────────────

    private static class Parser
    {
        public static MessagePart[] ParseMessage(string s, ref int i, bool nested)
        {
            var parts = new List<MessagePart>();
            var lit = new StringBuilder();

            void Flush()
            {
                if (lit.Length > 0)
                {
                    parts.Add(new LiteralPart(lit.ToString()));
                    lit.Clear();
                }
            }

            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\'')
                {
                    ConsumeQuoted(s, ref i, lit);
                    continue;
                }

                if (c == '{')
                {
                    Flush();
                    parts.Add(ParseArgument(s, ref i));
                    continue;
                }

                if (c == '}')
                {
                    if (nested) break; // the caller consumes the '}'
                    lit.Append('}'); // stray at top level — tolerate as literal
                    i++;
                    continue;
                }

                if (c == '#')
                {
                    Flush();
                    parts.Add(PoundPart.Instance);
                    i++;
                    continue;
                }

                lit.Append(c);
                i++;
            }

            Flush();
            return parts.ToArray();
        }

        // ICU apostrophe escaping. s[i] == '\''.
        private static void ConsumeQuoted(string s, ref int i, StringBuilder lit)
        {
            i++; // consume opening quote
            if (i >= s.Length)
            {
                lit.Append('\'');
                return;
            }

            if (s[i] == '\'')
            {
                lit.Append('\''); // '' → '
                i++;
                return;
            }

            if (!IsSyntax(s[i]))
            {
                lit.Append(
                    '\''
                ); // a lone apostrophe is literal; leave the next char for the main loop
                return;
            }

            // Quoted run: copy literally until the next lone apostrophe.
            while (i < s.Length)
            {
                if (s[i] == '\'')
                {
                    if (i + 1 < s.Length && s[i + 1] == '\'')
                    {
                        lit.Append('\'');
                        i += 2;
                        continue;
                    }

                    i++; // closing quote
                    return;
                }

                lit.Append(s[i]);
                i++;
            }
        }

        private static bool IsSyntax(char c)
        {
            return c is '{' or '}' or '#' or '|';
        }

        private static MessagePart ParseArgument(string s, ref int i)
        {
            i++; // consume '{'
            SkipWs(s, ref i);
            var name = ReadName(s, ref i);
            SkipWs(s, ref i);

            if (Peek(s, i) == '}')
            {
                i++;
                return new SimpleArgPart(name, null, null);
            }

            Expect(s, ref i, ',');
            SkipWs(s, ref i);
            var type = ReadName(s, ref i);
            SkipWs(s, ref i);

            // The second comma introduces the style (number/date) or the case list (plural/select).
            var hasComma = Peek(s, i) == ',';
            if (hasComma)
            {
                i++;
                SkipWs(s, ref i);
            }

            switch (type)
            {
                case "plural":
                    return ParsePlural(
                        s,
                        ref i,
                        name,
                        false
                    );
                case "selectordinal":
                    return ParsePlural(
                        s,
                        ref i,
                        name,
                        true
                    );
                case "select":
                    return ParseSelect(s, ref i, name);
                default:
                    // number / date / time (or unknown) — capture the optional style up to the close.
                    var style = hasComma ? ReadStyle(s, ref i) : null;
                    Expect(s, ref i, '}');
                    return new SimpleArgPart(
                        name,
                        type,
                        string.IsNullOrWhiteSpace(style) ? null : style.Trim()
                    );
            }
        }

        private static MessagePart ParsePlural(string s, ref int i, string name, bool ordinal)
        {
            SkipWs(s, ref i);

            double offset = 0;
            if (MatchKeyword(s, ref i, "offset:"))
            {
                SkipWs(s, ref i);
                offset = ReadNumber(s, ref i);
                SkipWs(s, ref i);
            }

            var explicitCases = new Dictionary<double, MessagePart[]>();
            var keywordCases = new Dictionary<PluralCategory, MessagePart[]>();

            while (i < s.Length && s[i] != '}')
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] == '}') break;

                if (s[i] == '=')
                {
                    i++;
                    var value = ReadNumber(s, ref i);
                    SkipWs(s, ref i);
                    explicitCases[value] = ReadSubmessage(s, ref i);
                }
                else
                {
                    var keyword = ReadName(s, ref i);
                    SkipWs(s, ref i);
                    var body = ReadSubmessage(s, ref i);
                    if (PluralCategoryNames.TryParse(keyword, out var cat))
                        keywordCases[cat] = body;
                }

                SkipWs(s, ref i);
            }

            Expect(s, ref i, '}');
            return new PluralPart(
                name,
                offset,
                ordinal,
                explicitCases,
                keywordCases
            );
        }

        private static MessagePart ParseSelect(string s, ref int i, string name)
        {
            var cases = new Dictionary<string, MessagePart[]>(StringComparer.Ordinal);
            while (i < s.Length && s[i] != '}')
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] == '}') break;
                var keyword = ReadName(s, ref i);
                SkipWs(s, ref i);
                cases[keyword] = ReadSubmessage(s, ref i);
                SkipWs(s, ref i);
            }

            Expect(s, ref i, '}');
            return new SelectPart(name, cases);
        }

        private static MessagePart[] ReadSubmessage(string s, ref int i)
        {
            Expect(s, ref i, '{');
            var body = ParseMessage(s, ref i, true);
            Expect(s, ref i, '}');
            return body;
        }

        // ── Lexing helpers ───────────────────────────────────────────────────

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static char Peek(string s, int i)
        {
            return i < s.Length ? s[i] : '\0';
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c)
                throw new FormatException($"Expected '{c}' at position {i} in message \"{s}\".");
            i++;
        }

        private static string ReadName(string s, ref int i)
        {
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ',' && s[i] != '{' &&
                   s[i] != '}')
                i++;
            if (i == start)
                throw new FormatException(
                    $"Expected an identifier at position {i} in message \"{s}\"."
                );
            return s[start..i];
        }

        // Style text runs to the matching '}', tolerating nested braces (e.g. a date skeleton).
        private static string ReadStyle(string s, ref int i)
        {
            var start = i;
            var depth = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth == 0) break;
                    depth--;
                }

                i++;
            }

            return s[start..i];
        }

        private static double ReadNumber(string s, ref int i)
        {
            var start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
            var text = s[start..i];
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var v
            )
                ? v
                : throw new FormatException(
                    $"Invalid number '{text}' at position {start} in message \"{s}\"."
                );
        }

        private static bool MatchKeyword(string s, ref int i, string keyword)
        {
            if (i + keyword.Length > s.Length) return false;
            if (string.CompareOrdinal(
                    s,
                    i,
                    keyword,
                    0,
                    keyword.Length
                ) != 0) return false;
            i += keyword.Length;
            return true;
        }
    }

    // ── Numeric coercion ─────────────────────────────────────────────────────

    private static class Numbers
    {
        public static bool IsNumeric(object? value)
        {
            return value is byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal;
        }

        public static bool TryToDouble(object? value, out double result)
        {
            switch (value)
            {
                case null:
                    result = 0;
                    return false;
                case double d:
                    result = d;
                    return true;
                case float f:
                    result = f;
                    return true;
                case int n:
                    result = n;
                    return true;
                case long l:
                    result = l;
                    return true;
                case short sh:
                    result = sh;
                    return true;
                case byte b:
                    result = b;
                    return true;
                case sbyte sb:
                    result = sb;
                    return true;
                case uint ui:
                    result = ui;
                    return true;
                case ulong ul:
                    result = ul;
                    return true;
                case ushort us:
                    result = us;
                    return true;
                case decimal m:
                    result = (double)m;
                    return true;
                case string str when double.TryParse(
                    str,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var p
                ):
                    result = p;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        public static decimal ToDecimal(object? value)
        {
            try
            {
                return value switch {
                    decimal m => m,
                    string str when decimal.TryParse(
                            str,
                            NumberStyles.Any,
                            CultureInfo.InvariantCulture,
                            out var p
                        )
                        => p,
                    _ => TryToDouble(value, out var d) ? (decimal)d : 0m,
                };
            }
            catch (OverflowException)
            {
                return 0m;
            }
        }
    }
}
