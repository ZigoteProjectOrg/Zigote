using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

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

    /// <summary>
    ///     A <em>transitive</em> source may have changed — must resolve sources before trusting the
    ///     value.
    /// </summary>
    Check = 1,

    /// <summary>A <em>direct</em> source changed — must recompute.</summary>
    Dirty = 2,
}

/// <summary>
///     A node that can be depended upon — a <see cref="Signal{T}" />, or (through
///     <see cref="Reaction" />)
///     a <see cref="Computed{T}" />. Carries the monotonically-increasing <see cref="Version" />
///     bumped
///     whenever its value changes, so an unobserved reader can detect "did any of my sources actually
///     change?" without being subscribed, and owns the observer edges pointing back at it.
///     <para>
///         <b>Observer storage</b> is one inline slot plus a growable array rather than a set: the
///         overwhelmingly common shape is zero or one observer (which then costs no allocation at
///         all),
///         and the hot operation is <see cref="MarkObservers" /> on every write — a walk over a packed
///         array, not a hash-set enumeration. Ceiling: add/remove are a linear scan, which is the
///         right
///         trade up to the low hundreds of observers per source; past that, a source with thousands of
///         watchers would want per-edge back-pointers (preact's intrusive node list) instead.
///     </para>
///     <para>
///         Public only because <see cref="Signal{T}" />/<see cref="Reaction" /> (public) derive from
///         it;
///         it has no public members and cannot be subclassed outside this assembly.
///     </para>
/// </summary>
public abstract class Source
{
    /// <summary>Bumped whenever this source's value changes. Read directly by dependent reactions.</summary>
    internal long Version;

    // Invariant: _observer0 == null implies _restCount == 0.
    private Reaction? _observer0;
    private Reaction[]? _rest;
    private int _restCount;

    private protected Source() { }

    internal bool HasObservers => _observer0 != null;

    /// <summary>Register this source as a dependency of the currently-running reaction (if any).</summary>
    internal void Track() => Reactive.EvalContext?.AddSource(this);

    /// <summary>Resolve to a current value (a signal is always current; a computed recomputes if stale).</summary>
    internal abstract void Refresh();

    /// <summary>Gained the first observer (a computed uses this to subscribe to its own sources).</summary>
    private protected virtual void OnObserved() { }

    /// <summary>Lost the last observer (a computed uses this to detach, cascading up the cone).</summary>
    private protected virtual void OnUnobserved() { }

    internal void AddObserver(Reaction r)
    {
        Debug.Assert(
            condition: !IsObserver(r),
            message: "Reactive: observer edge added twice."
        );
        if (_observer0 is null)
        {
            _observer0 = r;
            OnObserved();
            return;
        }

        if (_rest is null) _rest = new Reaction[4];
        else if (_restCount == _rest.Length)
            Array.Resize(array: ref _rest, newSize: _rest.Length * 2);
        _rest[_restCount++] = r;
    }

    internal void RemoveObserver(Reaction r)
    {
        if (_observer0 is null) return;

        if (ReferenceEquals(objA: _observer0, objB: r))
        {
            // Promote the last of the tail into the inline slot (order is not part of the contract).
            if (_restCount > 0)
            {
                _observer0 = _rest![--_restCount];
                _rest[_restCount] = null!;
                return;
            }

            _observer0 = null;
            OnUnobserved();
            return;
        }

        for (int i = 0; i < _restCount; i++)
        {
            if (!ReferenceEquals(objA: _rest![i], objB: r)) continue;
            _rest[i] = _rest[--_restCount];
            _rest[_restCount] = null!;
            return;
        }
    }

    /// <summary>Push staleness to every observer — the hot path of a write.</summary>
    internal void MarkObservers(NodeState state, bool fromSourceWrite = false)
    {
        if (_observer0 is null) return;

        // Snapshot the count: a handler reached from MarkStale may subscribe, and the new observer is
        // by definition already up to date with this change.
        int n = _restCount;
        _observer0.MarkStale(newState: state, fromSourceWrite: fromSourceWrite);
        for (int i = 0; i < n && i < _restCount; i++)
            _rest![i].MarkStale(newState: state, fromSourceWrite: fromSourceWrite);
    }

    /// <summary>
    ///     Commit a change: bump the version and push staleness to observers inside a batch, so a fan-out
    ///     settles each effect once and the whole cascade's effects run after every input is set.
    /// </summary>
    private protected void NotifyWrite()
    {
        Version++;
        Reactive.Bump();
        if (!HasObservers) return;

        Reactive.EnterBatch();
        try
        {
            MarkObservers(state: NodeState.Dirty, fromSourceWrite: true);
        }
        finally
        {
            Reactive.LeaveBatch();
        }
    }

