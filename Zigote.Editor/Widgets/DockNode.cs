namespace Zigote.Editor.Widgets;

public enum DropZone
{
    Left,
    Right,
    Top,
    Bottom,
    Center,
}

public abstract class DockNode
{
    /// <summary>All panel ids contained anywhere under this node.</summary>
    public abstract IEnumerable<string> LeafIds();
}

/// <summary>
///     A dock region holding one or more panels shown as tabs. The active tab's content
///     fills the region below the tab bar.
/// </summary>
public sealed class DockLeaf : DockNode
{
    public DockLeaf(string panelId)
    {
        PanelIds = [panelId];
    }

    public DockLeaf(IEnumerable<string> panelIds)
    {
        PanelIds = panelIds.ToList();
    }

    public string LeafId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public List<string> PanelIds { get; }
    public int ActiveIndex { get; set; }

    /// <summary>
    ///     When true the region renders only its tab strip (content hidden) and shrinks to a thin
    ///     bar along its parent split's axis. Clicking the strip expands it again. Persisted per project.
    /// </summary>
    public bool Collapsed { get; set; }

    public string ActivePanelId =>
        PanelIds[Math.Clamp(ActiveIndex, 0, Math.Max(0, PanelIds.Count - 1))];

    public override IEnumerable<string> LeafIds()
    {
        return PanelIds;
    }
}

public sealed class DockSplit : DockNode
{
    public DockSplit(DockNode first, DockNode second, bool vertical = false, float ratio = 0.5f)
    {
        First = first;
        Second = second;
        Vertical = vertical;
        Ratio = ratio;
    }

    public DockNode First { get; set; }
    public DockNode Second { get; set; }
    public bool Vertical { get; set; }
    public float Ratio { get; set; }

    public override IEnumerable<string> LeafIds()
    {
        foreach (var id in First.LeafIds()) yield return id;
        foreach (var id in Second.LeafIds()) yield return id;
    }
}