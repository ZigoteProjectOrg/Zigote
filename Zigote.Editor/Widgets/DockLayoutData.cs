namespace Zigote.Editor.Widgets;

/// <summary>
///     Serializable dock-tree node, persisted per project as the <c>layout.dock</c> preference: a
///     leaf carries <see cref="Panels" />/<see cref="Active" />/<see cref="Collapsed" />, a split
///     carries <see cref="First" />/<see cref="Second" />/<see cref="Vertical" />/<see cref="Ratio" />.
///     Convert to/from live <see cref="DockNode" /> trees with <see cref="DockLayoutStore" />.
/// </summary>
public sealed class DockLayoutData
{
    public List<string>? Panels { get; set; } // leaf
    public int Active { get; set; }
    public bool Collapsed { get; set; }
    public DockLayoutData? First { get; set; } // split
    public DockLayoutData? Second { get; set; }
    public bool Vertical { get; set; }
    public float Ratio { get; set; }
}