    /// <summary>Drop every observer edge (disposal).</summary>
    private protected void ClearObservers()
    {
        _observer0 = null;
        if (_rest != null) Array.Clear(array: _rest, index: 0, length: _restCount);
        _restCount = 0;
    }

    private bool IsObserver(Reaction r)
    {
        if (ReferenceEquals(objA: _observer0, objB: r)) return true;
        for (int i = 0; i < _restCount; i++)
        {
            if (ReferenceEquals(objA: _rest![i], objB: r))
                return true;
        }

        return false;
    }
}

/// <summary>
///     Base of every derived node — a <see cref="Computed{T}" /> or an <see cref="Effect" />. Holds
///     the
///     ordered dependency list, the tri-colour state, and the shared push (<see cref="MarkStale" />) /
///     pull (<see cref="Refresh" />) machinery. Auto-tracking: whatever sources <see cref="Execute" />
///     reads become dependencies, re-derived every run, so conditional dependencies come and go.
///     <para>
///         <b>Lazy + leak-free:</b> a reaction subscribes to its sources only while it is
///         <em>watched</em>
///         (an effect always is; a computed only while it has observers). An unobserved computed
///         neither
///         recomputes on upstream change nor is retained by its sources — it re-derives lazily on
///         read,
///         detecting staleness by comparing the combined source <see cref="Source.Version" />.
///     </para>
///     <para>
///         Public only because <see cref="Computed{T}" />/<see cref="Effect" /> (public) derive from
///         it;
///         it has no public members and is not meant to be subclassed outside this assembly.
///     </para>
/// </summary>
public abstract class Reaction : Source
{
    /// <summary>
    ///     Bound on self-referential re-runs (a reaction that writes a source it reads). Small on purpose:
    ///     the spin holds the global gate, so a "converging" reaction that needs hundreds of rounds is a
    ///     bug that stalls every other thread — trip early and loudly.
    /// </summary>
    private const int MaxSelfReruns = 100;

    private protected bool Disposed;

    private protected NodeState State;
    private long _depsVersion;
    private bool _dirtiedWhileRunning;
    private bool _diverged;
    private bool _failed;
    private bool _hasRun;

    private string? _label;
    private Source? _lastRead;
    private int _matched;
    private int _readCount;

    // The run in progress: how many leading reads matched _sources positionally, and — once a read
    // diverges — the replacement tail collected into _reads.
    private Source[] _reads = [];

    private int _sourceCount;

    // Ordered dependency list, reconciled positionally (the Reactively algorithm): each run's reads
    // are compared slot-by-slot against the previous run's list, so a run whose dependencies are
    // unchanged does no set bookkeeping at all; only from the first divergence on is the tail
    // unwatched/rewatched.
    private Source[] _sources = [];
    private long _validatedVersion = -1;

    private protected Reaction() { }

    /// <summary>Effects are always watched; a computed is watched only while it has ≥1 observer.</summary>
    internal virtual bool IsWatched => HasObservers;

    /// <summary>Body is executing right now — reading a running <see cref="Computed{T}" /> is a cycle.</summary>
    internal bool IsRunning { get; private set; }

    /// <summary>
    ///     Human-readable name for the diagnostics table — the body's declaring type and method, worked
    ///     out once, on the first run after tracking is switched on. Never on the hot path.
    /// </summary>
    internal string Label => _label ??= DescribeBody();

    /// <summary>Run the body under dependency tracking (compute a value / perform a side effect).</summary>
    private protected abstract void Execute();

    /// <summary>Runs untracked, before <see cref="Execute" /> (effect cleanup).</summary>
    private protected virtual void BeforeExecute() { }

    /// <summary>Invoked the first time this reaction is flagged stale in a cascade (was Clean).</summary>
    private protected abstract void OnScheduled();

