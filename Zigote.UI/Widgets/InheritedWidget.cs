using System.Runtime.CompilerServices;
using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets;

/// <summary>
///     A widget that provides data to all of its descendants.
///     During the Measure pass it pushes itself onto <see cref="BuildContext.Current" />;
///     nested widgets (including every <see cref="ComposedWidget" />)
///     can call <c>BuildContext.Of&lt;T&gt;(ctx)</c> or <c>ctx.FindAncestor&lt;T&gt;()</c>
///     to retrieve it.
/// </summary>
public abstract class InheritedWidget : Widget
{
    // Weak-keyed so a dependent that detaches (e.g. a popped route's widgets) can be garbage-collected
    // even though it never re-builds to re-register. A strong HashSet leaked every widget that had ever
    // read this inherited data until the next data change — unbounded under a static theme/media query.
    private readonly ConditionalWeakTable<Widget, object?> _dependents = new();
    private Size _size;

    public Widget? Child { get; set; }

    /// <summary>
    ///     Return true if dependents should be considered changed when this widget
    ///     is replaced by <paramref name="oldWidget" /> in the tree.
    ///     Used by future dependency-tracking optimisations; override conservatively.
    /// </summary>
    public abstract bool UpdateShouldNotify(InheritedWidget oldWidget);

    /// <summary>Register <paramref name="w" /> to be rebuilt when this widget's data changes.</summary>
    internal void AddDependent(Widget w) => _dependents.AddOrUpdate(key: w, value: null);

    /// <summary>
    ///     Rebuild every still-live dependent. Collected/detached dependents are simply absent from the
    ///     weak table, so they are neither notified nor retained. The set is cleared afterwards — each
    ///     live dependent re-registers via <see cref="BuildContext.DependOn{T}" /> the next time it
    ///     builds.
    /// </summary>
    protected void NotifyDependents()
    {
        List<Widget>? live = null;
        foreach (var kv in _dependents)
            (live ??= []).Add(kv.Key);
        if (live is null) return;
        _dependents.Clear();
        foreach (var d in live) d.MarkNeedsBuild();
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        var ctx = BuildContext.Current;
        ctx.Push(this);
        try
        {
            _size = Child?.Measure(c) ?? new Size(width: 0f, height: 0f);
            return _size;
        }
        finally
        {
            ctx.Pop(this);
        }
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        // Push onto the context during Layout too (not just Measure): some widgets measure their
        // children from their own Layout (e.g. SplitPane, ColorPicker), and those re-measures must still
        // see this inherited data — otherwise a control reading ThemeProvider.Of in Measure would fall
        // back to the default theme and render as if in the wrong appearance.
        var ctx = BuildContext.Current;
        ctx.Push(this);
        try
        {
            Child?.Layout(origin);
        }
        finally
        {
            ctx.Pop(this);
        }
    }

    public override void Paint(PaintList paint) => Child?.Paint(paint);

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override int DebugStateHash() => Child?.DebugStateHash() ?? 0;

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
