using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Core.State;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets;

/// <summary>
///     A reactive subtree: runs <paramref name="build" /> under dependency tracking and rebuilds ONLY
///     when a <see cref="Signal{T}" />/<see cref="Computed{T}" /> it read changes — the C# counterpart
///     of the F# <c>Ui.bind</c>, and the framework's bridge from a signal to the retained widget tree
///     (replacing the old <c>BlocBuilder</c>/<c>Cubit</c> pattern). Drop signals straight into any tree:
///     <code>
///   new Watch(() => new Label($"Count: {count.Value}"))
/// </code>
///     <para>
///         The builder is wrapped in a <see cref="Computed{T}" /> that auto-tracks whatever it reads, so
///         there is no dependency list. A change recomputes the subtree; on the UI thread it swaps in
///         place, and an off-thread change (a timer/async completion setting a signal) is marshalled —
///         the loop is woken via <c>RequestLayout</c> and the swap happens in <see cref="Measure" /> on
///         the UI thread. Like any rebuild in this retained framework, the new subtree replaces the old,
///         so hoist stateful children (or give them keys via a container) if they must survive a rebuild.
///     </para>
/// </summary>
public sealed class Watch : Widget
{
    private readonly Func<Widget> _build;
    private Computed<Widget>? _computed;
    private Widget? _child;
    private IDisposable? _subscription;
    private Size _size;
    private bool _started;
    private bool _detached;
    private bool _dirty;
    private bool _measuredOnce;
    private int _uiThread;

    // The inherited-widget scope (theme, media query, localisations) this Watch was last measured
    // under. Captured during the walk and re-entered for the out-of-walk rebuild below, which would
    // otherwise build its subtree against an empty scope — see BuildContext.CaptureScope.
    private InheritedWidget[] _scope = [];
    private int _scopeDepth;

    public Watch(Func<Widget> build)
    {
        _build = build;
    }

    /// <summary>
    ///     Subtree swaps applied by every <see cref="Watch" /> since start, excluding first
    ///     materialisation — the UI-side half of <see cref="Reactive.Runs" />, and the number that tells
    ///     you a screen is rebuilding when nothing visible changed. Diagnostics only; surfaced as
    ///     <c>ui.watch_rebuilds</c> in devtools.
    /// </summary>
    /// <remarks>
    ///     Per-instance counts live on the inherited <see cref="Widget.RebuildCount" /> — the same field
    ///     <c>ComposedWidget</c> bumps, and what the inspector already reads.
    /// </remarks>
    public static long Rebuilds { get; private set; }

    private void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        _uiThread = Environment.CurrentManagedThreadId;
        _computed =
            Computed.From(_build); // eager first build (on the UI thread), auto-tracks its reads