    /// <summary>Record <paramref name="source" /> as a dependency of the reaction currently running.</summary>
    internal void AddSource(Source source)
    {
        // Adjacent duplicate — `a.Value + a.Value`, or a diamond whose two branches both read the root.
        // One compare instead of the scans below, which is what the shape actually costs otherwise.
        if (ReferenceEquals(objA: source, objB: _lastRead)) return;
        _lastRead = source;

        // Fast path, and the whole point of the positional reconcile: this run is re-reading the same
        // sources in the same order as the last one, so the next slot matches and the read is O(1). It is
        // checked FIRST because a duplicate read can never match positionally — _matched has already
        // advanced past the slot holding an earlier read of the same source — so the scans below are only
        // needed off this path. (Checking them first made a run over n dependencies O(n^2): measured
        // 1.4us for a 64-dependency computed.)
        if (!_diverged && _matched < _sourceCount &&
            ReferenceEquals(objA: _sources[_matched], objB: source))
        {
            _matched++;
            return;
        }

        // A source read twice in one run yields ONE slot (observer edges are per-source, and the
        // reconcile below assumes no duplicates). Dependency lists are small (typically 1-4), so a
        // linear scan beats hashing here.
        for (int i = 0; i < _matched; i++)
        {
            if (ReferenceEquals(objA: _sources[i], objB: source))
                return;
        }

        for (int i = 0; i < _readCount; i++)
        {
            if (ReferenceEquals(objA: _reads[i], objB: source))
                return;
        }

        _diverged = true;

        if (_readCount == _reads.Length)
            Array.Resize(array: ref _reads, newSize: Math.Max(val1: 4, val2: _reads.Length * 2));
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

        if (IsRunning && fromSourceWrite) _dirtiedWhileRunning = true;

        // _failed: the last run threw, so this node is stale-but-unscheduled — its state never made it
        // back to Clean and a plain "already >= newState" bail would leave it deaf to every future change.
        if (State >= newState && !_failed) return;
        bool wasClean = State == NodeState.Clean || _failed;
        _failed = false;
        if (newState > State) State = newState;
        if (wasClean) OnScheduled();
    }

    /// <summary>
    ///     Resolve to a current value/state, recomputing iff a dependency <em>actually</em> changed
    ///     (glitch-free: a pull refreshes the whole upstream cone before the body runs).
    /// </summary>
    internal override void Refresh()
    {
        if (Disposed || IsRunning) return;

        // Fast-out: already validated at the current global version → nothing anywhere has changed.
        if (_hasRun && _validatedVersion == Reactive.GlobalVersion) return;

        // The loop only iterates more than once when Execute wrote a source it also reads (see MarkStale
        // → _dirtiedWhileRunning): a self-referential reaction re-runs until it stabilises, or the guard
        // trips. A normal reaction runs the body path at most once.
        int spins = 0;
        try
        {
            do
            {
                _dirtiedWhileRunning = false;

                if (!_hasRun)
                    Update();
                else if (State == NodeState.Dirty)
                    Update();
                else if (State == NodeState.Check)
                {
                    // Watched & maybe-dirty: resolve sources; a changed one pushes us to Dirty (see Computed.Execute).
                    for (int i = 0; i < _sourceCount; i++)
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
                    for (int i = 0; i < _sourceCount; i++)
                    {
                        var s = _sources[i];
                        s.Refresh();
                        sum += s.Version;
                    }

                    if (sum != _depsVersion) Update();
                }

                if (Disposed)
                    return; // Execute may have disposed us (Update already bailed before reconciling)

                if (++spins > MaxSelfReruns)
                {
                    throw new InvalidOperationException(
                        "Reactive: a reaction re-dirtied itself without converging — it writes a source it reads."
                    );
                }
            } while (_dirtiedWhileRunning);
        }
        catch
        {
            // Left stale: remember it, so the next change reschedules this node instead of assuming the
            // already-Dirty state means "a run is coming". One throw must not silence a reaction forever.
            _failed = true;
            throw;
        }

        _failed = false;
        State = NodeState.Clean;
        _validatedVersion = Reactive.GlobalVersion;
    }

    /// <summary>Overridden by <see cref="Computed{T}" />/<see cref="Effect" /> to name their body.</summary>
    private protected virtual string DescribeBody() => GetType().Name;

    private void Update()
    {
        Reactive.Runs++; // diagnostics: one increment per body, under the lock we already hold
        if (Reactive.TrackReactions) Reactive.RecordRun(this);
        var prev = Reactive.EvalContext;

        // Marks this thread for the whole run, including the untracked cleanup below: while it is set,
        // every Signal read on this thread takes the gated path, so EvalContext is consulted exactly as
        // before. Deliberately coarser than EvalContext (which goes null across BeforeExecute) — being
        // conservative here only costs a reaction's own reads the old price, and never mis-tracks.
        bool wasInReaction = Reactive.EnterReaction();

        try
        {
            // Cleanup (effects) runs untracked so its reads don't become dependencies.
            Reactive.EvalContext = null;
            BeforeExecute();

            Reactive.EvalContext = this;
            _matched = 0;
            _readCount = 0;
            _diverged = false;
            _lastRead = null;
            IsRunning = true;
            try
            {
                Execute();
            }
            finally
            {
                IsRunning = false;
                Reactive.EvalContext = prev;
            }
        }
        finally
        {
            Reactive.LeaveReaction(wasInReaction);
        }

        // Execute (user code) may have disposed this reaction (or been disposed by a re-entrant callback).
        // Dispose already detached from sources; don't let ReconcileSources re-subscribe a dead node.
        if (Disposed)
        {
            Array.Clear(array: _reads, index: 0, length: _readCount);
            _readCount = 0;
            return;
        }

        ReconcileSources();

        long sum = 0;
        for (int i = 0; i < _sourceCount; i++) sum += _sources[i].Version;
        _depsVersion = sum;
        _hasRun = true;
    }

