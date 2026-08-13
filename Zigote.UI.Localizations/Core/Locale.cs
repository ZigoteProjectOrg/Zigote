using System.Text;

namespace Zigote.UI.Localizations;

/// <summary>
///     An immutable BCP-47 locale: a required language plus an optional script and region.
///     <para>
///         Parsed leniently (<c>"en"</c>, <c>"en-US"</c>, <c>"en_US"</c>, <c>"zh-Hant"</c>,
///         <c>"zh-Hant-TW"</c>) and normalised to canonical casing — language lowercase, script
///         Title-case, region UPPERCASE — so equality is case-insensitive and hashable.
///     </para>
///     <para>
///         A bare string converts implicitly (<c>Locale l = "en-US";</c>) so catalogs and scope
///         configuration read declaratively.
///     </para>
/// </summary>
public readonly struct Locale : IEquatable<Locale>
{
    /// <summary>Lowercase ISO 639 language code (e.g. <c>"en"</c>). The one required component.</summary>
    public string Language { get; }

    /// <summary>Title-case ISO 15924 script code (e.g. <c>"Hant"</c>), or <c>null</c>.</summary>
    public string? Script { get; }

    /// <summary>Uppercase ISO 3166 region/country code (e.g. <c>"US"</c>), or <c>null</c>.</summary>
    public string? Country { get; }

    public Locale(string language, string? script = null, string? country = null)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            throw new ArgumentException(
                message: "A locale requires a non-empty language code.",
                paramName: nameof(language)
            );
        }

        Language = language.Trim().ToLowerInvariant();
        Script = Normalize(script) is { } s ? Title(s) : null;
        Country = Normalize(country)?.ToUpperInvariant();
    }

    /// <summary>True when this is the default (uninitialised) struct value with no language.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Language);

    /// <summary>The writing direction implied by the language + script.</summary>
    public TextDirection TextDirection =>
        TextDirectionInfo.ForLanguage(language: Language, script: Script);

    // ── Parsing ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parse a BCP-47 / POSIX locale tag. Accepts <c>-</c> or <c>_</c> separators and an optional
    ///     4-letter script subtag between language and region. A trailing <c>.UTF-8</c> / <c>@variant</c>
    ///     (POSIX) suffix is ignored. Throws <see cref="FormatException" /> on an empty/garbage tag.
    /// </summary>
    public static Locale Parse(string tag)
    {
        if (!TryParse(tag: tag, locale: out var locale))
            throw new FormatException($"'{tag}' is not a valid locale tag.");
        return locale;
    }

    /// <summary>Non-throwing <see cref="Parse" />.</summary>
    public static bool TryParse(string? tag, out Locale locale)
    {
        locale = default;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        // Drop POSIX codeset/variant suffixes: en_US.UTF-8@euro → en_US
        string cleaned = tag.Trim();
        int cut = cleaned.IndexOfAny(['.', '@']);
        if (cut >= 0) cleaned = cleaned[..cut];

        string[] parts = cleaned.Split(
            separator: ['-', '_'],
            options: StringSplitOptions.RemoveEmptyEntries
        );
        if (parts.Length == 0) return false;

        string language = parts[0];
        if (!IsAlpha(language) || language.Length is < 2 or > 3) return false;

        string? script = null;
        string? country = null;

        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i];
            // A 4-letter alpha subtag is unambiguously a script (regions are 2-alpha or 3-digit), so
            // accept it even after a region — tolerating region-before-script order without data loss.
            if (script is null && p.Length == 4 && IsAlpha(p))
                script = p; // ISO 15924 script subtag (4 letters)
            else if (country is null &&
                     ((p.Length == 2 && IsAlpha(p)) || (p.Length == 3 && IsDigits(p))))
                country = p; // ISO 3166-1 alpha-2 or UN M.49 numeric-3 region
            // Extra subtags (variants, extensions) are tolerated and ignored.
        }

        locale = new Locale(language: language, script: script, country: country);
        return true;
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    /// <summary>Canonical BCP-47 tag with hyphen separators (e.g. <c>"zh-Hant-TW"</c>).</summary>
    public string ToBcp47() => Join('-');

    /// <summary>POSIX-style tag with underscore separators (e.g. <c>"zh_Hant_TW"</c>) — for file names.</summary>
    public string ToUnderscore() => Join('_');

    private string Join(char sep)
    {
        if (string.IsNullOrEmpty(Language)) return string.Empty; // default(Locale) formats to ""
        if (Script is null && Country is null) return Language;
        var sb = new StringBuilder(Language);
        if (Script is not null) sb.Append(sep).Append(Script);
        if (Country is not null) sb.Append(sep).Append(Country);
        return sb.ToString();
    }

    public override string ToString() => IsEmpty ? "(none)" : ToBcp47();

    // ── Resolution helpers ───────────────────────────────────────────────────

    /// <summary>This locale reduced to just its language (drops script + region).</summary>
    public Locale LanguageOnly() => new(Language);

    /// <summary>This locale reduced to language + region (drops the script subtag).</summary>
    public Locale WithoutScript() => Country is null
        ? new Locale(Language)
        : new Locale(language: Language, script: null, country: Country);

    // ── Equality ─────────────────────────────────────────────────────────────

    public bool Equals(Locale other)
    {
        return string.Equals(
                   a: Language,
                   b: other.Language,
                   comparisonType: StringComparison.Ordinal
               )
               && string.Equals(
                   a: Script,
                   b: other.Script,
                   comparisonType: StringComparison.Ordinal
               )
               && string.Equals(
                   a: Country,
                   b: other.Country,
                   comparisonType: StringComparison.Ordinal
               );
    }

    public override bool Equals(object? obj) => obj is Locale l && Equals(l);

    public override int GetHashCode() => HashCode.Combine(
        value1: Language,
        value2: Script,
        value3: Country
    );

    public static bool operator ==(Locale a, Locale b) => a.Equals(b);

    public static bool operator !=(Locale a, Locale b) => !a.Equals(b);

    /// <summary>
    ///     Convert a tag string to a <see cref="Locale" /> — the declarative sugar for literals
    ///     (<c>Locale l = "en-US";</c>, <c>SupportedLocales = { "en", "es" }</c>). Delegates to
    ///     <see cref="Parse" />, so it <b>throws <see cref="FormatException" /></b> on an invalid tag;
    ///     for untrusted/config input use <see cref="TryParse" /> instead.
    /// </summary>
    public static implicit operator Locale(string tag) => Parse(tag);

    // ── Well-known locales / system ──────────────────────────────────────────

    /// <summary>The operating system's current UI locale, or <see cref="En" /> when it is invariant.</summary>
    public static Locale System
    {
        get
        {
            string name = CultureInfo.CurrentUICulture.Name;
            return TryParse(tag: name, locale: out var l) ? l : En;
        }
    }

    public static readonly Locale En = new("en");
    public static readonly Locale EnUs = new(language: "en", script: null, country: "US");
    public static readonly Locale EnGb = new(language: "en", script: null, country: "GB");
    public static readonly Locale Es = new("es");
    public static readonly Locale Fr = new("fr");
    public static readonly Locale De = new("de");
    public static readonly Locale It = new("it");
    public static readonly Locale Pt = new("pt");
    public static readonly Locale PtBr = new(language: "pt", script: null, country: "BR");
    public static readonly Locale Nl = new("nl");
    public static readonly Locale Ru = new("ru");
    public static readonly Locale Pl = new("pl");
    public static readonly Locale Tr = new("tr");
    public static readonly Locale Ar = new("ar");
    public static readonly Locale He = new("he");
    public static readonly Locale Fa = new("fa");
    public static readonly Locale Hi = new("hi");
    public static readonly Locale Ja = new("ja");
    public static readonly Locale Ko = new("ko");
    public static readonly Locale Zh = new("zh");
    public static readonly Locale ZhHans = new(language: "zh", script: "Hans");
    public static readonly Locale ZhHant = new(language: "zh", script: "Hant");

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Title(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private static bool IsAlpha(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsAsciiLetter(c))
                return false;
        }

        return true;
    }

    private static bool IsDigits(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }
}
