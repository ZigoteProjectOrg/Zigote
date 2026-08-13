using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Widgets.Navigation;

/// <summary>
///     A declarative description of a route for the <b>Navigator 2.0</b> page API. A list of pages
///     describes the whole navigation stack; the <see cref="Navigator" /> reconciles its live
///     routes against that list (matching by <see cref="Key" />), animating pages in and out as the
///     list changes.
///     <para>Give pages a stable <see cref="Key" /> so their route state survives reordering.</para>
/// </summary>
public abstract class Page
{
    /// <summary>Stable identity used to match this page to a live route across list updates.</summary>
    public Key? Key { get; init; }

    /// <summary>Optional route name (exposed via <see cref="RouteSettings.Name" />).</summary>
    public string? Name { get; init; }

    /// <summary>Optional arguments (exposed via <see cref="RouteSettings.Arguments" />).</summary>
    public object? Arguments { get; init; }

    /// <summary>Settings derived from this page, applied to its route.</summary>
    public RouteSettings ToSettings()
    {
        return Name is null && Arguments is null
            ? RouteSettings.Empty
            : new RouteSettings(Name, Arguments);
    }

    /// <summary>Create the live route that renders this page.</summary>
    internal abstract Route CreateRoute();
}

/// <summary>
///     A <see cref="Page" /> backed by a widget (or a <see cref="WidgetBuilder" />), rendered with the
///     standard slide-and-fade <see cref="PageRoute{T}" /> transition.
/// </summary>
public sealed class MaterialPage : Page
{
    public MaterialPage()
    {
    }

    public MaterialPage(Widget child, Key? key = null, string? name = null)
    {
        Child = child;
        Key = key;
        Name = name;
    }

    /// <summary>The page content (used when <see cref="Builder" /> is null).</summary>
    public Widget? Child { get; init; }

    /// <summary>Builds the page content lazily. Takes precedence over <see cref="Child" />.</summary>
    public WidgetBuilder? Builder { get; init; }

    /// <summary>When false the page appears/disappears instantly (no transition).</summary>
    public bool Animate { get; init; } = true;

    internal override Route CreateRoute()
    {
        return new PageBackedRoute(this) { Settings = ToSettings() };
    }
}

/// <summary>The route that renders a <see cref="MaterialPage" />.</summary>
internal sealed class PageBackedRoute : PageRoute<object?>
{
    private readonly MaterialPage _page;

    public PageBackedRoute(MaterialPage page)
    {
        _page = page;
    }

    public override float TransitionDuration => _page.Animate ? 0.30f : 0f;

    protected override Widget BuildContent(BuildContext context)
    {
        return _page.Builder?.Invoke(context) ?? _page.Child ?? new SizedBox();
    }
}
