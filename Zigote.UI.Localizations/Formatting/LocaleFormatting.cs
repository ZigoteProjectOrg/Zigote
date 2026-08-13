using System.Collections.Concurrent;
using System.Text;
using Zigote.Core.Diagnostics;

namespace Zigote.UI.Localizations;

/// <summary>Length preset for a formatted date or time.</summary>
public enum DateStyle
{
    /// <summary>Numeric, compact — e.g. <c>10/6/2026</c>, <c>4:07 PM</c>.</summary>
    Short,

    /// <summary>Abbreviated — e.g. <c>Oct 6, 2026</c>.</summary>
    Medium,

    /// <summary>Full month name — e.g. <c>October 6, 2026</c>, <c>4:07:12 PM</c>.</summary>
    Long,

    /// <summary>Everything, including the weekday.</summary>
    Full,
}

/// <summary>
///     Culture-aware number, currency, percent and date/time formatting for a <see cref="Locale" />.
///     A thin, cached wrapper over <see cref="CultureInfo" /> that degrades gracefully — an unknown
///     tag falls back to the bare language, then to the invariant culture — so it never throws on an
///     exotic locale. Instances are immutable and shared per locale via <see cref="For" />.
/// </summary>
public sealed class LocaleFormatting
{
    private static readonly ConcurrentDictionary<Locale, LocaleFormatting> Cache = new();

    // A minimal ISO-4217 → symbol table for explicit currency overrides; unknown codes render as the code.
    private static readonly Dictionary<string, string> CurrencySymbols =
        new(StringComparer.OrdinalIgnoreCase) {
            ["USD"] = "$",
            ["EUR"] = "€",
            ["GBP"] = "£",
            ["JPY"] = "¥",
            ["CNY"] = "¥",
            ["RUB"] = "₽",
            ["INR"] = "₹",
            ["KRW"] = "₩",
            ["BRL"] = "R$",
            ["CHF"] = "CHF",
            ["CAD"] = "$",
            ["AUD"] = "$",
            ["TRY"] = "₺",
            ["PLN"] = "zł",
            ["SEK"] = "kr",
        };

    private LocaleFormatting(Locale locale, CultureInfo culture)
    {
        Locale = locale;
        Culture = culture;
    }

    public Locale Locale { get; }
    public CultureInfo Culture { get; }

    /// <summary>The (cached) formatter for a locale.</summary>
    public static LocaleFormatting For(Locale locale)
    {
        return Cache.GetOrAdd(locale, static l => new LocaleFormatting(l, ResolveCulture(l)));
    }

    private static CultureInfo ResolveCulture(Locale locale)
    {
        if (locale.IsEmpty) return CultureInfo.InvariantCulture;
        foreach (var tag in new[] {
                     locale.ToBcp47(),
                     locale.WithoutScript().ToBcp47(),
                     locale.Language,
                 })
            try
            {
                return CultureInfo.GetCultureInfo(tag);
            }
            catch (CultureNotFoundException)
            {
                // try the next, looser tag
            }

        // Falling back is correct — a formatter that threw on an exotic tag would take the screen
        // down over a date — but it is never what the caller wanted, and it is invisible: the app
        // renders fully translated text next to 8/7/2026 and 1,234.56. Said once per locale, since
        // the cache only builds each one once.
        //
        // The usual cause is not an exotic locale at all but InvariantGlobalization=true, under
        // which GetCultureInfo throws for *every* named culture. An app that ships a non-English
        // catalog and sets that flag gets correct words (catalog lookup and PluralRules are both
        // ICU-free by design) and wrong numbers, with nothing else to say so.
        DebugLog.Warn(
            $"no CultureInfo for '{locale.ToBcp47()}' — dates, numbers and currency will format as " +
            "invariant. If the app sets InvariantGlobalization=true, that is the cause: clear it to " +
            "format for this locale, or ignore this if the app never shows a formatted date or number.",
            "localizations"
        );
        return CultureInfo.InvariantCulture;
    }

