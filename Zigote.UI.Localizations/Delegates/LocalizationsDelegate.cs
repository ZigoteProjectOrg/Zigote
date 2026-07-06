namespace Zigote.UI.Localizations;

/// <summary>
///     Loads one kind of localized resource for a locale — the modular unit a
///     <see cref="LocalizationsScope" /> composes. Each delegate owns a resource type
///     (<see cref="ResourceType" />); the provider stores one loaded instance per type and hands it
///     back through <c>Localizations.Of&lt;T&gt;(context)</c>. This non-generic base exists so a
///     heterogeneous list of delegates can be held together; author delegates against
///     <see cref="LocalizationsDelegate{T}" />.
/// </summary>
public abstract class LocalizationsDelegate
{
    /// <summary>
    ///     The resource type produced by <see cref="LoadResource" /> — the key under which it is
    ///     stored.
    /// </summary>
    public abstract Type ResourceType { get; }

    /// <summary>Whether this delegate can produce a resource for the locale.</summary>
    public abstract bool IsSupported(Locale locale);

    /// <summary>Build the resource for the locale (synchronous — pre-load anything expensive).</summary>
    public abstract object LoadResource(Locale locale);

    /// <summary>Whether the resource must be rebuilt when the delegate configuration changes.</summary>
    public virtual bool ShouldReload(LocalizationsDelegate old)
    {
        return false;
    }
}

/// <summary>
///     Typed <see cref="LocalizationsDelegate" />. Override <see cref="Load" /> and
///     <see cref="IsSupported" />.
/// </summary>
/// <typeparam name="T">The resource type retrieved via <c>Localizations.Of&lt;T&gt;(context)</c>.</typeparam>
public abstract class LocalizationsDelegate<T> : LocalizationsDelegate where T : notnull
{
    public sealed override Type ResourceType => typeof(T);

    /// <summary>Build the typed resource for the locale.</summary>
    public abstract T Load(Locale locale);

    public sealed override object LoadResource(Locale locale)
    {
        return Load(locale);
    }
}

/// <summary>Factory helpers for lambda-defined delegates.</summary>
public static class LocalizationsDelegates
{
    /// <summary>A delegate from an <c>isSupported</c> predicate and a <c>load</c> factory.</summary>
    public static LocalizationsDelegate<T> Create<T>(Func<Locale, bool> isSupported,
        Func<Locale, T> load)
        where T : notnull
    {
        return new FuncDelegate<T>(isSupported, load);
    }

    /// <summary>A delegate that supports every locale, producing <paramref name="load" />'s result.</summary>
    public static LocalizationsDelegate<T> Always<T>(Func<Locale, T> load) where T : notnull
    {
        return new FuncDelegate<T>(static _ => true, load);
    }

    private sealed class FuncDelegate<T>(Func<Locale, bool> isSupported, Func<Locale, T> load)
        : LocalizationsDelegate<T> where T : notnull
    {
        public override bool IsSupported(Locale locale)
        {
            return isSupported(locale);
        }

        public override T Load(Locale locale)
        {
            return load(locale);
        }
    }
}