using Zigote.Core;
using Zigote.Core.State;
using Zigote.UI.Widgets;

namespace Zigote.UI.Functional;

/// <summary>
///     The one widget that lets a component be a plain function — <c>Widget MyView(…)</c> — instead of
///     a <see cref="ComposedWidget" /> subclass. The function's body is the constructor (runs once;
///     signals created there are the component's retained state, held by the closure), and the
///     <see cref="View" /> it returns is the build:
///     <code>
///   static Widget Counter()
///   {
///       var count = new Signal&lt;int&gt;(0);               // state — created once, survives rebuilds
///       return new View(ctx =&gt; new Row {               // view — re-runs when `count` changes
///           Children = {
///               new Button("+1", () =&gt; count.Value++),
///               new Label($"{count.Value}", 17f, ThemeProvider.Of(ctx).OnSurface),
///           }
///       });
///   }
/// </code>
///     <para>
///         The builder gets both of the things a bare function otherwise cannot reach:
///         <list type="bullet">
///             <item>
///                 <b>Inherited data, dependably.</b> The builder only ever runs inside
///                 <c>Build</c> — inside the measure walk, with the ancestor scope live and
///                 <c>BuildOwner</c> set. <c>ThemeProvider.Of(ctx)</c> therefore returns the real
///                 theme (never the <c>ThemeData.Dark</c> fallback an out-of-walk builder sees),
///                 and <c>DependOn</c> registers this widget, so a theme flip rebuilds the function
///                 with the new tokens. Wrapping the builder in a <see cref="Watch" /> instead
///                 loses exactly that: the Watch first evaluates while being attached, after
///                 <c>ComposedWidget</c> has already restored <c>BuildOwner</c>, so the dependency
///                 is never registered and a theme flip leaves the subtree stale.
///             </item>
///             <item>
///                 <b>Reactivity.</b> The builder is evaluated under a <c>Computed</c>, so any
///                 <c>Signal</c>/<c>Computed</c> it reads schedules a rebuild on change — safely
///                 from any thread. The rebuild lands in the next walk rather than swapping
///                 in place like <see cref="Watch" />: a whole-tree measure instead of Watch's
///                 O(changed) fast path, which is the price of the scope guarantees above. For a
///                 hot inner subtree that rebuilds at animation rate, nest a <see cref="Watch" />
///                 (or write signal values into retained children from an effect) inside the View.
///             </item>
///         </list>
///     </para>
///     <para>
///         Like any rebuild in this retained framework, a rebuild swaps in a fresh subtree. State
///         that must survive lives in the closure — signals, and any stateful child widgets, which
///         belong in the function body rather than inside the lambda (a child created inside the
///         builder is recreated, and reset, by every rebuild; a closed-over one is re-adopted).
///     </para>
/// </summary>
public sealed class View : ComposedWidget
{
    private readonly Func<BuildContext, Widget> _build;

    // Set by OnSignalChanged (any thread) and consumed on the UI thread. Distinct from NeedsBuild
    // because EnsureBuilt clears NeedsBuild AFTER Build returns: a signal written concurrently
    // during the build window would set NeedsBuild only to have it wiped, losing the change. This
    // flag survives that wipe; the Measure override re-raises NeedsBuild from it.
    private volatile bool _changed;
    private Computed<int>? _computed;

    // The subtree the last Build returned — kept so HitTest can delegate without touching
    // ComposedWidget's private child slot.
    private Widget? _root;
    private IDisposable? _subscription;

    public View(Func<BuildContext, Widget> build) => _build = build;

    /// <summary>
    ///     Start something whose lifetime is this widget's mount period — a timer feeding a signal, a
    ///     subscription, an <c>Effect</c>. Runs on attach (before the first build of that mount period,
    ///     so a subscription the build depends on already exists) and again on every re-attach; the
    ///     returned disposable is torn down on unmount.
    ///     <code>
    ///   new View(ctx =&gt; new Label($"{now.Value:HH:mm:ss}")) {
    ///       OnMounted = () =&gt; new Timer(_ =&gt; now.Value = DateTime.Now, null, 0, 1000),
    ///   }
    /// </code>
    /// </summary>
    public Func<IDisposable?>? OnMounted { get; set; }

