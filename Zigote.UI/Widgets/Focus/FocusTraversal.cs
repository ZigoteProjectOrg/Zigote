using Zigote.UI.Host;

namespace Zigote.UI.Widgets.Focus;

/// <summary>
///     Pure focus-traversal policy, factored out of <see cref="App" /> so it is headless-testable:
///     given a laid-out widget subtree it produces the reading-order list of focusable widgets, and
///     given
///     that list it computes the next Tab target or the nearest neighbour in an arrow direction. The
///     app
///     supplies the active scope (a modal overlay, an enclosing <see cref="FocusScope" />, or the
///     root)
///     and applies the result; none of the geometry/ordering logic lives in the app loop.
/// </summary>
public static class FocusTraversal
{
    /// <summary>
    ///     A focusable is reachable only if it has a non-degenerate laid-out rect (not
    ///     collapsed/off-screen).
    /// </summary>
    public static bool IsFocusVisible(Widget w) => w.Bounds.Width > 0.5f && w.Bounds.Height > 0.5f;

    /// <summary>
    ///     Depth-first (reading order) collection of the visible focusables under
    ///     <paramref name="scope" />.
    /// </summary>
    public static void Collect(Widget scope, List<Widget> into)
    {
        if (scope.Focusable && IsFocusVisible(scope)) into.Add(scope);
        // Visible children only: a hidden TabView page / covered navigator route keeps its last
        // laid-out (non-zero) bounds, so the per-widget rect check alone can't exclude it.
        foreach (var child in scope.GetVisibleChildren())
            Collect(scope: child, into: into);
    }

    public static List<Widget> Focusables(Widget scope)
    {
        var list = new List<Widget>();
        Collect(scope: scope, into: list);
        return list;
    }

    public static bool HasFocusable(Widget w)
    {
        if (w.Focusable && IsFocusVisible(w)) return true;
        foreach (var child in w.GetVisibleChildren())
        {
            if (HasFocusable(child))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The Tab order for <paramref name="scope" />: the full focusable list, except that each
    ///     <see cref="IFocusGroup" /> subtree collapses to a single entry. The surviving entry is
    ///     whichever descendant currently holds focus — so Tab leaves a group from wherever the
    ///     arrows left off — otherwise the group's <see cref="IFocusGroup.TabTarget" />, otherwise
    ///     its first focusable.
    ///     <para>
    ///         Arrow traversal deliberately keeps using <see cref="Focusables" />: grouping is about
    ///         how many Tab presses a list costs, not about which rows the arrows can reach.
    ///     </para>
    /// </summary>
    public static List<Widget> TabOrder(Widget scope, Widget? focused)
    {
        // Groups are tracked on the way DOWN, not by walking Parent back up: Parent is only set by
        // Attach, so an unmounted subtree (and every headless test) has none, and the answer would
        // silently degrade to "no groups" — the exact bug this is meant to prevent.
        List<(Widget Widget, IFocusGroup? Group)> all = [];
        CollectGrouped(w: scope, group: null, into: all);

        var focusedGroup = focused is null
            ? null
            : all.FirstOrDefault(e => ReferenceEquals(objA: e.Widget, objB: focused)).Group;

        List<Widget> order = [];
        IFocusGroup? current = null;
        foreach (var (widget, group) in all)
        {
            if (group is null)
            {
                current = null;
                order.Add(widget);
                continue;
            }

            // A group's focusables are contiguous in reading order, so its run is represented by
            // one entry and the rest are skipped.
            if (ReferenceEquals(objA: group, objB: current)) continue;
            current = group;
            // Tab leaves from wherever the arrows left off; otherwise the group's own target.
            var keep = ReferenceEquals(objA: group, objB: focusedGroup) && focused is not null
                ? focused
                : group.TabTarget ?? widget;
            if (IsFocusVisible(keep)) order.Add(keep);
        }

        return order;
    }

    /// <summary>
    ///     Reading-order collection that tags each focusable with the innermost
    ///     <see cref="IFocusGroup" /> enclosing it, or null.
    /// </summary>
    private static void CollectGrouped(Widget w, IFocusGroup? group,
        List<(Widget Widget, IFocusGroup? Group)> into)
    {
        var inner = w as IFocusGroup ?? group;
        if (w.Focusable && IsFocusVisible(w)) into.Add((w, inner));
        foreach (var child in w.GetVisibleChildren())
            CollectGrouped(w: child, group: inner, into: into);
    }

    /// <summary>
    ///     Next/previous focusable in Tab order, wrapping at the ends. Null when
    ///     <paramref name="order" /> is empty.
    /// </summary>
    public static Widget? NextInTab(IReadOnlyList<Widget> order, Widget? current, bool backwards)
    {
        if (order.Count == 0) return null;
        int idx = current is null ? -1 : IndexOf(order: order, w: current);
        if (backwards) idx = idx <= 0 ? order.Count - 1 : idx - 1;
        else idx = (idx + 1) % order.Count;
        return order[idx];
    }

    /// <summary>
    ///     Nearest focusable whose centre lies in direction (<paramref name="dx" />,
    ///     <paramref name="dy" />)
    ///     from <paramref name="current" />, scoring along-axis distance plus a cross-axis penalty so the
    ///     pick favours the most aligned neighbour. Null when nothing lies ahead.
    /// </summary>
    public static Widget? Directional(IReadOnlyList<Widget> order, Widget current, float dx,
        float dy)
    {
        var fb = current.Bounds;
        float fx = fb.X + (fb.Width / 2f), fy = fb.Y + (fb.Height / 2f);
        Widget? best = null;
        float bestScore = float.MaxValue;
        foreach (var c in order)
        {
            if (ReferenceEquals(objA: c, objB: current)) continue;
            var cb = c.Bounds;
            float cx = cb.X + (cb.Width / 2f), cy = cb.Y + (cb.Height / 2f);
            float ex = cx - fx, ey = cy - fy;
            float along = (ex * dx) + (ey * dy);
            if (along <= 1f) continue; // not ahead in the pressed direction
            float cross = MathF.Abs((ex * dy) - (ey * dx));
            float score = along + (cross * 2f);
            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
    }

    private static int IndexOf(IReadOnlyList<Widget> order, Widget w)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (ReferenceEquals(objA: order[i], objB: w))
                return i;
        }

        return -1;
    }
}
