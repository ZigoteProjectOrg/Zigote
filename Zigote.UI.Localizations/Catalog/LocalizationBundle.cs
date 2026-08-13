namespace Zigote.UI.Localizations;

/// <summary>What to return for a key that is missing from both the active and fallback catalogs.</summary>
public enum MissingTranslationPolicy
{
    /// <summary>Return the key itself — the developer-friendly default (missing text is obvious in-UI).</summary>
    Key,

    /// <summary>Return an empty string.</summary>
    Empty,

    /// <summary>Throw a <see cref="KeyNotFoundException" />.</summary>
    Throw,
}

/// <summary>
///     A set of <see cref="LocalizationCatalog" />s across locales, with a fallback locale and a
///     miss policy. Resolves a request to the best same-language catalog, then to the fallback, then
///     to <see cref="OnMissing" /> / <see cref="MissingPolicy" />. This is the string-based
///     translation
///     store behind <c>context.Tr(...)</c>.
/// </summary>
public sealed class LocalizationBundle
{
    private readonly Dictionary<Locale, LocalizationCatalog> _catalogs = new();

    public LocalizationBundle() { }

    public LocalizationBundle(params LocalizationCatalog[] catalogs)
    {
        foreach (var c in catalogs) Add(c);
    }

    /// <summary>The locale used when the requested one lacks a key. Defaults to the first catalog added.</summary>
    public Locale FallbackLocale { get; set; }

    /// <summary>
    ///     How a total miss (absent from active + fallback) is resolved. Defaults to
    ///     <see cref="MissingTranslationPolicy.Key" />.
    /// </summary>
    public MissingTranslationPolicy MissingPolicy { get; set; } = MissingTranslationPolicy.Key;

    /// <summary>
    ///     Optional hook invoked on a total miss before <see cref="MissingPolicy" /> applies. Return a
    ///     string to use it, or <c>null</c> to fall through to the policy. Handy for logging missing keys.
    /// </summary>
    public Func<string, Locale, string?>? OnMissing { get; set; }

    /// <summary>All locales that have a catalog, in insertion order.</summary>
    public IReadOnlyList<Locale> Locales => _catalogs.Keys.ToList();

    public void Add(LocalizationCatalog catalog)
    {
        if (_catalogs.Count == 0 && FallbackLocale.IsEmpty) FallbackLocale = catalog.Locale;
        _catalogs[catalog.Locale] = catalog;
    }

    /// <summary>Register (or extend) a locale's catalog from raw key/template pairs.</summary>
    public LocalizationBundle Add(Locale locale, IEnumerable<KeyValuePair<string, string>> messages)
    {
        if (_catalogs.TryGetValue(key: locale, value: out var existing))
            existing.AddRange(messages);
        else Add(new LocalizationCatalog(locale: locale, messages: messages));
        return this;
    }

    /// <summary>True when a catalog exists for the locale or its bare language.</summary>
    public bool Supports(Locale locale) => CatalogFor(locale) is not null;

    /// <summary>The best catalog for a locale: exact, else same language (preferring a script match).</summary>
    public LocalizationCatalog? CatalogFor(Locale locale)
    {
        if (_catalogs.TryGetValue(key: locale, value: out var exact)) return exact;

        LocalizationCatalog? sameLanguage = null;
        foreach (var (key, catalog) in _catalogs)
        {
            if (!string.Equals(
                    a: key.Language,
                    b: locale.Language,
                    comparisonType: StringComparison.Ordinal
                )) continue;
            sameLanguage ??= catalog;
            if (locale.Script is not null && string.Equals(
                    a: key.Script,
                    b: locale.Script,
                    comparisonType: StringComparison.Ordinal
                ))
                return catalog;
        }

        return sameLanguage;
    }

    public string Translate(Locale locale, string key,
        IReadOnlyDictionary<string, object?>? args = null)
    {
        string? primary = CatalogFor(locale)?.Translate(key: key, args: args);
        if (primary is not null) return primary;

        if (FallbackLocale != locale)
        {
            string? fallback = CatalogFor(FallbackLocale)?.Translate(key: key, args: args);
            if (fallback is not null) return fallback;
        }

        if (OnMissing?.Invoke(arg1: key, arg2: locale) is { } handled) return handled;

        return MissingPolicy switch {
            MissingTranslationPolicy.Empty => string.Empty,
            MissingTranslationPolicy.Throw => throw new KeyNotFoundException(
                $"No translation for key '{key}' in locale '{locale}' (fallback '{FallbackLocale}')."
            ),
            _ => key,
        };
    }

    public string
        Translate(Locale locale, string key, params (string Name, object? Value)[] args) =>
        Translate(locale: locale, key: key, args: MessageFormat.ToDictionary(args));

    /// <summary>
    ///     A payload bound to <paramref name="locale" /> for provider storage /
    ///     <c>Localizations.Of</c>.
    /// </summary>
    public StringLocalizations For(Locale locale) => new(bundle: this, locale: locale);

    /// <summary>Expose this bundle as a delegate so it plugs into a <see cref="LocalizationsScope" />.</summary>
    public LocalizationsDelegate<StringLocalizations> AsDelegate() =>
        LocalizationsDelegates.Create(isSupported: Supports, load: For);
}
