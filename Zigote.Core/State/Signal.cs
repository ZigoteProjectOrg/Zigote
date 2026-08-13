namespace Zigote.Core.State;

/// <summary>
///     Untyped "something changed" seam. Lets change-observation (<see cref="ReactiveExtensions.Observe" />)
///     and combinators work over a <see cref="Signal{T}" /> or a <see cref="Computed{T}" /> without
///     knowing the value type.
/// </summary>
public interface ISignal
{
    event Action Invalidated;
}

/// <summary>
///     Reactive value container — the "what is true now" primitive. A graph <b>source</b>: reading it
///     inside a <see cref="Computed{T}" />/<see cref="Effect" /> subscribes that reaction; writing it
///     pushes staleness to observers and drains effects once.
///     <para>
///         Fires <see cref="Changed" /> on mutation; <see cref="Subscribe" /> returns a disposable that
///         auto-unsubscribes. Thread-safe: reads/writes run under the shared <see cref="Reactive" />
///         graph lock, so a signal may be set from any thread (a timer/async completion). An optional
///         <see cref="IEqualityComparer{T}" /> controls when a write counts as a change.
///     </para>
/// </summary>
public sealed class Signal<T> : Source, IReadableSignal<T>
{
    private readonly IEqualityComparer<T> _equals;
    private T _value;

    /// <summary>
    ///     Seqlock counter: even means <see cref="_value" /> is stable, odd means a write is in flight.
    ///     Bumped only by <see cref="Write" />, which always runs under the graph gate, so writers never
    ///     race each other here — this exists purely so a reader on another thread can take a coherent
    ///     snapshot WITHOUT the gate. One <c>long</c> per signal, no allocation.
    /// </summary>
    private long _seq;

    public Signal(T initialValue, IEqualityComparer<T>? comparer = null)
    {
        Reactive.AssertUnboxedEquality(comparer);
        _value = initialValue;
        _equals = comparer ?? EqualityComparer<T>.Default;
    }

    public T Value
    {
        get
        {
            // Not inside a reaction → there is no dependency to register, and a single signal read has
            // no cross-node invariant to uphold (that is what Reactive.Sync is for). The gate would be
            // protecting nothing, so skip it: one process-wide lock word is what made concurrent reads
            // collapse (measured: 6.6ns at one thread, 47.8ns at sixteen).
            if (!Reactive.InReaction) return Snapshot();

            using (Reactive.Hold())
            {
                Reactive.EvalContext?.AddSource(this);
                return _value;
            }
        }
        set
        {
            using (Reactive.Hold())
            {
                if (_equals.Equals(_value, value)) return;
                Write(value);
            }
        }
    }

    /// <inheritdoc />
    public event Action? Invalidated;

    /// <summary>
    ///     Raised after every committed write, with that write's value — a <b>raw write hook</b>, not a
    ///     glitch-free view: inside a <see cref="Reactive.Batch(Action)" /> it fires once per write, so it
    ///     sees intermediate states that no effect ever observes. For a consistent, coalesced view (fires
    ///     at most once per cascade, after everything has settled) use
    ///     <see cref="ReactiveExtensions.Observe" /> or an <see cref="Effect" />.
    /// </summary>
    public event Action<T>? Changed;

    internal override void Refresh()
    {
        // A signal is always current.
    }

    /// <summary>Read the current value WITHOUT subscribing (does not become a dependency of the reader).</summary>
    public T Peek()
    {
        if (!Reactive.InReaction) return Snapshot();

        using (Reactive.Hold())
        {
            return _value;
        }
    }

    /// <summary>
    ///     Lock-free coherent read. Retries while a write is in flight, and rejects any snapshot a write
    ///     straddled — so a value wider than a machine word (a 16-byte struct) can never be observed torn,
    ///     which is the guarantee the gate used to provide on this path.
    ///     <para>
    ///         Only valid when this thread is NOT inside a reaction: it registers no dependency. The two
    ///         volatile reads of <see cref="_seq" /> are also the acquire fences that stop the JIT hoisting
    ///         <see cref="_value" /> out of a caller's spin loop.
    ///     </para>
    /// </summary>
    private T Snapshot()
    {
        while (true)
        {
            var before = Volatile.Read(ref _seq);
            if ((before & 1) == 0)
            {
                var snapshot = _value;
                if (Volatile.Read(ref _seq) == before) return snapshot;
            }

            Thread.SpinWait(1); // a writer holds the gate; it will be brief
        }
    }

