using Zigote.Core.State;

namespace Zigote.UI.Adwaita;

/// <summary>One tab of an <see cref="AdwTabView" />: a title (+ optional icon) and its content.</summary>
public sealed class AdwTabPage
{
    public AdwTabPage(string title, Widget child, string? iconName = null)
    {
        Title = title;
        Child = child;
        IconName = iconName;
    }

    public string Title { get; set; }
    public Widget Child { get; set; }
    public string? IconName { get; set; }

    /// <summary>Pinned tabs render icon-only, without a close button.</summary>
    public bool Pinned { get; set; }
}

/// <summary>
///     AdwTabView — holds a dynamic list of <see cref="AdwTabPage" />s and renders the selected
///     page's content. Pair with <see cref="AdwTabBar" /> for the tab strip.
/// </summary>
public sealed class AdwTabView : ComposedWidget
{
    /// <summary>Fired on Append/Close so the tab bar rebuilds its strip.</summary>
    internal readonly Trigger PagesChanged = new();

    /// <summary>Selection, signal-backed so the tab bar can react.</summary>
    internal readonly Signal<int> Selected = new(0);

    public AdwTabView(params AdwTabPage[] pages) : this((IEnumerable<AdwTabPage>)pages) { }

    public AdwTabView(IEnumerable<AdwTabPage> pages) => Pages = [.. pages];

    public List<AdwTabPage> Pages { get; }

    public int SelectedIndex
    {
        get => Selected.Value;
        set => Selected.Value = Math.Clamp(
            value: value,
            min: 0,
            max: Math.Max(val1: 0, val2: Pages.Count - 1)
        );
    }

    public Action<AdwTabPage>? OnClosed { get; set; }

    /// <summary>Add a page at the end and select it.</summary>
    public void Append(AdwTabPage page)
    {
        Pages.Add(page);
        PagesChanged.Fire();
        SelectedIndex = Pages.Count - 1;
    }

    /// <summary>Remove a page, keeping the selection on the same page where possible.</summary>
    public void Close(AdwTabPage page)
    {
        int index = Pages.IndexOf(page);
        if (index < 0) return;
        Pages.RemoveAt(index);

        int selected = Selected.Peek();
        if (index < selected) selected--;
        Selected.Value = Math.Clamp(
            value: selected,
            min: 0,
            max: Math.Max(val1: 0, val2: Pages.Count - 1)
        );

        PagesChanged.Fire();
        OnClosed?.Invoke(page);
    }

    protected override Widget Build(BuildContext context)
    {
        return new Watch(() =>
            {
                PagesChanged.Depend();
                int index = Selected.Value;
                if (Pages.Count == 0) return new SizedBox();
                index = Math.Clamp(value: index, min: 0, max: Pages.Count - 1);
                return Pages[index].Child;
            }
        );
    }
}
