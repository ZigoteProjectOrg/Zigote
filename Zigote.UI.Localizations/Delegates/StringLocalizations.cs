namespace Zigote.UI.Localizations;

/// <summary>
///     The built-in key-based localization payload: a <see cref="LocalizationBundle" /> bound to one
///     active <see cref="Locale" />. This is what <c>Localizations.Of&lt;StringLocalizations&gt;</c>
///     and
///     <c>context.Tr(key)</c> resolve to. Advanced apps can instead register typed
///     <see cref="LocalizationsDelegate{T}" />s with generated accessors — both coexist.
/// </summary>
public sealed class StringLocalizations
{
    private readonly LocalizationBundle _bundle;

    internal StringLocalizations(LocalizationBundle bundle, Locale locale)
    {
        _bundle = bundle;
        Locale = locale;
    }

    public Locale Locale { get; }

    /// <summary>Translate a key with no arguments.</summary>
    public string this[string key] => _bundle.Translate(locale: Locale, key: key);

    /// <summary>Translate a key with named arguments.</summary>
    public string Translate(string key, IReadOnlyDictionary<string, object?>? args = null) =>
        _bundle.Translate(locale: Locale, key: key, args: args);

    /// <summary>Translate a key with inline <c>(name, value)</c> arguments.</summary>
    public string Translate(string key, params (string Name, object? Value)[] args) =>
        _bundle.Translate(locale: Locale, key: key, args: args);

    /// <summary>Whether a key resolves (in the active or fallback locale).</summary>
    public bool Contains(string key)
    {
        return (_bundle.CatalogFor(Locale)?.Contains(key) ?? false)
               || (_bundle.CatalogFor(_bundle.FallbackLocale)?.Contains(key) ?? false);
    }
}
