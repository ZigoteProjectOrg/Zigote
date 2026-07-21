namespace Zigote.Core.State;

/// <summary>
///     A reactive value you can read and observe — implemented by both <see cref="Signal{T}" /> and
///     <see cref="Computed{T}" />, so combinators (map/effect/UI binding) can work over either without
///     caring which concrete kind it is. Extends <see cref="ISignal" /> (the untyped change seam).
/// </summary>
public interface IReadableSignal<out T> : ISignal
{
    T Value { get; }
}

/// <summary>Reaction state in the push-notify/pull-recompute graph (Reactively's tri-colour scheme).</summary>
internal enum NodeState : byte
{
    /// <summary>Value is current; nothing upstream changed.</summary>
    Clean = 0,

    /// <summary>A <em>transitive</em> source may have changed — must resolve sources before trusting the value.</summary>
    Check = 1,

    /// <summary>A <em>direct</em> source changed — must recompute.</summary>
    Dirty = 2
}

/// <summary>
///     A node that can be depended upon (a <see cref="Signal{T}" /> or <see cref="Computed{T}" />).
///     Carries a monotonically-increasing <see cref="Version" /> (bumped whenever its value changes) so
///     an unobserved reader can detect "did any of my sources actually change?" without being subscribed.
/// </summary>
internal interface IReactiveSource
{
    long Version { get; }

    /// <summary>Register this source as a dependency of the currently-running reaction (if any).</summary>
    void Track();

    void AddObserver(Reaction r);
    void RemoveObserver(Reaction r);

    /// <summary>Resolve to a current value (a signal is always current; a computed recomputes if stale).</summary>
    void Refresh();
}

/// <summary>
///     Base of every derived node — a <see cref="Computed{T}" /> or an <see cref="Effect" />. Holds the
///     ordered dependency list, the tri-colour state, and the shared push (<see cref="MarkStale" />) /
///     pull (<see cref="Refresh" />) machinery. Auto-tracking: whatever sources <see cref="Execute" />
///     reads become dependencies, re-derived every run, so conditional dependencies come and go.
///     <para>
///         <b>Lazy + leak-free:</b> a reaction subscribes to its sources only while it is <em>watched</em>
///         (an effect always is; a computed only while it has observers). An unobserved computed neither
///         recomputes on upstream change nor is retained by its sources — it re-derives lazily on read,
///         detecting staleness by comparing the combined source <see cref="IReactiveSource.Version" />.
///     </para>
///     <para>
///         Public only because <see cref="Computed{T}" />/<see cref="Effect" /> (public) derive from it;
///         it has no public members and is not meant to be subclassed outside this assembly.
///     </para>
/// </summary>
public abstract class Reaction
{
    // Ordered dependency list, reconciled positionally (the Reactively algorithm): each run's reads
    // are compared slot-by-slot against the previous run's list, so a run whose dependencies are
    // unchanged does no set bookkeeping at all; only from the first divergence on is the tail
    // unwatched/rewatched.
    private IReactiveSource[] _sources = [];
    private int _sourceCount;

    // The run in progress: how many leading reads matched _sources positionally, and — once a read
    // diverges — the replacement tail collected into _reads.
    private IReactiveSource[] _reads = [];
    private int _readCount;
    private int _matched;
    private bool _diverged;

    private protected NodeState State;
    private protected bool Disposed;
    private bool _running;
    private bool _dirtiedWhileRunning;
    private bool _hasRun;
    private long _depsVersion;
    private long _validatedVersion = -1;

    /// <summary>Bound on self-referential re-runs (a reaction that writes a source it reads).</summary>
    private const int MaxSelfReruns = 10_000;

    private protected Reaction()
    {
    }

    /// <summary>Effects are always watched; a computed is watched only while it has ≥1 observer.</summary>
    internal abstract bool IsWatched { get; }

    /// <summary>Run the body under dependency tracking (compute a value / perform a side effect).</summary>
    private protected abstract void Execute();

    /// <summary>Runs untracked, before <see cref="Execute" /> (effect cleanup).</summary>
    private protected virtual void BeforeExecute()
    {
    }

    /// <summary>Invoked the first time this reaction is flagged stale in a cascade (was Clean).</summary>
    private protected abstract void OnScheduled();

