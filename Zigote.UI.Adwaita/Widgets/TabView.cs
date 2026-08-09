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
    /// <summary>Selection, signal-backed so the tab bar can react.</summary>
    internal readonly Signal<int> Selected = new(0);

    /// <summary>Fired on Append/Close so the tab bar rebuilds its strip.</summary>
    internal readonly Trigger PagesChanged = new();

    public AdwTabView(params AdwTabPage[] pages) : this((IEnumerable<AdwTabPage>)pages)
    {
    }

    public AdwTabView(IEnumerable<AdwTabPage> pages)
    {
        Pages = [.. pages];
    }

    public List<AdwTabPage> Pages { get; }

    public int SelectedIndex
    {
        get => Selected.Value;
        set => Selected.Value = Math.Clamp(value, 0, Math.Max(0, Pages.Count - 1));
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
        var index = Pages.IndexOf(page);
        if (index < 0) return;
        Pages.RemoveAt(index);

        var selected = Selected.Peek();
        if (index < selected) selected--;
        Selected.Value = Math.Clamp(selected, 0, Math.Max(0, Pages.Count - 1));

        PagesChanged.Fire();
        OnClosed?.Invoke(page);
    }

    protected override Widget Build(BuildContext context)
    {
        return new Watch(() =>
            {
                PagesChanged.Depend();
                var index = Selected.Value;
                if (Pages.Count == 0) return new SizedBox();
                index = Math.Clamp(index, 0, Pages.Count - 1);
                return Pages[index].Child;
            }
        );
    }
}