    private void ReconcileSources()
    {
        // Only (un)wire observer edges while watched — an unobserved reaction leaves no trace on its
        // sources (leak-free), it just records what it read for a future connect.
        bool watched = IsWatched;

        // Drop the stale tail: everything past the matched prefix was not re-read this run.
        int keep = _matched;
        for (int i = keep; i < _sourceCount; i++)
        {
            if (watched) _sources[i].RemoveObserver(this);
            _sources[i] = null!;
        }

        _sourceCount = keep;
        if (!_diverged) return;

        // Splice in this run's divergent tail and wire its edges.
        int count = keep + _readCount;
        if (_sources.Length < count)
        {
            Array.Resize(
                array: ref _sources,
                newSize: Math.Max(val1: count, val2: _sources.Length * 2)
            );
        }

        Array.Copy(
            sourceArray: _reads,
            sourceIndex: 0,
            destinationArray: _sources,
            destinationIndex: keep,
            length: _readCount
        );
        Array.Clear(array: _reads, index: 0, length: _readCount);
        _sourceCount = count;
        _readCount = 0;
        if (!watched) return;
        for (int i = keep; i < count; i++) _sources[i].AddObserver(this);
    }

    /// <summary>
    ///     Became watched: subscribe to every current source (recomputing only if the value is
    ///     stale).
    /// </summary>
    private protected void Connect()
    {
        if (_hasRun && _validatedVersion == Reactive.GlobalVersion)
        {
            // Value is current (nothing changed since we last computed) — just wire the observer edges
            // to the already-recorded sources; each source computed connects recursively down the cone.
            for (int i = 0; i < _sourceCount; i++) _sources[i].AddObserver(this);
            State = NodeState.Clean;
        }
        else
        {
            // Stale (or never run): drop the recorded sources so ReconcileSources treats all as new and
            // recompute under `watched` to wire the edges.
            Array.Clear(array: _sources, index: 0, length: _sourceCount);
            _sourceCount = 0;
            State = NodeState.Dirty;
            Refresh();
        }
    }

    /// <summary>
    ///     No longer watched (or disposed): unsubscribe from all sources; cascades to source
    ///     computeds.
    /// </summary>
    private protected void DetachFromSources()
    {
        for (int i = 0; i < _sourceCount; i++) _sources[i].RemoveObserver(this);

        // Keep the recorded list while merely unwatched: a re-connect that is provably current
        // (validated at the current global version) rewires these edges without a recompute, and a
        // remove/re-add of the same source inside one reconcile round-trips losslessly. Only a real
        // Dispose (flag already set by the caller) drops the references for good.
        if (Disposed)
        {
            Array.Clear(array: _sources, index: 0, length: _sourceCount);
            _sourceCount = 0;
        }

        State = NodeState.Dirty; // a later unobserved read recomputes fresh
    }
}

/// <summary>
///     The reactive runtime: a single re-entrant <b>graph lock</b>, the global version counter, and
///     the
///     coalescing effect batch.
///     <para>
///         The lock makes signal writes safe from ANY thread (a timer/async can set a signal) — every
///         graph mutation runs under it, so the current-reaction pointer (thread-static) is only ever
///         touched by the one thread holding the lock. It is re-entrant (a recompute reads more
///         signals
///         under the same lock) and uncontended in single-threaded UI use, so the cost is negligible.
///         It
///         does NOT cover consumer widget/UI mutation — a UI host marshals that to its own thread.
///     </para>
/// </summary>
public static class Reactive
{
    /// <summary>
    ///     Guards against a dependency cycle (an effect that keeps dirtying a source it reads).
    ///     Deliberately
    ///     small: a spin holds the global gate, stalling every other thread, and nothing legitimate needs
    ///     more than a handful of rounds — this is a tripwire for a programming error, not a budget.
    /// </summary>
    private const int MaxDrain = 100;

    internal static readonly object Gate = new();