    /// <summary>Set the value and notify unconditionally (skips the equality check).</summary>
    public void Set(T value)
    {
        using (Reactive.Hold())
        {
            Write(value);
        }
    }

    /// <summary>Read-modify-write: store <c>update(current)</c> (equality-gated like the setter).</summary>
    public void Update(Func<T, T> update)
    {
        using (Reactive.Hold())
        {
            var next = update(_value);
            if (_equals.Equals(_value, next)) return;
            Write(next);
        }
    }

    /// <summary>
    ///     Subscribe and immediately invoke <paramref name="listener" /> with the current value.
    ///     <para>
    ///         The value is snapshotted under the graph lock but the initial invoke runs <b>after</b> it is
    ///         released — one less piece of user code under the global lock, so a listener that blocks or
    ///         takes another lock cannot stall (or deadlock) the graph. The trade: with a concurrent writer
    ///         the listener can see the newer value from <see cref="Changed" /> before this initial one.
    ///         Single-threaded UI use is unaffected.
    ///     </para>
    /// </summary>
    public IDisposable Subscribe(Action<T> listener)
    {
        T snapshot;
        using (Reactive.Hold())
        {
            Changed += listener;
            snapshot = _value;
        }

        try
        {
            // Nested inside a reaction's run, this thread still holds the gate — suspend tracking there,
            // so the listener's reads don't become dependencies of whatever is running.
            if (Monitor.IsEntered(Reactive.Gate)) Reactive.UntrackedInvoke(listener, snapshot);
            else listener(snapshot);
        }
        catch
        {
            Changed -= listener;
            throw;
        }

        return new Unsubscriber(() => Changed -= listener);
    }

    public static implicit operator T(Signal<T> s)
    {
        return s.Value;
    }

    public override string ToString()
    {
        using (Reactive.Hold())
        {
            return _value?.ToString() ?? "";
        }
    }

    // Commit a new value and cascade — always called under the lock.
    private void Write(T value)
    {
        // Seqlock publish, so a gate-free reader (see Snapshot) either misses this write entirely or
        // sees all of it. Interlocked for the opening bump rather than Volatile.Write: a release store
        // stops earlier work drifting later, but NOT the `_value` store below drifting earlier — which
        // on a weakly-ordered target (arm64: the mobile players) is exactly the reordering that would
        // let a reader observe a torn value under an even counter. x64 would be fine either way.
        Interlocked.Increment(ref _seq); // now odd: write in flight
        _value = value;
        Volatile.Write(ref _seq, _seq + 1); // now even: publish, with _value ordered before it

        // Cascade BEFORE firing Changed/Invalidated: a user handler is allowed to re-enter and
        // subscribe/dispose an observer of this same signal, which would otherwise mutate the observer
        // list in the middle of the walk.
        NotifyWrite();

        // A write can land mid-run (a self-writing reaction) — suspend tracking so handler reads
        // don't become phantom dependencies of whatever reaction is executing.
        Reactive.UntrackedInvoke(Changed, value);
        Reactive.UntrackedInvoke(Invalidated);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }
}

/// <summary>
///     A valueless source: "this happened". Reactions that <see cref="Depend" /> on it re-run on every
///     <see cref="Fire" />, with no value to compare — the escape hatch for recomputing on an event
///     (a reload, a tick, a device change) rather than on a state change. Cf. SignalsDotnet's signal
///     events. Everything a <see cref="Signal{T}" /> gives you applies: tracking, batching, glitch-free
///     settling, and it may be fired from any thread.
///     <code>
///     var reload = new Trigger();
///     var rows = Computed.From(() => { reload.Depend(); return LoadRows(); });
///     reload.Fire();   // rows recomputes
///     </code>
/// </summary>
public sealed class Trigger : Source, ISignal
{
    /// <inheritdoc />
    public event Action? Invalidated;

    /// <summary>The running computed/effect re-runs on the next <see cref="Fire" />.</summary>
    public void Depend()
    {
        using (Reactive.Hold())
        {
            Track();
        }
    }

    /// <summary>Raise the event: every reaction that depends on this trigger becomes stale.</summary>
    public void Fire()
    {
        using (Reactive.Hold())
        {
            NotifyWrite();
            Reactive.UntrackedInvoke(Invalidated);
        }
    }

    internal override void Refresh()
    {
        // Nothing to resolve — a trigger has no value.
    }
}
