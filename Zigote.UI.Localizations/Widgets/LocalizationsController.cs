using Zigote.Core.State;

namespace Zigote.UI.Localizations;

/// <summary>
///     Owns the mutable locale state behind a <see cref="LocalizationsScope" />: the registered
///     delegates, the supported/fallback locales, and the live provider widgets. Switching the locale
///     re-runs every delegate and pushes a fresh <see cref="LocalizationsData" /> into the provider,
///     which rebuilds the dependent subtree — the same reactive path the theme uses.
///     <para>Reach it from anywhere below the scope via <c>Localizations.ControllerOf(context)</c>.</para>
/// </summary>
public sealed class LocalizationsController
{
    private readonly IReadOnlyList<LocalizationsDelegate> _delegates;
    private Directionality? _directionality;
    private Localizations? _provider;

    public LocalizationsController(
        IEnumerable<LocalizationsDelegate> delegates,
        IReadOnlyList<Locale> supportedLocales,
        Locale initial,
        Locale fallback)
    {
        _delegates = delegates.ToList();
        SupportedLocales = supportedLocales;
        FallbackLocale = fallback;
        Locale = initial;
        Current = new Signal<Locale>(initial);
    }

    /// <summary>The active locale (already resolved against the supported set).</summary>
    public Locale Locale { get; private set; }

    /// <summary>The locales the app offers.</summary>
    public IReadOnlyList<Locale> SupportedLocales { get; }

    /// <summary>The locale used when a translation is missing for the active one.</summary>
    public Locale FallbackLocale { get; }

    /// <summary>Reactive view of the active locale — subscribe to update a settings screen, etc.</summary>
    public Signal<Locale> Current { get; }

    /// <summary>Raised after the active locale changes.</summary>
    public event Action<Locale>? LocaleChanged;

    /// <summary>Run every supporting delegate for a locale and assemble its data snapshot.</summary>
    public LocalizationsData Load(Locale locale)
    {
        var resources = new Dictionary<Type, object>();
        foreach (var d in _delegates)
        {
            if (!d.IsSupported(locale)) continue;
            resources[d.ResourceType] = d.LoadResource(locale); // later delegate of a type wins
        }

        return new LocalizationsData(
            locale: locale,
            textDirection: locale.TextDirection,
            resources: resources
        );
    }

    internal void Bind(Localizations provider, Directionality directionality)
    {
        _provider = provider;
        _directionality = directionality;
        provider.Controller = this;
    }

    /// <summary>
    ///     Resolve <paramref name="requested" /> against the supported set and switch to it, rebuilding
    ///     the subtree. Returns <c>false</c> when the resolved locale is already active (a no-op).
    /// </summary>
    public bool SetLocale(Locale requested)
    {
        var pool = SupportedLocales.Count > 0 ? SupportedLocales : new[] { requested };
        var resolved = LocaleResolution.Resolve(
            preferred: requested,
            supported: pool,
            fallback: FallbackLocale
        );

        if (resolved == Locale && _provider is not null) return false;

        Locale = resolved;
        var data = Load(resolved);
        if (_provider is not null) _provider.Data = data;
        if (_directionality is not null) _directionality.Direction = data.TextDirection;

        Current.Value = resolved;
        LocaleChanged?.Invoke(resolved);
        return true;
    }
}
