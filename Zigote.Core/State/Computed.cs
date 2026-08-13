using System.Runtime.ExceptionServices;

namespace Zigote.Core.State;

/// <summary>
///     Derived read-only value that recomputes when a source it read changes.
///     <para>
///         Dependencies are tracked <b>automatically</b>: every <see cref="Signal{T}" /> (or other
///         <see cref="Computed{T}" />) whose value is read while <see cref="_compute" /> runs becomes
///         a
///         dependency, re-derived on every recompute — so conditional dependencies subscribe and
///         unsubscribe as branches change. <see cref="_compute" /> must be side-effect free.
///     </para>
///     <para>
///         <b>Lazy &amp; leak-free:</b> while unobserved, a computed neither recomputes on upstream
///         change nor keeps its sources referencing it — it re-derives on read and detaches on the
///         loss
///         of its last observer. While observed it is glitch-free (a fan-out settles it once) and
///         minimal-recompute (an intermediate whose value is unchanged does not wake its observers).
///         Dispose to detach permanently. An optional comparer controls when the value counts as
///         changed.
///     </para>
///     <para>
///         <b>A throwing compute is cached like a value:</b> the exception is stored and rethrown on
///         every
///         read until a dependency changes, instead of re-running the failing body per read —
///         otherwise a
///         computed that throws inside a paint binding becomes a hidden per-frame recompute. A cycle
///         (a
///         computed read while it is recomputing) throws instead of quietly yielding a stale value.
///     </para>
/// </summary>
public sealed class Computed<T> : Reaction, IReadableSignal<T>, IDisposable
{
    private readonly Func<T> _compute;
    private readonly IEqualityComparer<T> _equals;
    private readonly ISignal[] _forced;
    private readonly Delegate _named; // what to name this computed after — see Reactive.Describe
    private ExceptionDispatchInfo? _error;
    private T _value = default!;

    internal Computed(Func<T> compute, IEqualityComparer<T>? equals, ISignal[] forced,
        Delegate? named = null)
    {
        Reactive.AssertUnboxedEquality(equals);
        _compute = compute;
        _named = named ?? compute;
        _equals = equals ?? EqualityComparer<T>.Default;
        _forced = forced;
        // Eager first compute (unobserved — records deps without subscribing), so `Computed.From` yields
        // a ready value like the previous API. Recomputes are lazy thereafter.
        using (Reactive.Hold())
        {
            State = NodeState.Dirty;
            Refresh(); // a failure here is cached, not thrown: errors are always delivered at read time
        }
    }

    public void Dispose()
    {
        using (Reactive.Hold())
        {
            if (Disposed) return;
            Disposed = true;
            DetachFromSources();
            ClearObservers();
        }
    }

    public T Value
    {
        get
        {
            using (Reactive.Hold())
            {
                Reactive.EvalContext?.AddSource(this);
                Refresh();
                _error?.Throw();
                return _value;
            }
        }
    }

    /// <inheritdoc />
    public event Action? Invalidated;

    /// <summary>
    ///     Raised after a recompute produced a new value — a <b>raw</b> hook fired from inside the
    ///     recompute, not a settled view of the graph: mid-cascade it can see intermediate values that no
    ///     effect ever observes. For a coalesced, glitch-free view use
    ///     <see cref="ReactiveExtensions.Observe" /> or an <see cref="Effect" />.
    /// </summary>
    public event Action<T>? Changed;

    /// <summary>0 → 1 observers: became watched, so subscribe to our own sources.</summary>
    private protected override void OnObserved() => Connect();

    /// <summary>1 → 0 observers: unsubscribe (cascades up the cone to source computeds).</summary>
    private protected override void OnUnobserved() => DetachFromSources();

    /// <summary>
    ///     <see cref="Reaction.Refresh" />, plus the cycle check: a read that re-enters a computed still
    ///     executing its own body is a dependency cycle — fail loudly (cf. preact's "Cycle detected")
    ///     rather than handing back the half-built previous value.
    /// </summary>
    internal override void Refresh()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException(
                "Reactive: cycle detected — a computed was read while it was still computing " +
                "(it depends, directly or through other computeds, on itself)."
            );
        }

        base.Refresh();
    }

    /// <summary>Read the current value WITHOUT subscribing (does not become a dependency of the reader).</summary>
    public T Peek()
    {
        using (Reactive.Hold())
        {
            Refresh();
            _error?.Throw();
            return _value;
        }
    }

    private protected override void OnScheduled()
    {
        // A computed does not schedule itself; it propagates "maybe-dirty" to its own observers.
        MarkObservers(NodeState.Check);
    }

    private protected override string DescribeBody() => Reactive.Describe(_named);

    private protected override void Execute()
    {
        bool hadError = _error != null;
        T next;
        try
        {
            next = _compute();
            _error = null;
        }
        catch (Exception ex)
        {
            // Cache the failure and propagate it as a change: observers go Dirty, re-read, and hit the
            // rethrow — the same shape as a value change, so nothing recomputes until a source moves.
            _error = ExceptionDispatchInfo.Capture(ex);
            TrackForced();
            Version++;
            MarkObservers(NodeState.Dirty);
            Reactive.UntrackedInvoke(Invalidated);
            return;
        }

        TrackForced();

        // Recovering from a cached error counts as a change even if the value matches the pre-error one:
        // observers were told it changed when it threw, so they must be told it is readable again.
        if (!hadError && _equals.Equals(x: _value, y: next)) return;

        _value = next;
        Version++;

        // Our value really changed → observers must recompute (they are already ≥Check from the cascade).
        MarkObservers(NodeState.Dirty);

        // Handlers run inside this computed's own tracked Execute — suspend tracking so their reads
        // don't become phantom dependencies of it.
        Reactive.UntrackedInvoke(handler: Changed, value: next);
        Reactive.UntrackedInvoke(Invalidated);
    }

    /// <summary>Always-subscribed extras: track them even if this run didn't read them (or threw).</summary>
    private void TrackForced()
    {
        for (int i = 0; i < _forced.Length; i++)
        {
            if (_forced[i] is Source src)
                src.Track();
        }
    }
}

public static class Computed
{
    /// <summary>
    ///     Auto-tracked derived value — <paramref name="compute" /> may read any number of
    ///     <see cref="Signal{T}" />s / <see cref="Computed{T}" />s and they are wired up automatically.
    ///     Any <paramref name="forced" /> sources are additionally subscribed even when a run doesn't read
    ///     them. Dispose the result to detach.
    /// </summary>
    public static Computed<T> From<T>(Func<T> compute, params ISignal[] forced) => new(
        compute: compute,
        equals: null,
        forced: forced
    );

    /// <summary>Auto-tracked derived value with a custom equality comparer (controls change propagation).</summary>
    public static Computed<T> From<T>(Func<T> compute, IEqualityComparer<T> equals) =>
        new(compute: compute, equals: equals, forced: []);

    /// <summary>
    ///     Single-source map — value is always <c>map(source.Value)</c>. A convenience wrapper over the
    ///     auto-tracked form (reading <paramref name="source" />'s value wires the dependency).
    /// </summary>
    public static Computed<TResult> From<TSource, TResult>(Signal<TSource> source,
        Func<TSource, TResult> map)
    {
        // Named after `map` — the closure below is declared here, so every mapped computed in the app
        // would otherwise pile into one "Computed.From" row in the diagnostics table.
        return new Computed<TResult>(
            compute: () => map(source.Value),
            equals: null,
            forced: [],
            named: map
        );
    }
}
