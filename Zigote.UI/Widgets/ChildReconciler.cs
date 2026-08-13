namespace Zigote.UI.Widgets;

/// <summary>
///     Key-aware child reconciliation for multi-child widgets.
///     <para>
///         In the retained model widgets persist across frames, so the bug keys solve is identity loss
///         when a child <i>list</i> changes (insert / remove / reorder of logically-stable items).
///         <see cref="Reconcile" /> matches each incoming child to an existing one by
///         <c>(runtime type, Key)</c>; a match reuses the existing instance — preserving its transient
///         state (hover, press, scroll position, in-flight animation) — and forwards the incoming
///         configuration through <see cref="Widget.UpdateFrom" />. Children present only in the new
///         list
///         are attached; children present only in the old list are detached (disposing their state).
///     </para>
///     A child passed by the same reference as before always reuses itself, so the common retained
///     pattern (hold instances in fields) keeps working with no keys at all — keys are needed only
///     when
///     the caller hands over freshly-constructed instances for the same logical items.
/// </summary>
internal static class ChildReconciler
{
    public static void Reconcile(List<Widget> current, IReadOnlyList<Widget> incoming,
        Widget parent)
    {
        // Index existing keyed children for O(1) reuse lookup.
        Dictionary<(Type, Key), Widget>? byKey = null;
        foreach (var w in current)
            if (w.Key is { } k)
            {
                byKey ??= new Dictionary<(Type, Key), Widget>();
                byKey[(w.GetType(), k)] = w;
            }

        var result = new List<Widget>(incoming.Count);
        var kept = new HashSet<Widget>();

        foreach (var inc in incoming)
        {
            var chosen = inc;

            // Reuse an existing keyed instance of the same type (different reference) — preserve state.
            if (inc.Key is { } k && byKey is not null &&
                byKey.TryGetValue((inc.GetType(), k), out var existing) && kept.Add(existing))
            {
                if (!ReferenceEquals(existing, inc)) existing.UpdateFrom(inc);
                chosen = existing;
            }
            else
            {
                kept.Add(inc);
            }

            result.Add(chosen);
        }

        // Detach children that are gone.
        foreach (var w in current)
            if (!kept.Contains(w))
                w.Detach();

        // Attach freshly-added children if the parent is already live in the tree.
        if (parent.Owner is not null)
            foreach (var w in result)
                if (w.Owner is null)
                    w.Attach(parent.Owner, parent);

        current.Clear();
        current.AddRange(result);
    }
}
