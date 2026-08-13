using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Focus;

/// <summary>
///     A boundary for keyboard focus traversal. Tab / Shift-Tab and directional arrow navigation are
///     confined to the focusable descendants of the nearest enclosing <see cref="FocusScope" /> (or
///     the
///     topmost modal overlay, or the whole tree when neither applies). Use it to keep Tab inside a
///     panel,
///     a form, or a custom in-tree popover.
///     <para>
///         Modal overlays (e.g. <see cref="Controls.Dialog" />) trap focus automatically by virtue of
///         being the top overlay — they don't need a FocusScope. This widget is for in-<c>Root</c>
///         traps.
///     </para>
/// </summary>
public sealed class FocusScope : Widget
{
    private Size _size;

    public FocusScope(Widget child)
    {
        Child = child;
    }

    public Widget Child { get; }

    /// <summary>
    ///     When true (default) Tab/arrow traversal cannot leave this subtree; false makes it advisory
    ///     only.
    /// </summary>
    public bool Trap { get; set; } = true;

    /// <summary>Focus the first focusable descendant when this scope is first laid out.</summary>
    public bool AutoFocus { get; set; }

    public override Size Measure(Constraints c)
    {
        _size = Child.Measure(c);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        Child.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        Child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return Child.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }
}
