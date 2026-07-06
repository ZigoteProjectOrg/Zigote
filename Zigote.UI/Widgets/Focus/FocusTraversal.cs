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
    public static bool IsFocusVisible(Widget w)
    {
        return w.Bounds.Width > 0.5f && w.Bounds.Height > 0.5f;
    }

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
            Collect(child, into);
    }

    public static List<Widget> Focusables(Widget scope)
    {
        var list = new List<Widget>();
        Collect(scope, list);
        return list;
    }

    public static bool HasFocusable(Widget w)
    {
        if (w.Focusable && IsFocusVisible(w)) return true;
        foreach (var child in w.GetVisibleChildren())
            if (HasFocusable(child))
                return true;
        return false;
    }

    /// <summary>
    ///     Next/previous focusable in Tab order, wrapping at the ends. Null when
    ///     <paramref name="order" /> is empty.
    /// </summary>
    public static Widget? NextInTab(IReadOnlyList<Widget> order, Widget? current, bool backwards)
    {
        if (order.Count == 0) return null;
        var idx = current is null ? -1 : IndexOf(order, current);
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
        float fx = fb.X + fb.Width / 2f, fy = fb.Y + fb.Height / 2f;
        Widget? best = null;
        var bestScore = float.MaxValue;
        foreach (var c in order)
        {
            if (ReferenceEquals(c, current)) continue;
            var cb = c.Bounds;
            float cx = cb.X + cb.Width / 2f, cy = cb.Y + cb.Height / 2f;
            float ex = cx - fx, ey = cy - fy;
            var along = ex * dx + ey * dy;
            if (along <= 1f) continue; // not ahead in the pressed direction
            var cross = MathF.Abs(ex * dy - ey * dx);
            var score = along + cross * 2f;
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
        for (var i = 0; i < order.Count; i++)
            if (ReferenceEquals(order[i], w))
                return i;
        return -1;
    }
}