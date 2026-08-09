namespace Zigote.UI.Widgets;

/// <summary>
///     Base class for widgets that have no children — a glyph, a thumb, a canvas. Everything else a
///     hand-written widget needs (Measure/Layout/Paint, input, semantics, the mount lifecycle) is on
///     <see cref="Widget" /> itself; the only thing a leaf specialises is having nothing below it.
///     <para>
///         <see cref="Widget.Attach" /> and <see cref="Widget.Detach" /> used to be overridden here to
///         skip the child cascade — but that cascade is already a no-op over an empty list, and skipping
///         the base also skipped the mount lifecycle (so an <see cref="Widget.OnMount" /> that binds a
///         ticker never ran, leaving glyph animations dead) and <c>Owner.NotifyDetached</c> (so removing
///         a focused or hovered leaf left the app pointing at an off-tree widget).
///     </para>
/// </summary>
public abstract class LeafWidget : Widget
{
    public override IEnumerable<Widget> GetChildren()
    {
        return [];
    }
}
