namespace Zigote.Core.State;

/// <summary>
///     Derived read-only value that recomputes when a source it read changes.
///     <para>
///         Dependencies are tracked <b>automatically</b>: every <see cref="Signal{T}" /> (or other
///         <see cref="Computed{T}" />) whose value is read while <see cref="_compute" /> runs becomes a
///         dependency, re-derived on every recompute — so conditional dependencies subscribe and
///         unsubscribe as branches change. <see cref="_compute" /> must be side-effect free.
///     </para>
///     <para>
///         <b>Lazy &amp; leak-free:</b> while unobserved, a computed neither recomputes on upstream
///         change nor keeps its sources referencing it — it re-derives on read and detaches on the loss
///         of its last observer. While observed it is glitch-free (a fan-out settles it once) and
///         minimal-recompute (an intermediate whose value is unchanged does not wake its observers).
///         Dispose to detach permanently. An optional comparer controls when the value counts as changed.
///     </para>
/// </summary>
public sealed class Computed<T> : Reaction, IReadableSignal<T>, IReactiveSource, IDisposable
{
    private readonly Func<T> _compute;
    private readonly IEqualityComparer<T> _equals;
    private readonly ISignal[] _forced;
    private HashSet<Reaction>? _observers;
    private T _value = default!;
    private long _version;

    internal Computed(Func<T> compute, IEqualityComparer<T>? equals, ISignal[] forced)
    {
        _compute = compute;
        _equals = equals ?? EqualityComparer<T>.Default;
        _forced = forced;
        // Eager first compute (unobserved — records deps without subscribing), so `Computed.From` yields
        // a ready value like the previous API. Recomputes are lazy thereafter.
        lock (Reactive.Gate)
        {
            State = NodeState.Dirty;
            Refresh();
        }
    }

    public T Value
    {
        get
        {
            lock (Reactive.Gate)
            {
                Reactive.EvalContext?.AddSource(this);
                Refresh();
                return _value;
            }
        }
    }

    /// <inheritdoc />
    public event Action? Invalidated;

    public event Action<T>? Changed;

    internal override bool IsWatched => _observers is { Count: > 0 };

    long IReactiveSource.Version => _version;

    void IReactiveSource.Track()
    {
        Reactive.EvalContext?.AddSource(this);
    }

    void IReactiveSource.AddObserver(Reaction r)
    {
        var was = _observers is { Count: > 0 };
        (_observers ??= []).Add(r);
        if (!was) Connect(); // 0 → 1: became watched, subscribe to sources
    }

    void IReactiveSource.RemoveObserver(Reaction r)
    {
        if (_observers == null) return;
        if (_observers.Remove(r) && _observers.Count == 0)
            DetachFromSources(); // 1 → 0: no longer watched, unsubscribe (cascades to source computeds)
    }

    void IReactiveSource.Refresh()
    {
        Refresh();
    }

    /// <summary>Read the current value WITHOUT subscribing (does not become a dependency of the reader).</summary>
    public T Peek()
    {
        lock (Reactive.Gate)
        {
            Refresh();
            return _value;
        }
    }

    public void Dispose()
    {
        lock (Reactive.Gate)
        {
            if (Disposed) return;
            Disposed = true;
            DetachFromSources();
            _observers?.Clear();
        }
    }

    private protected override void OnScheduled()
    {
        // A computed does not schedule itself; it propagates "maybe-dirty" to its own observers.
        if (_observers == null) return;
        foreach (var o in _observers) o.MarkStale(NodeState.Check);
    }

    private protected override void Execute()
    {
        var next = _compute();

        // Always-subscribed extras: track them even if this run didn't read them.
        for (var i = 0; i < _forced.Length; i++)
            if (_forced[i] is IReactiveSource src)
                src.Track();

        if (_equals.Equals(_value, next)) return;

        _value = next;
        _version++;

        // Our value really changed → observers must recompute (they are already ≥Check from the cascade).
        if (_observers != null)
            foreach (var o in _observers)
                o.MarkStale(NodeState.Dirty);

        // Handlers run inside this computed's own tracked Execute — suspend tracking so their reads
        // don't become phantom dependencies of it.
        Reactive.UntrackedInvoke(Changed, next);
        Reactive.UntrackedInvoke(Invalidated);
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
    public static Computed<T> From<T>(Func<T> compute, params ISignal[] forced)
    {
        return new Computed<T>(compute, null, forced);
    }

    /// <summary>Auto-tracked derived value with a custom equality comparer (controls change propagation).</summary>
    public static Computed<T> From<T>(Func<T> compute, IEqualityComparer<T> equals)
    {
        return new Computed<T>(compute, equals, []);
    }

    /// <summary>
    ///     Single-source map — value is always <c>map(source.Value)</c>. A convenience wrapper over the
    ///     auto-tracked form (reading <paramref name="source" />'s value wires the dependency).
    /// </summary>
    public static Computed<TResult> From<TSource, TResult>(Signal<TSource> source,
        Func<TSource, TResult> map)
    {
        return new Computed<TResult>(() => map(source.Value), null, []);
    }
}