    /// <summary>Record <paramref name="source" /> as a dependency of the reaction currently running.</summary>
    internal void AddSource(IReactiveSource source)
    {
        // A source read twice in one run yields ONE slot (observer edges are per-source, and the
        // reconcile below assumes no duplicates). Dependency lists are small (typically 1-4), so a
        // linear scan beats hashing here.
        for (var i = 0; i < _matched; i++)
            if (ReferenceEquals(_sources[i], source))
                return;
        for (var i = 0; i < _readCount; i++)
            if (ReferenceEquals(_reads[i], source))
                return;

        if (!_diverged)
        {
            if (_matched < _sourceCount && ReferenceEquals(_sources[_matched], source))
            {
                _matched++;
                return;
            }

            _diverged = true;
        }

        if (_readCount == _reads.Length)
            Array.Resize(ref _reads, Math.Max(4, _reads.Length * 2));
        _reads[_readCount++] = source;
    }

    /// <summary>
    ///     A source changed. Raise state; on the first (Clean→stale) transition, propagate/schedule.
    ///     <paramref name="fromSourceWrite" /> is true only for a direct <see cref="Signal{T}" /> write
    ///     (not a computed's derived change) — a signal write that lands while we are mid-Execute means
    ///     the body wrote a signal it also reads (a self-write), so we must re-run rather than silently
    ///     drop it. A computed recomputing during a normal read must NOT trigger this (that is the
    ///     glitch-free resolve — we already see its fresh value).
    /// </summary>
    internal void MarkStale(NodeState newState, bool fromSourceWrite = false)
    {
        if (Disposed) return;

        if (_running && fromSourceWrite) _dirtiedWhileRunning = true;

        if (State >= newState) return;
        var wasClean = State == NodeState.Clean;
        State = newState;
        if (wasClean) OnScheduled();
    }

    /// <summary>
    ///     Resolve to a current value/state, recomputing iff a dependency <em>actually</em> changed
    ///     (glitch-free: a pull refreshes the whole upstream cone before the body runs).
    /// </summary>
    internal void Refresh()
    {
        if (Disposed || _running) return;

        // Fast-out: already validated at the current global version → nothing anywhere has changed.
        if (_hasRun && _validatedVersion == Reactive.GlobalVersion) return;

        // The loop only iterates more than once when Execute wrote a source it also reads (see MarkStale
        // → _dirtiedWhileRunning): a self-referential reaction re-runs until it stabilises, or the guard
        // trips. A normal reaction runs the body path at most once.
        var spins = 0;
        do
        {
            _dirtiedWhileRunning = false;

            if (!_hasRun)
            {
                Update();
            }
            else if (State == NodeState.Dirty)
            {
                Update();
            }
            else if (State == NodeState.Check)
            {
                // Watched & maybe-dirty: resolve sources; a changed one pushes us to Dirty (see Computed.Execute).
                for (var i = 0; i < _sourceCount; i++)
                {
                    _sources[i].Refresh();
                    if (State == NodeState.Dirty) break;
                }

                if (State == NodeState.Dirty) Update();
            }
            else if (!IsWatched)
            {
                // Unobserved: not subscribed, so state is unreliable — verify via the combined source version.
                long sum = 0;
                for (var i = 0; i < _sourceCount; i++)
                {
                    var s = _sources[i];
                    s.Refresh();
                    sum += s.Version;
                }

                if (sum != _depsVersion) Update();
            }

            if (Disposed) return; // Execute may have disposed us (Update already bailed before reconciling)

            if (++spins > MaxSelfReruns)
                throw new InvalidOperationException(
                    "Reactive: a reaction re-dirtied itself without converging — it writes a source it reads.");
        } while (_dirtiedWhileRunning);

        State = NodeState.Clean;
        _validatedVersion = Reactive.GlobalVersion;
    }

    private void Update()
    {
        var prev = Reactive.EvalContext;

        // Cleanup (effects) runs untracked so its reads don't become dependencies.
        Reactive.EvalContext = null;
        BeforeExecute();

        Reactive.EvalContext = this;
        _matched = 0;
        _readCount = 0;
        _diverged = false;
        _running = true;
        try
        {
            Execute();
        }
        finally
        {
            _running = false;
            Reactive.EvalContext = prev;
        }

        // Execute (user code) may have disposed this reaction (or been disposed by a re-entrant callback).
        // Dispose already detached from sources; don't let ReconcileSources re-subscribe a dead node.
        if (Disposed)
        {
            Array.Clear(_reads, 0, _readCount);
            _readCount = 0;
            return;
        }

        ReconcileSources();

        long sum = 0;
        for (var i = 0; i < _sourceCount; i++) sum += _sources[i].Version;
        _depsVersion = sum;
        _hasRun = true;
    }

