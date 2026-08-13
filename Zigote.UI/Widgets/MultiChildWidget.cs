namespace Zigote.UI.Widgets;

/// <summary>
///     Base class for widgets that own an ordered list of children (Row, Column, Stack, Wrap, …).
///     <para>
///         Holds the <see cref="Children" /> list and provides <see cref="SetChildren" />, the
///         key-aware reconciling path for dynamic lists: it preserves the state of reused (keyed or
///         same-reference) children and runs Attach/Detach for the delta. Build the initial children
///         via the constructor or the <see cref="Children" /> collection initializer; use
///         <see cref="SetChildren" /> afterwards when the set changes so identity is preserved.
///     </para>
///     Subclasses implement Measure / Layout / Paint / HitTest over <see cref="Children" />.
/// </summary>
public abstract class MultiChildWidget : Widget
{
    protected MultiChildWidget(IEnumerable<Widget>? children = null)
    {
        if (children is not null) Children.AddRange(children);
    }

    /// <summary>The children, in paint/layout order (last is on top for hit-testing).</summary>
    public List<Widget> Children { get; } = [];

    /// <summary>
    ///     Replace the children with <paramref name="children" />, reconciling by key so reused
    ///     instances keep their state, and marking the widget for re-layout.
    /// </summary>
    public void SetChildren(IEnumerable<Widget> children)
    {
        var list = children as IReadOnlyList<Widget> ?? children.ToList();
        ChildReconciler.Reconcile(current: Children, incoming: list, parent: this);
        MarkNeedsLayout();
    }

    /// <summary>Reconcile this container's children from another container of the same type.</summary>
    public override void UpdateFrom(Widget newWidget)
    {
        if (newWidget.GetType() == GetType() && newWidget is MultiChildWidget m)
            SetChildren(m.Children);
    }

    public override IEnumerable<Widget> GetChildren() => Children;
}
