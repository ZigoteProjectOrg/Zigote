namespace Zigote.UI.Localizations;

/// <summary>
///     An immutable snapshot of the localization state in scope: the active <see cref="Locale" />, its
///     <see cref="TextDirection" />, and the resources produced by each registered delegate keyed by
///     type. Retrieve a resource with <see cref="Get{T}" /> (or <c>Localizations.Of&lt;T&gt;</c>).
/// </summary>
public sealed class LocalizationsData
{
    /// <summary>The empty default used when no <see cref="Localizations" /> ancestor is in scope.</summary>
    public static readonly LocalizationsData Empty =
        new(Locale.En, TextDirection.Ltr, new Dictionary<Type, object>(0));

    private readonly IReadOnlyDictionary<Type, object> _resources;

    public LocalizationsData(Locale locale, TextDirection textDirection,
        IReadOnlyDictionary<Type, object> resources)
    {
        Locale = locale;
        TextDirection = textDirection;
        _resources = resources;
    }

    public Locale Locale { get; }
    public TextDirection TextDirection { get; }

    /// <summary>The loaded resource of type <typeparamref name="T" />, or <c>null</c> if no delegate produced one.</summary>
    public T? Get<T>() where T : class
    {
        return _resources.TryGetValue(typeof(T), out var r) ? (T)r : null;
    }

    public bool Has<T>()
    {
        return _resources.ContainsKey(typeof(T));
    }
}

/// <summary>
///     The <see cref="InheritedWidget" /> that publishes the current <see cref="LocalizationsData" /> to
///     its subtree. Mirrors <c>ThemeProvider</c>: reassigning <see cref="Data" /> rebuilds every
///     descendant that read it (via <c>context.Tr</c>, <c>Localizations.Of</c>, etc.). Normally created
///     and driven by a <see cref="LocalizationsScope" /> rather than constructed directly.
/// </summary>
public sealed class Localizations : InheritedWidget
{
    private LocalizationsData _data;

    public Localizations(LocalizationsData data, Widget? child = null)
    {
        _data = data;
        Child = child;
    }

    /// <summary>The controller driving this provider, when created by a <see cref="LocalizationsScope" />.</summary>
    public LocalizationsController? Controller { get; internal set; }

    /// <summary>The current data. Reassigning to a new snapshot relayouts and rebuilds dependents.</summary>
    public LocalizationsData Data
    {
        get => _data;
        set
        {
            if (ReferenceEquals(_data, value)) return;
            _data = value;
            MarkNeedsLayout();
            NotifyDependents();
        }
    }

    // ── Static accessors (T.Of(context) style) ───────────────────────────────

    /// <summary>The data in scope (registers a dependency); falls back to <see cref="LocalizationsData.Empty" />.</summary>
    public static LocalizationsData Of(BuildContext ctx)
    {
        return ctx.DependOn<Localizations>()?.Data ?? LocalizationsData.Empty;
    }

    /// <summary>The data in scope, or <c>null</c> when there is no provider.</summary>
    public static LocalizationsData? MaybeOf(BuildContext ctx)
    {
        return ctx.DependOn<Localizations>()?.Data;
    }

    /// <summary>The resource of type <typeparamref name="T" /> in scope (registers a dependency).</summary>
    public static T? Of<T>(BuildContext ctx) where T : class
    {
        return ctx.DependOn<Localizations>()?.Data.Get<T>();
    }

    /// <summary>The active locale in scope (registers a dependency).</summary>
    public static Locale LocaleOf(BuildContext ctx)
    {
        return Of(ctx).Locale;
    }

    /// <summary>The controller in scope, if any — without registering a dependency (for actions/callbacks).</summary>
    public static LocalizationsController? ControllerOf(BuildContext ctx)
    {
        return ctx.FindAncestor<Localizations>()?.Controller;
    }

    public override bool UpdateShouldNotify(InheritedWidget oldWidget)
    {
        return oldWidget is not Localizations old || !ReferenceEquals(old.Data, Data);
    }
}
