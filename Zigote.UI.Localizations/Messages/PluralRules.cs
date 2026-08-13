namespace Zigote.UI.Localizations;

/// <summary>
///     The CLDR plural operands derived from a formatted number: <c>n</c> (absolute value), <c>i</c>
///     (integer part), <c>v</c>/<c>w</c> (visible fraction-digit count with / without trailing zeros)
///     and <c>f</c>/<c>t</c> (those fraction digits as an integer).
/// </summary>
public readonly struct PluralOperands
{
    public double N { get; }
    public long I { get; }
    public int V { get; }
    public int W { get; }
    public long F { get; }
    public long T { get; }

    private PluralOperands(double n, long i, int v, int w, long f, long t)
    {
        N = n;
        I = i;
        V = v;
        W = w;
        F = f;
        T = t;
    }

    /// <summary>Operands for an integer count (no visible fraction digits).</summary>
    public static PluralOperands FromLong(long value)
    {
        return new PluralOperands(
            Math.Abs((double)value),
            Math.Abs(value),
            0,
            0,
            0,
            0
        );
    }

    /// <summary>
    ///     Operands for a number, optionally pinned to a fixed number of visible fraction digits (so
    ///     "1.0" selects a different category than "1" where the language distinguishes them).
    /// </summary>
    public static PluralOperands FromDouble(double value, int? fractionDigits = null)
    {
        value = Math.Abs(value);

        var s = fractionDigits is int fd
            ? value.ToString("F" + Math.Clamp(fd, 0, 15), CultureInfo.InvariantCulture)
            : value.ToString("0.###############", CultureInfo.InvariantCulture);

        var dot = s.IndexOf('.');
        var intText = dot < 0 ? s : s[..dot];
        var fracText = dot < 0 ? string.Empty : s[(dot + 1)..];

        var i = ParseIntegerTail(intText);

        var v = fracText.Length;
        var trimmed = fracText.TrimEnd('0');
        var w = trimmed.Length;
        var f = fracText.Length == 0 ? 0 : long.Parse(fracText, CultureInfo.InvariantCulture);
        var t = trimmed.Length == 0 ? 0 : long.Parse(trimmed, CultureInfo.InvariantCulture);

        return new PluralOperands(
            value,
            i,
            v,
            w,
            f,
            t
        );
    }

    // Integer part as a long; for values beyond long range only the low digits matter to the rules,
    // so fall back to the last 18 digits rather than overflowing.
    private static long ParseIntegerTail(string intText)
    {
        if (long.TryParse(
                intText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var i
            )) return i;
        var tail = intText.Length > 18 ? intText[^18..] : intText;
        var t = long.TryParse(
            tail,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed
            : 0;
        // A huge value with an all-zero low tail must stay non-zero for exact i==0 / i!=0 tests while
        // preserving its (all-zero) low residues — 1e6 is divisible by 10/100/1e6.
        return t == 0 ? 1_000_000L : t;
    }
}

/// <summary>The plural-category selector a language plugs into <see cref="PluralRules.Register" />.</summary>
public delegate PluralCategory PluralRule(PluralOperands operands);

/// <summary>
///     CLDR cardinal + ordinal plural-category selection. Languages with distinctive rules are
///     implemented exactly (English, the Slavic and Serbo-Croatian families, Arabic, Hebrew, the
///     Romance family, Baltic, Romanian, Filipino, Indic, and the many "no distinction" languages).
///     Any language not listed defaults to the English-style <c>one</c> (i = 1 and v = 0) /
///     <c>other</c> split for cardinals and to <c>other</c> for ordinals — a safe, common shape.
///     <para>
///         A language the built-in table doesn't cover (or covers wrongly for your needs) can be
///         added without forking: <see cref="Register" /> installs custom cardinal/ordinal rules
///         that take precedence over the built-ins for that language. Register at startup — the
///         registry is copy-on-write, so reads are lock-free on the formatting path.
///     </para>
/// </summary>
public static class PluralRules
{
    private static readonly object RegisterLock = new();

    // Custom per-language overrides (Register/Unregister). Copy-on-write: formatting reads the
    // volatile snapshot lock-free; registration clones + swaps under the lock.
    private static volatile Dictionary<string, (PluralRule? Cardinal, PluralRule? Ordinal)>
        _custom = new(StringComparer.Ordinal);

    /// <summary>
    ///     Install custom plural rules for a language (ISO 639 code), taking precedence over the
    ///     built-in table. Pass null to keep the built-in behaviour for that form. Call at startup;
    ///     replaces any previous registration for the language.
    /// </summary>
    public static void Register(string language, PluralRule? cardinal,
        PluralRule? ordinal = null)
    {
        lock (RegisterLock)
        {
            var next = new Dictionary<string, (PluralRule?, PluralRule?)>(
                _custom,
                StringComparer.Ordinal
            ) { [Canon(language)] = (cardinal, ordinal) };
            _custom = next;
        }
    }

    /// <summary>Remove a custom registration, restoring the built-in rules for the language.</summary>
    public static bool Unregister(string language)
    {
        lock (RegisterLock)
        {
            if (!_custom.ContainsKey(Canon(language))) return false;
            var next = new Dictionary<string, (PluralRule?, PluralRule?)>(
                _custom,
                StringComparer.Ordinal
            );
            next.Remove(Canon(language));
            _custom = next;
            return true;
        }
    }

    // Legacy / alternate ISO codes → canonical.
    private static string Canon(string language)
    {
        return language.ToLowerInvariant() switch {
            "iw" => "he",
            "in" => "id",
            "ji" => "yi",
            "no" or "nb" or "nn" => "nb",
            "tl" => "fil",
            "sh" => "sr",
            var l => l,
        };
    }

    /// <summary>The cardinal category ("1 item / 2 items") for a language and number.</summary>
    public static PluralCategory Cardinal(string language, PluralOperands op)
    {
        var canon = Canon(language);
        if (_custom.TryGetValue(canon, out var custom) && custom.Cardinal is { } rule)
            return rule(op);

        return canon switch {
            // No plural distinction — always "other".
            "ja" or "ko" or "zh" or "yue" or "vi" or "th" or "id" or "ms" or "my" or "km" or "lo"
                or "bo" or "dz" or "ig" or "yo" or "jv" or "su" or "sg" or "to" or "wo"
                => PluralCategory.Other,

            // one: i = 1 and v = 0
            "en" or "de" or "nl" or "sv" or "et" or "fi" or "ur" or "sw"
                => EnglishLike(op),
            // Romance: one: i = 1 and v = 0; many: i != 0 and i % 1e6 = 0 and v = 0 (CLDR v43+)
            "it" or "ca"
                => op.I == 1 && op.V == 0 ? PluralCategory.One : RomanceMany(op),
            // Romance: one: n = 1; many: i % 1e6 = 0
            "es"
                => op.N == 1 ? PluralCategory.One : RomanceMany(op),
            // one: n = 1 (Turkic, Georgian, Greek, Hungarian, Dravidian, Marathi, Nepali — no "many")
            "tr" or "ka" or "az" or "kk" or "ky" or "uz" or "el" or "hu" or "ta" or "te" or "mr"
                or "ne"
                => op.N == 1 ? PluralCategory.One : PluralCategory.Other,
            // Danish: one: n = 1, or t != 0 and i is 0 or 1 ("0,5 time" is singular)
            "da"
                => op.N == 1 || (op.T != 0 && op.I is 0 or 1)
                    ? PluralCategory.One
                    : PluralCategory.Other,
            // Romance: one: i = 0 or i = 1; many: i % 1e6 = 0
            "fr" or "pt"
                => op.I is 0 or 1 ? PluralCategory.One : RomanceMany(op),
            // one: i = 0 or n = 1
            "hi" or "bn" or "gu" or "kn" or "pa" or "am" or "fa"
                => op.I == 0 || op.N == 1 ? PluralCategory.One : PluralCategory.Other,

            "ru" or "uk" => RussianLike(op),
            "sr" or "hr" or "bs" => SerboCroatian(op),
            "pl" => Polish(op),
            "cs" or "sk" => CzechLike(op),
            "lt" => Lithuanian(op),
            "ro" or "mo" => Romanian(op),
            "fil" => Filipino(op),
            "ar" => Arabic(op),
            "he" => Hebrew(op),

            _ => EnglishLike(op),
        };
    }

    /// <summary>The ordinal category ("1st / 2nd / 3rd") for a language and number.</summary>
    public static PluralCategory Ordinal(string language, PluralOperands op)
    {
        var canon = Canon(language);
        if (_custom.TryGetValue(canon, out var custom) && custom.Ordinal is { } rule)
            return rule(op);

        var n = (long)op.N;
        return canon switch {
            "en" => EnglishOrdinal(n),
            "mo" or "ro" => n == 1 ? PluralCategory.One : PluralCategory.Other,
            // Swedish only — Finnish has no ordinal rule in CLDR (always "other").
            "sv" =>
                n % 10 is 1 or 2 && n % 100 is not (11 or 12)
                    ? PluralCategory.One
                    : PluralCategory.Other,
            _ => PluralCategory.Other,
        };
    }

    // ── Rule bodies ──────────────────────────────────────────────────────────

    private static PluralCategory EnglishLike(PluralOperands op)
    {
        return op.I == 1 && op.V == 0 ? PluralCategory.One : PluralCategory.Other;
    }

    // Romance "many": exact non-zero integer multiples of a million (CLDR v43+ for es/fr/pt/it/ca).
    private static PluralCategory RomanceMany(PluralOperands op)
    {
        return op.V == 0 && op.I != 0 && op.I % 1_000_000 == 0
            ? PluralCategory.Many
            : PluralCategory.Other;
    }

    // Serbian/Croatian/Bosnian: like Russian but the residual category is "other" (no "many"),
    // and the one/few tests also apply to visible fraction digits.
    private static PluralCategory SerboCroatian(PluralOperands op)
    {
        var i10 = op.I % 10;
        var i100 = op.I % 100;
        var f10 = op.F % 10;
        var f100 = op.F % 100;
        if ((op.V == 0 && i10 == 1 && i100 != 11) || (f10 == 1 && f100 != 11))
            return PluralCategory.One;
        if ((op.V == 0 && i10 is >= 2 and <= 4 && i100 is < 12 or > 14) ||
            (f10 is >= 2 and <= 4 && f100 is < 12 or > 14))
            return PluralCategory.Few;
        return PluralCategory.Other;
    }

    // Filipino/Tagalog: "one" unless a digit of the number ends in 4, 6 or 9.
    private static PluralCategory Filipino(PluralOperands op)
    {
        if (op.V == 0)
            return op.I is >= 1 and <= 3 || (op.I % 10 != 4 && op.I % 10 != 6 && op.I % 10 != 9)
                ? PluralCategory.One
                : PluralCategory.Other;
        return op.F % 10 != 4 && op.F % 10 != 6 && op.F % 10 != 9
            ? PluralCategory.One
            : PluralCategory.Other;
    }

    private static PluralCategory RussianLike(PluralOperands op)
    {
        if (op.V != 0) return PluralCategory.Other;
        var i10 = op.I % 10;
        var i100 = op.I % 100;
        if (i10 == 1 && i100 != 11) return PluralCategory.One;
        if (i10 is >= 2 and <= 4 && i100 is < 12 or > 14) return PluralCategory.Few;
        return PluralCategory.Many;
    }

    private static PluralCategory Polish(PluralOperands op)
    {
        if (op.V != 0) return PluralCategory.Other;
        if (op.I == 1) return PluralCategory.One;
        var i10 = op.I % 10;
        var i100 = op.I % 100;
        if (i10 is >= 2 and <= 4 && i100 is < 12 or > 14) return PluralCategory.Few;
        return PluralCategory.Many;
    }

    private static PluralCategory CzechLike(PluralOperands op)
    {
        if (op.V != 0) return PluralCategory.Many;
        if (op.I == 1) return PluralCategory.One;
        if (op.I is >= 2 and <= 4) return PluralCategory.Few;
        return PluralCategory.Other;
    }

    private static PluralCategory Lithuanian(PluralOperands op)
    {
        var n10 = op.I % 10;
        var n100 = op.I % 100;
        if (op.F != 0) return PluralCategory.Many;
        if (n10 == 1 && n100 is < 11 or > 19) return PluralCategory.One;
        if (n10 is >= 2 and <= 9 && n100 is < 11 or > 19) return PluralCategory.Few;
        return PluralCategory.Other;
    }

    private static PluralCategory Romanian(PluralOperands op)
    {
        if (op.I == 1 && op.V == 0) return PluralCategory.One;
        var i100 = op.I % 100;
        if (op.V != 0 || op.N == 0 || i100 is >= 2 and <= 19) return PluralCategory.Few;
        return PluralCategory.Other;
    }

    private static PluralCategory Arabic(PluralOperands op)
    {
        if (op.N == 0) return PluralCategory.Zero;
        if (op.N == 1) return PluralCategory.One;
        if (op.N == 2) return PluralCategory.Two;
        var n100 = op.I % 100;
        if (op.V == 0 && n100 is >= 3 and <= 10) return PluralCategory.Few;
        if (op.V == 0 && n100 is >= 11 and <= 99) return PluralCategory.Many;
        return PluralCategory.Other;
    }

    private static PluralCategory Hebrew(PluralOperands op)
    {
        if (op.I == 1 && op.V == 0) return PluralCategory.One;
        if (op.I == 2 && op.V == 0) return PluralCategory.Two;
        if (op.V == 0 && (op.I < 0 || op.I > 10) && op.I % 10 == 0) return PluralCategory.Many;
        return PluralCategory.Other;
    }

    private static PluralCategory EnglishOrdinal(long n)
    {
        var n10 = n % 10;
        var n100 = n % 100;
        if (n10 == 1 && n100 != 11) return PluralCategory.One;
        if (n10 == 2 && n100 != 12) return PluralCategory.Two;
        if (n10 == 3 && n100 != 13) return PluralCategory.Few;
        return PluralCategory.Other;
    }
}