    protected override void OnMount()
    {
        var resource = OnMounted?.Invoke();
        if (resource is not null) Own(resource);
    }

    protected override Widget Build(BuildContext context)
    {
        // A rebuild re-evaluates from scratch: the old computed's dependency set may not survive
        // (a different branch reads different signals), so subscription and computed are recreated
        // rather than reused. Rebuild-rate, not frame-rate, so the allocation is fine.
        DisposeReactive();
        _changed = false;

        // The builder runs exactly ONCE per rebuild, and only HERE — inside Build, where BuildOwner
        // is set and the walk's ancestor scope is live. That single fact carries the inherited-data
        // guarantees in the class doc. The computed's only job is to remember what that one run
        // read: when a dependency later changes, the Observe effect re-evaluates the body — out of
        // the walk, where the builder must not run — and the memoized `root` short-circuits it to a
        // revision bump. The bump is a value change (never equality-gated away), so the observer
        // fires, and the real rebuild happens in the next walk. Computed.From evaluates eagerly, so
        // `root` is populated before this call returns.
        Widget? root = null;
        int revision = 0;
        _computed = Computed.From(() =>
        {
            root ??= _build(context);
            return ++revision;
        });
        _subscription = _computed.Observe(OnSignalChanged);

        // A change can land between the eager evaluation and the subscription; Observe absorbs it
        // by silently recomputing with the callback suppressed. It cannot hide from the revision:
        // anything past the first evaluation means a rebuild is owed. (_changed, not NeedsBuild —
        // see the field comment; the Measure tail below converts it once EnsureBuilt is done.)
        if (_computed.Value > 1) ScheduleRebuild();
        return _root = root!;
    }

    private void OnSignalChanged()
    {
        if (Mounted) ScheduleRebuild();
    }

    private void ScheduleRebuild()
    {
        // Rebuilds always land in the next walk's EnsureBuilt — never out-of-walk, so there is no
        // scope capture to keep consistent, and one path serves every origin: UI thread, mid-walk,
        // or a background signal write. NeedsBuild directly (cheap, covers the common between-frames
        // case), _changed as the loss-proof backstop, and the App queues the ancestor-chain
        // invalidation for the top of the next frame (a direct MarkNeedsBuild would be unsafe
        // off-thread and futile mid-walk — the unwinding walk resets ancestor flags).
        _changed = true;
        NeedsBuild = true;
        Owner?.InvalidateLayoutFromAnyThread(this);
    }

    public override Size Measure(Constraints constraints)
    {
        var size = base.Measure(constraints); // may run EnsureBuilt, which clears NeedsBuild at its end
        if (_changed && !NeedsBuild)
        {
            // A signal changed while the build above was in flight (or in the observe gap): the
            // flag EnsureBuilt just cleared must come back so the next walk rebuilds. The wake-up
            // and ancestor invalidation are already queued by ScheduleRebuild.
            NeedsBuild = true;
        }

        return size;
    }

    private void DisposeReactive()
    {
        _subscription?.Dispose();
        _subscription = null;
        _computed?.Dispose();
        _computed = null;
    }

    /// <summary>
    ///     Tear down the reactive pair and force the next attach to rebuild: signals hold their
    ///     observers strongly, so a live subscription would keep re-running a detached View, and a
    ///     re-attach must build fresh anyway to resubscribe under the real ancestor scope.
    /// </summary>
    public override void Detach()
    {
        base.Detach();
        DisposeReactive();
        NeedsBuild = true;
    }

    // Hit-transparent on a miss, like Watch and every other container: a full-bleed functional
    // wrapper layered in a Stack must not swallow clicks aimed at content beneath it —
    // ComposedWidget's default answers `this` on a child miss.
    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _root?.HitTest(point);
    }
}