        // Observe FIRST, then materialise. The order is load-bearing: a signal that changes between
        // the computed's evaluation and this subscription is handled inside Observe's first effect
        // run — Track() connects the computed, Connect() sees the stale version and silently
        // recomputes — but the `first` flag suppresses the callback for that run, so nothing tells
        // the Watch. Applying AFTER the subscription reads that recomputed value and swaps it in;
        // applying before it reads the stale one, and with the change already consumed nothing ever
        // invalidates again — the Watch shows its first build forever. In an app that is a screen
        // stuck on its spinner because the load landed while the first layout pass was mounting it
        // (Mahou.Tests.WatchRaceTests reproduced 3232 lost swaps in 5000 with the old order).
        _subscription = ((ISignal)_computed).Observe(OnChanged);
        Apply();
    }

    /// <summary>
    ///     Re-measure the freshly swapped subtree against the constraints this Watch was last given.
    ///     If it wants the same size it did before, no ancestor's geometry can have changed by
    ///     definition — so lay the subtree out in place and ask for a repaint, instead of dirtying the
    ///     parent chain and making the App re-walk the WHOLE tree.
    ///     <para>
    ///         This is the retained-mode payoff, and it is the difference between a screen of reactive
    ///         cells costing O(changed) and O(tree) per frame: with 10 000 cells on screen, one changed
    ///         cell used to cost a 1.2 ms full-tree Measure+Layout, the same as ten thousand changed
    ///         cells. A size CHANGE still falls through to the normal upward invalidation — that one
    ///         genuinely can move everything around it.
    ///     </para>
    /// </summary>
    private bool TryRelayoutInPlace()
    {
        if (_child is null || Owner is null || !_measuredOnce) return false;

        var size = _child.Measure(LastConstraints);
        if (size != _size) return false; // our size moved — ancestors must re-layout

        _child.Layout(new Offset(Bounds.X, Bounds.Y));
        MarkNeedsPaint(); // damage is this widget's region only
        return true;
    }

    // Swap in the freshly-built subtree (UI thread only).
    private void Apply(bool inPlace = false)
    {
        var next = _computed!.Value;
        if (ReferenceEquals(next, _child)) return;

        var previous = _child;
        if (previous is not null)
        {
            Rebuilds++; // UI thread only, like the rest of Apply
            RebuildCount++; // a Watch swap is this widget's rebuild — the inspector's R: column
        }

        _child = next;
        SwapChild(previous, _child); // attach-then-detach; see Widget.SwapChild for why that order

        if (inPlace && TryRelayoutInPlace()) return;
        MarkNeedsLayout();
    }

    private void OnChanged()
    {
        if (_detached) return;

        // Swapping while the measure/layout/paint walk is running would mutate the tree mid-walk
        // (a parent has already sized its arrays / cached its ranges for this pass) — defer exactly
        // like the off-thread path; the swap lands in Measure next frame.
        if (Environment.CurrentManagedThreadId == _uiThread && Owner is not { InTreeWalk: true })
        {
            // Safe to re-measure in place here precisely BECAUSE no walk is running (checked above)
            // — and for the same reason the ambient inherited scope is empty, so the subtree about
            // to be built and measured has to be given the one this Watch actually sits under. Skip
            // that when nothing was captured yet (never measured): there is nothing to restore.
            var ctx = BuildContext.Current;
            ctx.EnterScope(_scope, _scopeDepth);
            try
            {
                Apply(true);
            }
            finally
            {
                ctx.ExitScope(_scopeDepth);
            }
        }
        else
        {
            // Off the UI thread: flag the swap and ask the App to mark this widget's ancestor chain for
            // layout on the UI thread next frame — the App-level layout flag alone lets cached ancestors
            // (ComposedWidget) skip re-measuring this subtree, so the swap in Measure would never run.
            _dirty = true;
            Owner?.InvalidateLayoutFromAnyThread(this);
        }
    }

    public override void Attach(App owner, Widget? parent)
    {
        _detached = false;
        EnsureStarted();
        base.Attach(owner, parent); // attaches the current _child via GetChildren
    }

    public override void Detach()
    {
        _detached = true;
        _subscription?.Dispose();
        _subscription = null;
        _computed?.Dispose();
        _computed = null;
        base.Detach(); // detaches _child via GetChildren
        _child = null;
        _started = false;
        _dirty = false;
        _measuredOnce = false; // a re-attached Watch must not lay out against stale constraints
        // Nor build against a stale scope — and holding the captured ancestors would keep a detached
        // subtree's providers alive. Array.Clear rather than a fresh array: the buffer is reused.
        Array.Clear(_scope, 0, _scopeDepth);
        _scopeDepth = 0;
    }

    public override Size Measure(Constraints constraints)
    {
        EnsureStarted();
        if (_dirty)
        {
            _dirty = false;
            Apply();
        }

        LastConstraints = constraints; // remembered for the in-place path — see TryRelayoutInPlace
        // Same reason, for the ancestors rather than the constraints: this runs inside the walk, so
        // the scope on the context right now is the one an out-of-walk rebuild has to reproduce.
        _scopeDepth = BuildContext.Current.CaptureScope(ref _scope);
        _measuredOnce = true;
        _size = _child?.Measure(constraints) ?? constraints.Constrain(Size.Zero);
        MeasuredSize = _size;
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
        _child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _child?.Paint(paint);
    }

    // The child's answer, including "nothing": a Watch is a container, and every other container
    // (Align, Stack, the route transitions) lets a miss fall through to whatever is underneath.
    // This one used to answer `this` on a miss — so a full-screen Watch overlaying content (a
    // reader's chrome bar over the page) silently ate every click and wheel event beneath it, and
    // being non-focusable, dropped keyboard focus with them.
    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _child?.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(_child);
    }

    public override int DebugStateHash()
    {
        return _child?.DebugStateHash() ?? 0;
    }
}