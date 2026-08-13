namespace Zigote.Editor.Widgets;

/// <summary>
///     Converts between the live <see cref="DockNode" /> dock tree and its serializable
///     <see cref="DockLayoutData" /> form (the per-project <c>layout.dock</c> preference).
///     Unknown panel ids are dropped on restore (so layouts survive panels being
///     renamed/removed) and empty subtrees collapse — a fully-unknown layout restores as null,
///     which means "use the default arrangement".
/// </summary>
public static class DockLayoutStore
{
    public static DockLayoutData ToData(DockNode node)
    {
        return node switch {
            DockLeaf l => new DockLayoutData {
                Panels = l.PanelIds.ToList(),
                Active = l.ActiveIndex,
                Collapsed = l.Collapsed,
            },
            DockSplit s => new DockLayoutData {
                First = ToData(s.First),
                Second = ToData(s.Second),
                Vertical = s.Vertical,
                Ratio = s.Ratio,
            },
            _ => new DockLayoutData(),
        };
    }

    /// <summary>
    ///     Rebuild a node, dropping unknown panels and collapsing empty subtrees (returns null if
    ///     empty).
    /// </summary>
    public static DockNode? FromData(DockLayoutData data, IReadOnlySet<string> knownPanels)
    {
        if (data.Panels is { Count: > 0 })
        {
            var ids = data.Panels.Where(knownPanels.Contains).Distinct().ToList();
            if (ids.Count == 0) return null;
            return new DockLeaf(ids) {
                ActiveIndex = Math.Clamp(value: data.Active, min: 0, max: ids.Count - 1),
                Collapsed = data.Collapsed,
            };
        }

        if (data.First != null && data.Second != null)
        {
            var f = FromData(data: data.First, knownPanels: knownPanels);
            var s = FromData(data: data.Second, knownPanels: knownPanels);
            if (f == null) return s;
            if (s == null) return f;
            return new DockSplit(
                first: f,
                second: s,
                vertical: data.Vertical,
                ratio: data.Ratio
            );
        }

        return null;
    }
}
