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
            throw new ArgumentException(
                "A locale requires a non-empty language code.",
                nameof(language)
            );

        Language = language.Trim().ToLowerInvariant();
        Script = Normalize(script) is { } s ? Title(s) : null;
        Country = Normalize(country)?.ToUpperInvariant();
    }

    /// <summary>True when this is the default (uninitialised) struct value with no language.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Language);

    /// <summary>The writing direction implied by the language + script.</summary>
    public TextDirection TextDirection => TextDirectionInfo.ForLanguage(Language, Script);

    // ── Parsing ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parse a BCP-47 / POSIX locale tag. Accepts <c>-</c> or <c>_</c> separators and an optional
    ///     4-letter script subtag between language and region. A trailing <c>.UTF-8</c> / <c>@variant</c>
    ///     (POSIX) suffix is ignored. Throws <see cref="FormatException" /> on an empty/garbage tag.
    /// </summary>
    public static Locale Parse(string tag)
    {
        if (!TryParse(tag, out var locale))
            throw new FormatException($"'{tag}' is not a valid locale tag.");
        return locale;
    }

    /// <summary>Non-throwing <see cref="Parse" />.</summary>
    public static bool TryParse(string? tag, out Locale locale)
    {
        locale = default;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        // Drop POSIX codeset/variant suffixes: en_US.UTF-8@euro → en_US
        var cleaned = tag.Trim();
        var cut = cleaned.IndexOfAny(['.', '@']);
        if (cut >= 0) cleaned = cleaned[..cut];

        var parts = cleaned.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var language = parts[0];
        if (!IsAlpha(language) || language.Length is < 2 or > 3) return false;

        string? script = null;
        string? country = null;

        for (var i = 1; i < parts.Length; i++)
        {
            var p = parts[i];
            // A 4-letter alpha subtag is unambiguously a script (regions are 2-alpha or 3-digit), so
            // accept it even after a region — tolerating region-before-script order without data loss.
            if (script is null && p.Length == 4 && IsAlpha(p))
                script = p; // ISO 15924 script subtag (4 letters)
            else if (country is null &&
                     ((p.Length == 2 && IsAlpha(p)) || (p.Length == 3 && IsDigits(p))))
                country = p; // ISO 3166-1 alpha-2 or UN M.49 numeric-3 region
            // Extra subtags (variants, extensions) are tolerated and ignored.
        }

        locale = new Locale(language, script, country);
        return true;
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    /// <summary>Canonical BCP-47 tag with hyphen separators (e.g. <c>"zh-Hant-TW"</c>).</summary>
    public string ToBcp47()
    {
        return Join('-');
    }

    /// <summary>POSIX-style tag with underscore separators (e.g. <c>"zh_Hant_TW"</c>) — for file names.</summary>
    public string ToUnderscore()
    {
        return Join('_');
    }

    private string Join(char sep)
    {
        if (string.IsNullOrEmpty(Language)) return string.Empty; // default(Locale) formats to ""
        if (Script is null && Country is null) return Language;
        var sb = new StringBuilder(Language);
        if (Script is not null) sb.Append(sep).Append(Script);
        if (Country is not null) sb.Append(sep).Append(Country);
        return sb.ToString();
    }

    public override string ToString()
    {
        return IsEmpty ? "(none)" : ToBcp47();
    }

    // ── Resolution helpers ───────────────────────────────────────────────────

    /// <summary>This locale reduced to just its language (drops script + region).</summary>
    public Locale LanguageOnly()
    {
        return new Locale(Language);
    }

    /// <summary>This locale reduced to language + region (drops the script subtag).</summary>
    public Locale WithoutScript()
    {
        return Country is null ? new Locale(Language) : new Locale(Language, null, Country);
    }

    // ── Equality ─────────────────────────────────────────────────────────────

    public bool Equals(Locale other)
    {
        return string.Equals(Language, other.Language, StringComparison.Ordinal)
               && string.Equals(Script, other.Script, StringComparison.Ordinal)
               && string.Equals(Country, other.Country, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is Locale l && Equals(l);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Language, Script, Country);
    }

    public static bool operator ==(Locale a, Locale b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(Locale a, Locale b)
    {
        return !a.Equals(b);
    }

    /// <summary>
    ///     Convert a tag string to a <see cref="Locale" /> — the declarative sugar for literals
    ///     (<c>Locale l = "en-US";</c>, <c>SupportedLocales = { "en", "es" }</c>). Delegates to
    ///     <see cref="Parse" />, so it <b>throws <see cref="FormatException" /></b> on an invalid tag;
    ///     for untrusted/config input use <see cref="TryParse" /> instead.
    /// </summary>
    public static implicit operator Locale(string tag)
    {
        return Parse(tag);
    }

    // ── Well-known locales / system ──────────────────────────────────────────

    /// <summary>The operating system's current UI locale, or <see cref="En" /> when it is invariant.</summary>
    public static Locale System
    {
        get
        {
            var name = CultureInfo.CurrentUICulture.Name;
            return TryParse(name, out var l) ? l : En;
        }
    }

    public static readonly Locale En = new("en");
    public static readonly Locale EnUs = new("en", null, "US");
    public static readonly Locale EnGb = new("en", null, "GB");
    public static readonly Locale Es = new("es");
    public static readonly Locale Fr = new("fr");
    public static readonly Locale De = new("de");
    public static readonly Locale It = new("it");
    public static readonly Locale Pt = new("pt");
    public static readonly Locale PtBr = new("pt", null, "BR");
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
    public static readonly Locale ZhHans = new("zh", "Hans");
    public static readonly Locale ZhHant = new("zh", "Hant");

    // ── Private helpers ──────────────────────────────────────────────────────

    private static string? Normalize(string? s)
    {
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static string Title(string s)
    {
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }

    private static bool IsAlpha(string s)
    {
        foreach (var c in s)
            if (!char.IsAsciiLetter(c))
                return false;
        return true;
    }

    private static bool IsDigits(string s)
    {
        foreach (var c in s)
            if (!char.IsAsciiDigit(c))
                return false;
        return true;
    }
}