    /// <summary>
    ///     How long a thread waits for the graph lock before giving up with a
    ///     <see cref="ReactiveDeadlockException" /> instead of blocking forever. The gate is held across
    ///     user code (compute bodies, effect bodies, <c>Changed</c> handlers), so a body that blocks on
    ///     another thread — one which is itself waiting for the gate — is a true deadlock; this turns that
    ///     from a frozen process into a stack trace naming both threads.
    ///     <para>
    ///         <see cref="Timeout.Infinite" /> (the default) waits forever, which is what a plain
    ///         <c>lock</c> does and what the hot path wants — bounded acquisition costs measurably on
    ///         every read. Set a value while chasing a hang, or in a debug host.
    ///     </para>
    /// </summary>
    public static int LockTimeoutMs = Timeout.Infinite;

    /// <summary>Managed id of the thread that most recently took the gate (diagnostics only).</summary>
    private static int _holderThread;

    /// <summary>
    ///     Is THIS thread inside a reaction body? Thread-static on purpose, unlike
    ///     <see cref="EvalContext" />: it is what lets an untracked <see cref="Signal{T}.Value" /> read
    ///     skip the gate entirely (see the seqlock fast path there). A reader that is not in a reaction
    ///     has no dependency to register, so the gate would be protecting nothing — and one process-wide
    ///     lock word is exactly what made concurrent reads collapse.
    ///     <para>
    ///         The TLS cost that rules <see cref="EvalContext" /> out does not apply here: this is written
    ///         once per reaction <em>run</em>, not once per read, and read only on the path that is
    ///         skipping a lock acquisition in exchange.
    ///     </para>
    /// </summary>
    [ThreadStatic] private static bool _inReaction;

    private static Reaction? _evalContext;

    /// <summary>Bumped on every signal write, so an unobserved computed can fast-out when nothing changed.</summary>
    internal static long GlobalVersion;

    /// <summary>
    ///     Attribute every reaction run to its body's <c>Type.Method</c>, so
    ///     <see cref="HottestReactions" /> can answer <em>which</em> computed or effect is churning —
    ///     <see cref="Runs" /> only says that something is. Off by default; devtools turns it on with
    ///     its Reactive panel.
    ///     <para>
    ///         Opt-in because it is the one diagnostic with a real cost: a dictionary lookup per body
    ///         run, and a reflected name per reaction the first time it runs. Off, it is one predictable
    ///         branch.
    ///     </para>
    /// </summary>
    public static bool TrackReactions;

    // ponytail: aggregated by call site, not per instance — "which code churns" is the question people
    // actually ask, and it needs no live-node registry (no weak refs, no lifetime bookkeeping). Per
    // instance would mean keeping every reaction discoverable for as long as it lives.
    private static readonly Dictionary<string, long> RunsByLabel = new(StringComparer.Ordinal);

    /// <summary>
    ///     Handler for an exception thrown by an effect body (or a computed recompute reached from one)
    ///     during the batch drain. When set, the drain isolates the failure — it reports it here and keeps
    ///     running the remaining effects, so one bad reaction can't drop its siblings or crash the thread
    ///     that wrote the signal (e.g. a background timer). When null, the first exception is rethrown to
    ///     the writer after every effect has still had a chance to run. Hosts should set this to log.
    /// </summary>
    public static Action<Exception>? OnError;

    // Graph-wide scheduling state. Like EvalContext these are lock-owned, not thread-owned: a batch can
    // only be open while its opener holds the gate, so one shared depth and one shared drain list are
    // exactly equivalent to per-thread copies — minus the TLS cost and the per-thread List allocation.
    private static int _batchDepth;
    private static readonly List<Effect> Effects = [];
    private static readonly List<Effect> Deferred = [];

    // Lock-free mirror of Deferred.Count, so a host's per-frame idle gate can ask "is there parked
    // cross-thread work?" without taking the graph gate on every frame it would otherwise sleep
    // through. Written under the lock, read from anywhere.
    private static int _deferredCount;

    /// <inheritdoc cref="_inReaction" />
    internal static bool InReaction => InReaction;

    /// <summary>
    ///     The reaction currently running (its reads become dependencies). Plain static, NOT
    ///     thread-static:
    ///     every access happens under <see cref="Gate" />, so mutual exclusion already gives it a single
    ///     owner — per-thread storage would only add a TLS indirection to the hottest read in the graph
    ///     (measured: ~3.5x on a tracked <c>Value</c> read). <see cref="AssertLocked" /> pins the
    ///     invariant
    ///     in DEBUG.
    /// </summary>
    internal static Reaction? EvalContext
    {
        get
        {
            AssertLocked();
            return _evalContext;
        }
        set
        {
            AssertLocked();
            _evalContext = value;
        }
    }

