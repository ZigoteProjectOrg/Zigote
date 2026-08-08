using Zigote.Core.State;
using Zigote.UI.Widgets.Transitions;

namespace Zigote.UI.Adwaita;

/// <summary>
///     One named page of an <see cref="AdwViewStack" /> — a title (+ optional icon and badge count)
///     for the switchers, and the content widget shown when the page is visible.
/// </summary>
public sealed class AdwViewStackPage
{
    public AdwViewStackPage(
        string name,
        string title,
        Widget child,
        string? iconName = null,
        int badge = 0)
    {
        Name = name;
        Title = title;
        Child = child;
        IconName = iconName;
        Badge = badge;
    }

    /// <summary>Stable identifier used by <see cref="AdwViewStack.VisibleName" />.</summary>
    public string Name { get; set; }

    public string Title { get; set; }
    public Widget Child { get; set; }

    /// <summary>Icon glyph (an <see cref="Icons" />/<see cref="MaterialIcons" /> constant), or null.</summary>
    public string? IconName { get; set; }

    /// <summary>Badge count shown by the switchers when &gt; 0 (unread items, …).</summary>
    public int Badge { get; set; }
}

/// <summary>
///     AdwViewStack — shows exactly one of its named pages, selected by
///     <see cref="VisibleName" />. Pair with <see cref="AdwViewSwitcher" />,
///     <see cref="AdwViewSwitcherBar" /> or <see cref="AdwInlineViewSwitcher" /> to switch pages.
///     Page changes cross-fade (~200 ms ease-out); both layers stay retained during the fade.
/// </summary>
public sealed class AdwViewStack : StatelessWidget
{
    /// <summary>The visible page name, signal-backed so switchers can react.</summary>
    internal readonly Signal<string> Visible;

    private AnimatedSwitcher? _switcher;

    public AdwViewStack(params AdwViewStackPage[] pages)
        : this((IEnumerable<AdwViewStackPage>)pages)
    {
    }

    public AdwViewStack(IEnumerable<AdwViewStackPage> pages)
    {
        Pages = [.. pages];
        Visible = new Signal<string>(Pages.Count > 0 ? Pages[0].Name : "");
    }

    public List<AdwViewStackPage> Pages { get; }

    /// <summary>The name of the page currently shown. Setting it cross-fades to the page.</summary>
    public string VisibleName
    {
        get => Visible.Value;
        set
        {
            if (Visible.Peek() == value) return;
            Visible.Value = value;
            if (_switcher is not null) _switcher.Child = Resolve(value);
            OnVisibleChanged?.Invoke(value);
        }
    }

    public Action<string>? OnVisibleChanged { get; set; }

    private Widget Resolve(string name)
    {
        var page = Pages.Find(p => p.Name == name) ?? (Pages.Count > 0 ? Pages[0] : null);
        return page?.Child ?? new SizedBox();
    }

    protected override Widget Build(BuildContext context)
    {
        // Retained AnimatedSwitcher does the two-layer crossfade; the VisibleName setter feeds it.
        return _switcher = new AnimatedSwitcher(Resolve(Visible.Peek()), 0.2f);
    }
}