using Zigote.Core;
using Zigote.UI.Widgets;

namespace Zigote.UI.Semantics;

/// <summary>
///     One immutable node in the accessibility tree, produced from a widget's
///     <see cref="SemanticsConfiguration" /> by the <see cref="SemanticsBuilder" />. The tree mirrors
///     the
///     widget tree but is collapsed to only the nodes that carry meaning — decorative wrappers are
///     elided, so a screen reader (via an <see cref="ISemanticsBridge" />) walks a clean, role-tagged
///     structure rather than the raw layout hierarchy.
/// </summary>
public sealed class SemanticsNode
{
    public SemanticsNode(int id, SemanticsRole role, Rect bounds)
    {
        Id = id;
        Role = role;
        Bounds = bounds;
    }

    /// <summary>
    ///     Stable per-widget identity (see <see cref="Widget.SemanticsId" />), so a bridge can diff
    ///     frames.
    /// </summary>
    public int Id { get; }

    public SemanticsRole Role { get; init; }
    public string? Label { get; init; }
    public string? Value { get; init; }
    public string? Hint { get; init; }
    public SemanticsFlags Flags { get; init; }
    public SemanticsAction Actions { get; init; }

    /// <summary>Absolute screen-space rect of the originating widget.</summary>
    public Rect Bounds { get; init; }

    /// <summary>The widget this node was built from — used to route invoked actions back to it.</summary>
    public Widget? Source { get; init; }

    public List<SemanticsNode> Children { get; } = [];

    public bool HasFlag(SemanticsFlags flag) => (Flags & flag) != 0;

    public bool HasAction(SemanticsAction action) => (Actions & action) != 0;

    /// <summary>Depth-first enumeration of this node and all descendants.</summary>
    public IEnumerable<SemanticsNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        foreach (var n in child.Flatten())
            yield return n;
    }

    /// <summary>Count of nodes in this subtree (including this node).</summary>
    public int Count()
    {
        int total = 1;
        foreach (var c in Children) total += c.Count();
        return total;
    }

    /// <summary>
    ///     A short, single-line announcement string ("Button: Save", "Checkbox, checked: Wi-Fi") — the
    ///     order/shape a screen reader would read. Also drives the debug Semantics panel + tests.
    /// </summary>
    public string Describe()
    {
        string role = Role == SemanticsRole.None ? "" : Role.ToString();
        string name = Label ?? Value ?? "";
        string state = "";
        if (HasFlag(SemanticsFlags.Disabled)) state = ", disabled";
        else if (HasFlag(SemanticsFlags.Mixed)) state = ", mixed";
        else if (HasFlag(SemanticsFlags.Checked)) state = ", checked";
        else if (HasFlag(SemanticsFlags.Checkable)) state = ", unchecked";
        else if (HasFlag(SemanticsFlags.Selected)) state = ", selected";

        string head = role.Length > 0 ? role + state : name;
        if (role.Length > 0 && name.Length > 0) return $"{head}: {name}";
        return head.Length > 0 ? head : name;
    }
}