    /// <summary>
    ///     Writes committed to the graph since start — every <see cref="Signal{T}" /> set that passed its
    ///     equality check, plus every <see cref="Trigger.Fire" />. Diagnostics only.
    /// </summary>
    public static long Writes => GlobalVersion;

    /// <summary>
    ///     Reaction bodies executed since start: every <see cref="Computed{T}" /> recompute and every
    ///     <see cref="Effect" /> run, including the computed behind each <c>Watch</c>.
    ///     <para>
    ///         The rebuild counter, graph-wide. Read the <em>per-second delta</em>, not the absolute: a
    ///         number that climbs while the screen is idle means something is re-deriving for nothing —
    ///         a value-type signal without <see cref="IEquatable{T}" />, a computed returning a fresh
    ///         collection every run, or an effect writing a signal it reads.
    ///     </para>
    /// </summary>
    public static long Runs { get; internal set; }

    /// <summary>
    ///     How many <see cref="EffectAffinity.Deferred" /> effects the last <see cref="DrainDeferred" />
    ///     found parked — the cross-thread backlog, per frame. Diagnostics only.
    /// </summary>
    public static int PendingDeferred { get; private set; }

    /// <summary>
    ///     Whether any <see cref="EffectAffinity.Deferred" /> effect is parked, waiting for the host's
    ///     next <see cref="DrainDeferred" />. A frame loop that idles when nothing is dirty must treat
    ///     this as work — otherwise a background write parks an effect and the loop sleeps through it.
    ///     Lock-free, safe from any thread.
    /// </summary>
    public static bool HasPendingDeferred => Volatile.Read(ref _deferredCount) > 0;

    /// <summary>Set for the duration of a reaction body; restores the previous value (bodies nest).</summary>
    internal static bool EnterReaction()
    {
        bool previous = _inReaction;
        _inReaction = true;
        return previous;
    }

    /// <inheritdoc cref="EnterReaction" />
    internal static void LeaveReaction(bool previous) => _inReaction = previous;

    internal static void RecordRun(Reaction r)
    {
        AssertLocked();
        string label = r.Label;
        RunsByLabel.TryGetValue(key: label, value: out long n);
        RunsByLabel[label] = n + 1;
    }

    /// <summary>
    ///     The busiest reaction bodies since <see cref="TrackReactions" /> was switched on (or the last
    ///     <see cref="ResetReactionStats" />), most runs first. Allocates — call it on a diagnostics
    ///     cadence, not per frame.
    /// </summary>
    public static (string Label, long Runs)[] HottestReactions(int top = 10)
    {
        using (Hold())
        {
            var all = new (string Label, long Runs)[RunsByLabel.Count];
            int i = 0;
            foreach ((string label, long runs) in RunsByLabel) all[i++] = (label, runs);
            Array.Sort(array: all, comparison: static (a, b) => b.Runs.CompareTo(a.Runs));
            return all.Length <= top ? all : all[..top];
        }
    }

    /// <summary>Clear the per-body counts — "what churns while I do <em>this</em>".</summary>
    public static void ResetReactionStats()
    {
        using (Hold()) RunsByLabel.Clear();
    }

    /// <summary>
    ///     Name a reaction body by the method that wrote it. Lambdas compile to
    ///     <c>&lt;Build&gt;b__3_0</c> inside a <c>&lt;&gt;c</c> display class, so both get unwrapped back
    ///     to the enclosing type and method — <c>SettingsPage.Build</c>, not
    ///     <c>&lt;&gt;c.&lt;Build&gt;b__3_0</c>.
    /// </summary>
    internal static string Describe(Delegate body)
    {
        try
        {
            var method = body.Method;
            var owner = method.DeclaringType;

            // Walk out of the compiler-generated closure/display class to the type that declares it.
            while (owner is { Name: ['<', ..] } && owner.DeclaringType is { } outer) owner = outer;

            return $"{owner?.Name ?? "?"}.{Unwrap(method.Name)}";
        }
        catch
        {
            // Reflection over a delegate is not guaranteed everywhere (trimmed/AOT hosts). A diagnostic
            // must never be the thing that takes the app down.
            return body.GetType().Name;
        }
    }

    private static string Unwrap(string methodName)
    {
        if (methodName.Length == 0 || methodName[0] != '<') return methodName;
        int end = methodName.IndexOf('>');
        return end > 1 ? methodName[1..end] : methodName;
    }

