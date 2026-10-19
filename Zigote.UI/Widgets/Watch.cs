using System.Runtime.ExceptionServices;
using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Core.State;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets;

/// <summary>
///     A reactive subtree: runs <paramref name="build" /> under dependency tracking and rebuilds ONLY
///     when a <see cref="Signal{T}" />/<see cref="Computed{T}" /> it read changes — the C# counterpart
///     of the F# <c>Ui.bind</c>, and the framework's bridge from a signal to the retained widget tree
///     (replacing the old <c>BlocBuilder</c>/<c>Cubit</c> pattern). Drop signals straight into any
///     tree:
///     <code>
///   new Watch(() => new Label($"Count: {count.Value}"))
/// </code>
///     <para>
///         The builder runs inside an <see cref="Effect" /> that auto-tracks whatever it reads,
///         so there is no dependency list. A change recomputes the subtree; on the UI thread it swaps in
///         place, and an off-thread change (a timer/async completion setting a signal) is marshalled —
///         the loop is woken via <c>RequestLayout</c> and the swap happens in <see cref="Measure" />
///         on
///         the UI thread. The new subtree replaces the old, but transient state carries over —
///         scroll offsets, typed text and caret, focus — paired by key, else by position and type,
///         and a subtree swapped out for another type is restored when it comes back.
///     </para>
/// </summary>
public sealed class Watch : Widget
{
    private readonly Func<Widget> _build;

    // The latest build and its failure, written by the effect under the graph lock and read by
    // Apply under it — an off-thread rebuild hands them to the UI thread through here.
    private Widget? _built;
    private Widget? _child;
    private bool _detached;
    private bool _dirty;
    private Effect? _effect;
    private ExceptionDispatchInfo? _error;
    private bool _measuredOnce;

    // The inherited-widget scope (theme, media query, localisations) this Watch was last measured
    // under. Captured during the walk and re-entered for the out-of-walk rebuild below, which would
    // otherwise build its subtree against an empty scope — see BuildContext.CaptureScope.
    private InheritedWidget[] _scope = [];
    private int _scopeDepth;
    // Subtrees swapped out for a different type, kept so flipping back restores them — and the
    // live child parked on Detach, so a re-attached Watch rebuilds into the state it left.
    private Dictionary<Type, Widget>? _parked;
    private Size _size;
    private bool _started;
    private int _uiThread;

    public Watch(Func<Widget> build) => _build = build;

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
        // One reaction both builds and subscribes, in a single run under the graph lock — so no write
        // can land between the first build and the subscription and be lost (a screen stuck on its
        // spinner because the load landed while the first layout pass was mounting it). Named after
        // the builder, so the diagnostics table says which screen rebuilds, not "Watch.Rebuild".
        _effect = new Effect(body: Rebuild, named: _build);
        Apply();
    }

    // The effect body: build under tracking, then hand the result to the UI side.
    private void Rebuild()
    {
        Widget? next = null;
        ExceptionDispatchInfo? error = null;
        // A rebuild runs wherever the signal was written — a click handler, a worker thread — where
        // the ambient inherited stack is empty or someone else's. Re-enter the scope this Watch was
        // last measured under, so lookups in the builder (context.Tr, ThemeProvider.Of, MediaQuery)
        // resolve as they did on the first build. Empty before the first measure: the first build
        // runs inside the walk and sees the live stack. BuildContext is per-thread, so an off-thread
        // rebuild pushes onto its own thread's stack.
        var ctx = BuildContext.Current;
        int depth = _scopeDepth;
        ctx.EnterScope(buffer: _scope, count: depth);
        try
        {
            next = _build();
        }
        catch (Exception ex)
        {
            // Caught, not thrown: a body that throws keeps only its old dependencies, so the reads
            // that led here would never re-run it. The failure surfaces from Apply instead.
            error = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            ctx.ExitScope(depth);
        }

        // Same widget back (a builder that mutates and returns one retained widget): nothing to
        // swap, and no reason to dirty ancestors from an off-thread tick.
        if (ReferenceEquals(objA: next, objB: _built) && error is null && _error is null) return;
        _built = next;
        _error = error;

        // The constructor's run is the first build — EnsureStarted applies that one itself. The swap
        // runs untracked: what it measures must not become a dependency of this Watch.
        if (_effect is not null) Reactive.UntrackedInvoke(OnChanged);
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

        _child.Layout(new Offset(x: Bounds.X, y: Bounds.Y));
        MarkNeedsPaint(); // damage is this widget's region only
        return true;
    }

    // Swap in the freshly-built subtree (UI thread only).
    private void Apply(bool inPlace = false)
    {
        Widget? next;
        ExceptionDispatchInfo? error;
        using (Reactive.Hold())
        {
            next = _built;
            error = _error;
        }

        error?.Throw();
        if (ReferenceEquals(objA: next, objB: _child)) return;

        var previous = _child;
        if (previous is not null)
        {
            Rebuilds++; // UI thread only, like the rest of Apply
            if (Debug.WidgetDebug.CountersEnabled) RebuildCount++; // the inspector's R: column
        }

        _child = next;
        CarryState(stash: ref _parked, previous: previous, next: _child);
        SwapChild(
            previous: previous,
            next: _child
        ); // attach-then-detach; see Widget.SwapChild for why that order

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
            ctx.EnterScope(buffer: _scope, count: _scopeDepth);
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
        base.Attach(owner: owner, parent: parent); // attaches the current _child via GetChildren
    }

    public override void Detach()
    {
        _detached = true;
        _effect?.Dispose();
        _effect = null; // Dispose took the graph lock, so no run is in flight past this point
        _built = null;
        _error = null;
        base.Detach(); // detaches _child via GetChildren
        CarryState(stash: ref _parked, previous: _child, next: null);
        _child = null;
        _started = false;
        _dirty = false;
        _measuredOnce = false; // a re-attached Watch must not lay out against stale constraints
        // Nor build against a stale scope — and holding the captured ancestors would keep a detached
        // subtree's providers alive. Array.Clear rather than a fresh array: the buffer is reused.
        Array.Clear(array: _scope, index: 0, length: _scopeDepth);
        _scopeDepth = 0;
    }

    protected override void AdoptStateFrom(Widget previous)
    {
        if (_child is not null)
        {
            base.AdoptStateFrom(previous);
            return;
        }

        // Not built yet (the usual case: a fresh Watch from its parent's rebuild): inherit the old
        // one's subtrees as parked state for the first build to claim.
        var old = (Watch)previous;
        if (old._parked is not null)
        {
            foreach ((var type, var w) in old._parked) (_parked ??= [])[type] = w;
        }

        if (old._child is { } c) (_parked ??= [])[c.GetType()] = c;
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
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        _child?.Layout(origin);
    }

    public override void Paint(PaintList paint) => _child?.Paint(paint);

    // The child's answer, including "nothing": a Watch is a container, and every other container
    // (Align, Stack, the route transitions) lets a miss fall through to whatever is underneath.
    // This one used to answer `this` on a miss — so a full-screen Watch overlaying content (a
    // reader's chrome bar over the page) silently ate every click and wheel event beneath it, and
    // being non-focusable, dropped keyboard focus with them.
    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _child?.HitTest(point);
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_child);

    public override int DebugStateHash() => _child?.DebugStateHash() ?? 0;
}