    // ── Numbers ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Format a number with grouping. <paramref name="pattern" /> is a raw .NET numeric format
    ///     string.
    /// </summary>
    public string Number(double value, string? pattern = null)
    {
        return value.ToString(pattern ?? "#,0.###############", Culture);
    }

    /// <summary>Format an integer with grouping separators.</summary>
    public string Integer(long value)
    {
        return value.ToString("#,0", Culture);
    }

    /// <summary>Format a fraction as a percentage (0.5 → "50%"). Multiplies by 100, matching ICU/.NET.</summary>
    public string Percent(double value, int decimals = 0)
    {
        return value.ToString("P" + Math.Clamp(decimals, 0, 15), Culture);
    }

    /// <summary>
    ///     Format a monetary amount. An explicit ISO 4217 <paramref name="currencyCode" /> overrides the
    ///     culture
    ///     currency.
    /// </summary>
    public string Currency(decimal value, string? currencyCode = null)
    {
        if (string.IsNullOrEmpty(currencyCode))
            return value.ToString("C", Culture);

        var nf = (NumberFormatInfo)Culture.NumberFormat.Clone();
        nf.CurrencySymbol = CurrencySymbols.GetValueOrDefault(currencyCode, currencyCode + " ");
        return value.ToString("C", nf);
    }

    // ── Dates ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Format the date part. A non-null <paramref name="pattern" /> overrides
    ///     <paramref name="style" />.
    /// </summary>
    public string Date(DateTime value, DateStyle style = DateStyle.Medium, string? pattern = null)
    {
        if (pattern is not null) return value.ToString(pattern, Culture);
        return style switch {
            DateStyle.Short => value.ToString("d", Culture),
            DateStyle.Long => value.ToString("D", Culture),
            DateStyle.Full => value.ToString("D", Culture),
            _ => value.ToString(MediumDatePattern(Culture), Culture),
        };
    }

    /// <summary>
    ///     Format the time part. A non-null <paramref name="pattern" /> overrides
    ///     <paramref name="style" />.
    /// </summary>
    public string Time(DateTime value, DateStyle style = DateStyle.Short, string? pattern = null)
    {
        if (pattern is not null) return value.ToString(pattern, Culture);
        return style switch {
            DateStyle.Short or DateStyle.Medium => value.ToString("t", Culture),
            _ => value.ToString("T", Culture),
        };
    }

    /// <summary>Format both date and time.</summary>
    public string DateTime(DateTime value, DateStyle date = DateStyle.Medium,
        DateStyle time = DateStyle.Short)
    {
        return Date(value, date) + " " + Time(value, time);
    }

    // Derive an abbreviated-month "medium" pattern by widening the numeric month in the culture's SHORT
    // date pattern to an abbreviated month name — preserving the culture's field order and separators.
    // The short pattern never carries a weekday, which avoids the orphaned weekday-literal / double-space
    // artifacts that stripping tokens out of the long date pattern produces for cultures like th/mn/ba.
    private static string MediumDatePattern(CultureInfo c)
    {
        var sp = c.DateTimeFormat.ShortDatePattern;
        var sb = new StringBuilder(sp.Length + 2);
        var inQuote = false;
        var i = 0;
        while (i < sp.Length)
        {
            var ch = sp[i];
            if (ch == '\'')
            {
                inQuote = !inQuote;
                sb.Append(ch);
                i++;
            }
            else if (ch == 'M' && !inQuote)
            {
                var j = i;
                while (j < sp.Length && sp[j] == 'M') j++;
                var run = j - i;
                sb.Append('M', run < 3 ? 3 : run); // widen M/MM (numeric) → MMM (abbreviated name)
                i = j;
            }
            else
            {
                sb.Append(ch);
                i++;
            }
        }

        var p = sb.ToString();
        return string.IsNullOrWhiteSpace(p) ? "MMM d, yyyy" : p;
    }
}