    /// <summary>
    ///     In DEBUG, pin the "graph state is only touched under the gate" invariant the fields rely
    ///     on.
    /// </summary>
    [Conditional("DEBUG")]
    internal static void AssertLocked()
    {
        Debug.Assert(
            condition: Monitor.IsEntered(Gate),
            message: "Reactive graph state touched without holding Reactive.Gate."
        );
    }

    /// <summary>
    ///     In DEBUG, catch the silent zero-alloc killer: <see cref="EqualityComparer{T}.Default" /> for a
    ///     value type that does not implement <see cref="IEquatable{T}" /> falls back to
    ///     <see cref="object.Equals(object)" />, which boxes both operands on every write/recompute. Enums
    ///     are exempt — the runtime has a non-boxing comparer for them.
    /// </summary>
    [Conditional("DEBUG")]
    internal static void AssertUnboxedEquality<T>(IEqualityComparer<T>? custom)
    {
        if (custom != null) return;
        var t = typeof(T);
        Debug.Assert(
            condition: !t.IsValueType || t.IsEnum || Nullable.GetUnderlyingType(t) != null ||
                       typeof(IEquatable<T>).IsAssignableFrom(t),
            message:
            $"Signal<{t.Name}>: value type without IEquatable<{t.Name}> — equality boxes twice per write. " +
            "Implement IEquatable<T> or pass an explicit comparer."
        );
    }

    /// <summary>
    ///     Take the graph lock, bounded by <see cref="LockTimeoutMs" />. Re-entrant, like <c>lock</c>:
    ///     a thread that already holds it acquires immediately.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Held Hold()
    {
        // Uncontended (and re-entrant) acquisition takes the same fast path as `lock`. The timed wait is
        // only reached when somebody else already holds the gate — where its cost is noise next to the
        // waiting itself. Doing the timed form unconditionally costs ~2x on every read: the timeout
        // overload never gets the JIT's inlined fast path.
        if (Monitor.TryEnter(Gate))
        {
            RecordHolder();
            return default;
        }

        WaitForGate();
        return default;
    }

    /// <summary>
    ///     DEBUG-only: remember who holds the gate, so a timeout can name them. Free in RELEASE — the
    ///     thread-id read is a TLS access, and this sits on the hottest path in the graph.
    /// </summary>
    [Conditional("DEBUG")]
    private static void RecordHolder() => _holderThread = Environment.CurrentManagedThreadId;

    private static void WaitForGate()
    {
        int holder =
            _holderThread; // sampled before the wait: who we are about to blame (DEBUG only)
        if (LockTimeoutMs < 0)
            Monitor.Enter(Gate);
        else if (!Monitor.TryEnter(obj: Gate, millisecondsTimeout: LockTimeoutMs))
        {
            throw new ReactiveDeadlockException(
                $"Reactive: thread {Environment.CurrentManagedThreadId} waited {LockTimeoutMs} ms for the " +
                $"graph lock{(holder == 0 ? "" : $" held by thread {holder}")}. A reaction body or change " +
                "handler is blocking while holding it — move blocking or UI work to an " +
                "EffectAffinity.Deferred effect."
            );
        }

        RecordHolder();
    }

    internal static void Bump() => GlobalVersion++;

    internal static void ScheduleEffect(Effect e)
    {
        AssertLocked();
        if (e.Affinity == EffectAffinity.Deferred)
        {
            // Cross-thread safety valve: don't run this body on whoever wrote the signal (an audio or
            // network thread) — park it for the host's DrainDeferred() at frame start.
            if (!e.QueuedDeferred)
            {
                e.QueuedDeferred = true;
                Deferred.Add(e);
                Volatile.Write(location: ref _deferredCount, value: Deferred.Count);
            }

            return;
        }

        Effects.Add(e);
    }

