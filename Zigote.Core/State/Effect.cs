namespace Zigote.Core.State;

/// <summary>Which thread an effect's body is allowed to run on — see <see cref="Effect" />.</summary>
public enum EffectAffinity
{
    /// <summary>
    ///     Run at drain time on whichever thread wrote the signal. Cheapest, and the right default for a
    ///     body that only touches reactive state.
    /// </summary>
    Inline = 0,

    /// <summary>
    ///     A write only <em>marks</em> this effect; the body runs when the host calls
    ///     <see cref="Reactive.DrainDeferred" /> — which <c>Zigote.UI.Host.App</c> does once per frame, on
    ///     the UI thread, before the measure/layout pass. A host that never calls it never runs these
    ///     bodies, so a non-UI host driving its own loop must call it too. Use it for any body
    ///     that touches the UI, blocks, or takes another lock: an inline body reached from a background
    ///     write runs on that background thread while holding the graph lock — both a priority inversion
    ///     (an audio/network thread doing UI work) and the one shape that can deadlock, since it can block
    ///     on a lock whose owner is itself waiting for the graph lock.
    /// </summary>
    Deferred = 1,
}

/// <summary>
///     A side effect wired into the reactive graph: runs immediately and re-runs whenever a
///     <see cref="Signal{T}" />/<see cref="Computed{T}" /> it read while running changes — the same
///     auto-tracking as <see cref="Computed{T}" />, but for effects instead of a derived value. An
///     effect is a graph <b>root</b> (always watched), so it drives the subscription of the computeds it
///     reads and settles glitch-free — it runs at most once per change cascade.
///     <para>
///         The body returns a cleanup <see cref="Action" /> run before each re-run and on
///         <see cref="Dispose" /> (React-<c>useEffect</c> style) — use it to release the previous run's
///         resources (timer, subscription, …). <see cref="Dispose" /> stops the effect, detaches from
///         all sources, and runs the final cleanup.
///     </para>
///     <para>
///         <b>Which thread the body runs on</b> is <see cref="EffectAffinity" />: <c>Inline</c> (default)
///         is the writer's thread; <c>Deferred</c> is the host's <see cref="Reactive.DrainDeferred" />
///         pass. A body that touches the UI or takes another lock belongs on <c>Deferred</c>.
///     </para>
/// </summary>
public sealed class Effect : Reaction, IDisposable
{
    private static readonly Action Noop = () => { };

    private readonly Func<Action> _body;
    private readonly Delegate _named;
    private readonly int _homeThread;
    private Action _cleanup = Noop;

    /// <summary>Already queued for <see cref="Reactive.DrainDeferred" /> — a second write must not queue it twice.</summary>
    internal bool QueuedDeferred;

    /// <summary>An effect with no cleanup.</summary>
    public Effect(Action body, EffectAffinity affinity = EffectAffinity.Inline) : this(
        () =>
        {
            body();
            return Noop;
        },
        affinity,
        body // name the effect after the caller's body, not the wrapper declared right here
    )
    {
    }

    /// <summary>An effect whose body returns a cleanup thunk (run before each re-run and on dispose).</summary>
    public Effect(Func<Action> body, EffectAffinity affinity = EffectAffinity.Inline) : this(
        body,
        affinity,
        body
    )
    {
    }

    private Effect(Func<Action> body, EffectAffinity affinity, Delegate named)
    {
        _body = body;
        _named = named;
        Affinity = affinity;
        _homeThread = Environment.CurrentManagedThreadId;
        using (Reactive.Hold())
        {
            State = NodeState.Dirty;
            Refresh(); // run now (an effect is always watched, so this subscribes to its sources)
        }
    }

    /// <summary>Which thread this effect's body may run on. Fixed at construction.</summary>
    public EffectAffinity Affinity { get; }

    public void Dispose()
    {
        using (Reactive.Hold())
        {
            if (Disposed) return;
            Disposed = true;
            DetachFromSources();
            _cleanup();
            _cleanup = Noop;
        }
    }

    internal override bool IsWatched => true;

    private protected override void OnScheduled()
    {
        Reactive.ScheduleEffect(this);
    }

    private protected override string DescribeBody()
    {
        return Reactive.Describe(_named);
    }

    private protected override void BeforeExecute()
    {
        // Untracked teardown of the previous run before the new one tracks.
        _cleanup();
        _cleanup = Noop;
    }

    private protected override void Execute()
    {
#if DEBUG
        // An Inline body reached from another thread's write runs on that thread, holding the graph lock.
        // Cheap is fine; slow stalls every other thread — and a blocking one deadlocks the graph.
        if (Affinity == EffectAffinity.Inline && Environment.CurrentManagedThreadId != _homeThread)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _cleanup = _body() ?? Noop;
            var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 /
                     System.Diagnostics.Stopwatch.Frequency;
            if (us > SlowCrossThreadUs)
                System.Diagnostics.Debug.WriteLine(
                    $"Reactive: Inline effect body ran {us:F0}us on thread " +
                    $"{Environment.CurrentManagedThreadId} (created on {_homeThread}) while holding the " +
                    "graph lock — use EffectAffinity.Deferred for cross-thread or UI-touching work."
                );
            return;
        }
#endif
        _cleanup = _body() ?? Noop;
    }

    /// <summary>Invoked by the batch drain: resolve (Check → recompute-if-dirty) and re-run the body if dirty.</summary>
    internal void RunFromQueue()
    {
        Refresh();
    }

#if DEBUG
    private const double SlowCrossThreadUs = 1000;
#endif
}