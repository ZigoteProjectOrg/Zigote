using Zigote.UI.Host;

namespace Zigote.UI.Localizations;

/// <summary>
///     The declarative entry point: wrap your app's <c>Home</c> in a <see cref="LocalizationsScope" />
///     to make translations, the active locale, and text direction available to the subtree — and to
///     enable runtime locale switching. Works under a plain <c>ZigoteApp</c> / <c>MaterialApp</c>, so
///     it
///     stays modular (no bespoke app subclass required).
///     <code>
///   new ZigoteApp
///   {
///       Home = new LocalizationsScope
///       {
///           Bundle           = myBundle,          // key → message catalogs (declarative)
///           SupportedLocales = { Locale.En, Locale.Es, Locale.Ar },
///           FallbackLocale   = Locale.En,
///           Child            = new MyHomePage(),
///       },
///   }.Run();
///   </code>
///     Switch at runtime from anywhere below it: <c>context.SetLocale(Locale.Es)</c>.
///     <para>
///         It is a transparent single-child wrapper (not a rebuilding <c>StatefulWidget</c>): it
///         builds
///         the retained provider subtree once and delegates layout/paint to it, so a hot reload
///         rebuilds
///         the consumers below it <b>without</b> tearing down and recreating their <c>WidgetState</c>.
///     </para>
/// </summary>
public sealed class LocalizationsScope : Widget
{
    private bool _built;
    private Directionality? _directionality;
    private Localizations? _provider;

    /// <summary>Typed resource delegates. Composed with <see cref="Bundle" />.</summary>
    public List<LocalizationsDelegate> Delegates { get; init; } = [];

    /// <summary>
    ///     A key-based translation bundle exposed as <see cref="StringLocalizations" /> for
    ///     <c>context.Tr</c>.
    /// </summary>
    public LocalizationBundle? Bundle { get; init; }

    /// <summary>The locales the app offers. Defaults to the <see cref="Bundle" />'s locales when omitted.</summary>
    public List<Locale> SupportedLocales { get; init; } = [];

    /// <summary>
    ///     The starting locale. Empty → resolve from <see cref="Locale.System" /> when
    ///     <see cref="UseSystemLocale" />.
    /// </summary>
    public Locale InitialLocale { get; init; }

    /// <summary>
    ///     The locale to resolve to when the requested one is unsupported. Distinct from a
    ///     <see cref="LocalizationBundle" />'s own <c>FallbackLocale</c> (which is the missing-
    ///     <em>key</em>
    ///     fallback, set on the bundle). Defaults to the first supported locale.
    /// </summary>
    public Locale FallbackLocale { get; init; }

    /// <summary>When <see cref="InitialLocale" /> is empty, resolve the OS locale instead of the fallback.</summary>
    public bool UseSystemLocale { get; init; } = true;

    /// <summary>Install a <see cref="Directionality" /> from the active locale (default on).</summary>
    public bool ProvideDirectionality { get; init; } = true;

    /// <summary>The subtree that consumes localization.</summary>
    public Widget? Child { get; init; }

    /// <summary>Invoked after the active locale changes at runtime.</summary>
    public Action<Locale>? OnLocaleChanged { get; init; }

    /// <summary>The controller, valid once mounted (also reachable via <c>Localizations.ControllerOf</c>).</summary>
    public LocalizationsController? Controller { get; private set; }

    private void EnsureBuilt()
    {
        if (_built) return;
        _built = true;

        // Compose delegates: explicit typed delegates first, then the bundle's string delegate.
        var delegates = new List<LocalizationsDelegate>(Delegates);
        if (Bundle is { } bundle) delegates.Add(bundle.AsDelegate());

        var supported = SupportedLocales.Count > 0
            ? SupportedLocales
            : Bundle?.Locales.ToList() ?? [];

        var fallback = !FallbackLocale.IsEmpty
            ? FallbackLocale
            : supported.Count > 0
                ? supported[0]
                : Locale.En;

        var requested = !InitialLocale.IsEmpty
            ? InitialLocale
            : UseSystemLocale
                ? Locale.System
                : fallback;

        var pool = supported.Count > 0 ? supported : new List<Locale> { requested };
        var initial = LocaleResolution.Resolve(requested, pool, fallback);

        var controller = new LocalizationsController(
            delegates,
            supported,
            initial,
            fallback
        );
        var data = controller.Load(initial);

        _directionality = new Directionality(data.TextDirection);
        _provider = new Localizations(data);

        var content = Child ?? new SizedBox();
        if (ProvideDirectionality)
        {
            _directionality.Child = content;
            _provider.Child = _directionality;
        }
        else
        {
            _provider.Child = content;
        }

        controller.Bind(_provider, _directionality);
        controller.LocaleChanged += RaiseLocaleChanged;
        Controller = controller;
    }

    private void RaiseLocaleChanged(Locale locale)
    {
        OnLocaleChanged?.Invoke(locale);
    }

    // ── Widget protocol (transparent delegation to the retained provider) ─────

    public override void Attach(App owner, Widget? parent)
    {
        EnsureBuilt();
        base.Attach(owner, parent);
    }

    public override Size Measure(Constraints constraints)
    {
        EnsureBuilt();
        // If the scope was attached before the provider existed, attach the provider subtree now.
        if (Owner != null && _provider!.Owner == null) _provider.Attach(Owner, this);
        MeasuredSize = _provider!.Measure(constraints);
        return MeasuredSize;
    }

    public override void Layout(Offset origin)
    {
        _provider!.Layout(origin);
        Bounds = _provider.Bounds;
    }

    public override void Paint(PaintList paint)
    {
        _provider?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return _provider?.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return _provider is null ? [] : [_provider];
    }

    public override void Detach()
    {
        if (Controller != null) Controller.LocaleChanged -= RaiseLocaleChanged;
        base.Detach();
    }
}