    private void ReconcileSources()
    {
        // Only (un)wire observer edges while watched — an unobserved reaction leaves no trace on its
        // sources (leak-free), it just records what it read for a future connect.
        var watched = IsWatched;

        // Drop the stale tail: everything past the matched prefix was not re-read this run.
        var keep = _matched;
        for (var i = keep; i < _sourceCount; i++)
        {
            if (watched) _sources[i].RemoveObserver(this);
            _sources[i] = null!;
        }

        _sourceCount = keep;
        if (!_diverged) return;

        // Splice in this run's divergent tail and wire its edges.
        var count = keep + _readCount;
        if (_sources.Length < count)
            Array.Resize(ref _sources, Math.Max(count, _sources.Length * 2));
        Array.Copy(_reads, 0, _sources, keep, _readCount);
        Array.Clear(_reads, 0, _readCount);
        _sourceCount = count;
        _readCount = 0;
        if (!watched) return;
        for (var i = keep; i < count; i++) _sources[i].AddObserver(this);
    }

    /// <summary>Became watched: subscribe to every current source (recomputing only if the value is stale).</summary>
    private protected void Connect()
    {
        if (_hasRun && _validatedVersion == Reactive.GlobalVersion)
        {
            // Value is current (nothing changed since we last computed) — just wire the observer edges
            // to the already-recorded sources; each source computed connects recursively down the cone.
            for (var i = 0; i < _sourceCount; i++) _sources[i].AddObserver(this);
            State = NodeState.Clean;
        }
        else
        {
            // Stale (or never run): drop the recorded sources so ReconcileSources treats all as new and
            // recompute under `watched` to wire the edges.
            Array.Clear(_sources, 0, _sourceCount);
            _sourceCount = 0;
            State = NodeState.Dirty;
            Refresh();
        }
    }

    /// <summary>No longer watched (or disposed): unsubscribe from all sources; cascades to source computeds.</summary>
    private protected void DetachFromSources()
    {
        for (var i = 0; i < _sourceCount; i++) _sources[i].RemoveObserver(this);

        // Keep the recorded list while merely unwatched: a re-connect that is provably current
        // (validated at the current global version) rewires these edges without a recompute, and a
        // remove/re-add of the same source inside one reconcile round-trips losslessly. Only a real
        // Dispose (flag already set by the caller) drops the references for good.
        if (Disposed)
        {
            Array.Clear(_sources, 0, _sourceCount);
            _sourceCount = 0;
        }

        State = NodeState.Dirty; // a later unobserved read recomputes fresh
    }
}

/// <summary>
///     The reactive runtime: a single re-entrant <b>graph lock</b>, the global version counter, and the
///     coalescing effect batch.
///     <para>
///         The lock makes signal writes safe from ANY thread (a timer/async can set a signal) — every
///         graph mutation runs under it, so the current-reaction pointer (thread-static) is only ever
///         touched by the one thread holding the lock. It is re-entrant (a recompute reads more signals
///         under the same lock) and uncontended in single-threaded UI use, so the cost is negligible. It
///         does NOT cover consumer widget/UI mutation — a UI host marshals that to its own thread.
///     </para>
/// </summary>
public static class Reactive
{
    internal static readonly object Gate = new();

    /// <summary>The reaction currently running (its reads become dependencies). Only the lock-holder touches it.</summary>
    [ThreadStatic] internal static Reaction? EvalContext;

    /// <summary>Bumped on every signal write, so an unobserved computed can fast-out when nothing changed.</summary>
    internal static long GlobalVersion;

    /// <summary>
    ///     Handler for an exception thrown by an effect body (or a computed recompute reached from one)
    ///     during the batch drain. When set, the drain isolates the failure — it reports it here and keeps
    ///     running the remaining effects, so one bad reaction can't drop its siblings or crash the thread
    ///     that wrote the signal (e.g. a background timer). When null, the first exception is rethrown to
    ///     the writer after every effect has still had a chance to run. Hosts should set this to log.
    /// </summary>
    public static Action<Exception>? OnError;

    [ThreadStatic] private static int _batchDepth;
    [ThreadStatic] private static List<Effect>? _effects;

    /// <summary>Guards against a dependency cycle (an effect that keeps dirtying a source it reads).</summary>
    private const int MaxDrain = 10_000;

    internal static void Bump()
    {
        GlobalVersion++;
    }

    internal static void ScheduleEffect(Effect e)
    {
        (_effects ??= []).Add(e);
    }