    /// <summary>
    ///     Run the effects parked by <see cref="EffectAffinity.Deferred" /> since the last call. The host
    ///     calls this once per frame on its own thread; a background writer only marks them. Failures are
    ///     isolated exactly like the inline drain (see <see cref="OnError" />).
    ///     <para>
    ///         <b>The sanctioned cross-thread pattern</b> (audio, network, asset IO): the background
    ///         thread
    ///         writes signals and nothing else; every effect that reacts to those signals with real work —
    ///         UI mutation, another lock, IO — is <see cref="EffectAffinity.Deferred" />; the frame loop
    ///         calls this once at frame start. The background thread then holds the graph lock only for
    ///         the
    ///         write itself, and the work lands on the thread that owns it.
    ///     </para>
    /// </summary>
    public static void DrainDeferred()
    {
        using (Hold())
        {
            // Sampled under the lock, before the drain: "how deep was the queue this frame". A poller
            // reading Deferred.Count itself would both race the list and take the gate once a frame.
            PendingDeferred = Deferred.Count;
            if (Deferred.Count == 0) return;

            Exception? firstError = null;
            // Run under a batch so writes made by these bodies coalesce into one inline drain at the end.
            EnterBatch();
            try
            {
                // Index-based: a body may queue further deferred effects, which this same pass picks up.
                for (int i = 0; i < Deferred.Count; i++)
                {
                    var e = Deferred[i];
                    e.QueuedDeferred = false;
                    try
                    {
                        e.RunFromQueue();
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
                Deferred.Clear();
                Volatile.Write(location: ref _deferredCount, value: 0);
                LeaveBatch();
            }

            if (firstError != null)
                ExceptionDispatchInfo.Throw(firstError);
        }
    }

    /// <summary>Enter a batch: effects dirtied inside are deferred until the outermost batch leaves.</summary>
    internal static void EnterBatch()
    {
        AssertLocked();
        _batchDepth++;
    }

    internal static void LeaveBatch()
    {
        AssertLocked();
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
        using (Hold())
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

    /// <inheritdoc cref="Batch(Action)" />
    /// <returns>Whatever <paramref name="action" /> returned.</returns>
    public static T Batch<T>(Func<T> action)
    {
        using (Hold())
        {
            EnterBatch();
            try
            {
                return action();
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
        using (Hold())
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
        using (Hold()) fn();
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
        AssertLocked();
        if (Effects.Count == 0) return;

        int i = 0;
        int round =
            Effects.Count; // end of the current wave; effects queued by effects start the next
        int guard = 0;
        Exception? firstError = null;
        try
        {
            // New effects scheduled while draining are appended and picked up by this same loop.
            while (i < Effects.Count)
            {
                // Count WAVES, not effects: a cycle re-queues forever, but a wide fan-out (one signal
                // with thousands of subscribers) converges in a single wave and must not trip this.
                if (i == round)
                {
                    if (++guard > MaxDrain)
                    {
                        throw new InvalidOperationException(
                            $"Reactive: effects did not converge after {MaxDrain} iterations — a dependency cycle " +
                            "(an effect that writes a signal it reads)?"
                        );
                    }

                    round = Effects.Count;
                }

                // Isolate each effect: a throwing body must not drop the effects queued after it, nor
                // unwind through the (possibly background) thread that wrote the signal. See OnError.
                try
                {
                    Effects[i++].RunFromQueue();
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
            Effects.Clear();
        }

        // No handler installed: surface the first failure to the writer, but only after every effect ran.
        if (firstError != null)
            ExceptionDispatchInfo.Throw(firstError);
    }

    /// <summary>The <c>using</c> scope returned by <see cref="Hold" />; releasing is the gate release.</summary>
    internal readonly struct Held : IDisposable
    {
        public void Dispose() => Monitor.Exit(Gate);
    }
}

/// <summary>
///     A thread gave up waiting for the reactive graph lock (see <see cref="Reactive.LockTimeoutMs" />
///     ).
///     Always a real bug in a reaction body or change handler, never load: the graph lock is held for
///     microseconds unless user code blocks under it.
/// </summary>
public sealed class ReactiveDeadlockException(string message) : Exception(message);

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
        if (source is Source src)
        {
            bool first = true;
            return new Effect(() =>
                {
                    src.Track(); // depend on the source's changes
                    if (first) first = false;
                    else
                    {
                        Reactive.UntrackedInvoke(
                            onChanged
                        ); // callback reads must not extend the subscription
                    }
                }
            );
        }

        // Fallback for a foreign ISignal implementation (none in-tree today).
        Reactive.Sync(() => source.Invalidated += onChanged);
        return new ActionDisposable(() => Reactive.Sync(() => source.Invalidated -= onChanged));
    }

    /// <summary>
    ///     Observe several sources at once: <paramref name="onChanged" /> fires (once, coalesced) whenever
    ///     any of them changes. The multi-source form of <see cref="Observe" /> — cf. SignalsDotnet's
    ///     <c>WhenAnyChanged</c>.
    /// </summary>
    public static IDisposable ObserveAny(Action onChanged, params ISignal[] sources)
    {
        bool first = true;
        return new Effect(() =>
            {
                for (int i = 0; i < sources.Length; i++)
                {
                    if (sources[i] is Source src)
                        src.Track();
                }

                if (first) first = false;
                else Reactive.UntrackedInvoke(onChanged);
            }
        );
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