    /// <summary>Enter a batch: effects dirtied inside are deferred until the outermost batch leaves.</summary>
    internal static void EnterBatch()
    {
        _batchDepth++;
    }

    internal static void LeaveBatch()
    {
        if (_batchDepth > 1)
        {
            _batchDepth--;
            return;
        }

        try
        {
            Drain();
        }
        finally
        {
            _batchDepth = 0; // reset even if an effect body threw, so the next write starts clean
        }
    }

    /// <summary>
    ///     Coalesce every signal write inside <paramref name="action" /> into a single downstream effect
    ///     pass (instead of one per write). Nestable; composes with the implicit per-write batch.
    /// </summary>
    public static void Batch(Action action)
    {
        lock (Gate)
        {
            EnterBatch();
            try
            {
                action();
            }
            finally
            {
                LeaveBatch();
            }
        }
    }

    /// <summary>
    ///     Read reactive values inside <paramref name="fn" /> WITHOUT subscribing to them — the reads do
    ///     not become dependencies of the enclosing computed/effect (cf. SolidJS <c>untrack</c>).
    /// </summary>
    public static T Untracked<T>(Func<T> fn)
    {
        lock (Gate)
        {
            var prev = EvalContext;
            EvalContext = null;
            try
            {
                return fn();
            }
            finally
            {
                EvalContext = prev;
            }
        }
    }

    /// <summary>Run <paramref name="fn" /> holding the graph lock (for composed multi-step writes).</summary>
    public static void Sync(Action fn)
    {
        lock (Gate) fn();
    }

    /// <summary>
    ///     Invoke a user-facing event handler (<c>Changed</c>/<c>Invalidated</c>/observe callbacks) with
    ///     dependency tracking suspended: handlers fire while a reaction may be mid-run, and their reads
    ///     must not become phantom dependencies of it — the same reason effect cleanup runs untracked.
    /// </summary>
    internal static void UntrackedInvoke(Action? handler)
    {
        if (handler == null) return;
        var prev = EvalContext;
        EvalContext = null;
        try
        {
            handler();
        }
        finally
        {
            EvalContext = prev;
        }
    }

    /// <inheritdoc cref="UntrackedInvoke(Action?)" />
    internal static void UntrackedInvoke<T>(Action<T>? handler, T value)
    {
        if (handler == null) return;
        var prev = EvalContext;
        EvalContext = null;
        try
        {
            handler(value);
        }
        finally
        {
            EvalContext = prev;
        }
    }

    private static void Drain()
    {
        if (_effects == null) return;

        var i = 0;
        var guard = 0;
        Exception? firstError = null;
        try
        {
            // New effects scheduled while draining are appended and picked up by this same loop.
            while (i < _effects.Count)
            {
                if (++guard > MaxDrain)
                    throw new InvalidOperationException(
                        "Reactive: effects did not converge after 10000 iterations — a dependency cycle " +
                        "(an effect that writes a signal it reads)?");

                // Isolate each effect: a throwing body must not drop the effects queued after it, nor
                // unwind through the (possibly background) thread that wrote the signal. See OnError.
                try
                {
                    _effects[i++].RunFromQueue();
                }
                catch (Exception ex)
                {
                    if (OnError != null) OnError(ex);
                    else firstError ??= ex;
                }
            }
        }
        finally
        {
            _effects.Clear();
        }

        // No handler installed: surface the first failure to the writer, but only after every effect ran.
        if (firstError != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(firstError);
    }
}

/// <summary>Change-observation helpers over the untyped <see cref="ISignal" /> seam.</summary>
public static class ReactiveExtensions
{
    /// <summary>
    ///     Observe change notifications (fires <em>on change</em>, not immediately) and return an
    ///     unsubscribe. Backed by an <see cref="Effect" />, so it makes the source "live" (a lazy
    ///     computed it targets will recompute on upstream change) and settles glitch-free — the callback
    ///     runs at most once per change. Cf. <see cref="Signal{T}.Subscribe" />, which also fires now.
    /// </summary>
    public static IDisposable Observe(this ISignal source, Action onChanged)
    {
        if (source is IReactiveSource src)
        {
            var first = true;
            return new Effect(() =>
            {
                src.Track(); // depend on the source's changes
                if (first) first = false;
                else Reactive.UntrackedInvoke(onChanged); // callback reads must not extend the subscription
            });
        }

        // Fallback for a foreign ISignal implementation (none in-tree today).
        Reactive.Sync(() => source.Invalidated += onChanged);
        return new ActionDisposable(() => Reactive.Sync(() => source.Invalidated -= onChanged));
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }
}
