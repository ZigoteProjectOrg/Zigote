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
public sealed class Signal<T> : IReadableSignal<T>, IReactiveSource
{
    private readonly IEqualityComparer<T> _equals;
    private HashSet<Reaction>? _observers;
    private T _value;
    private long _version;

    public Signal(T initialValue, IEqualityComparer<T>? comparer = null)
    {
        _value = initialValue;
        _equals = comparer ?? EqualityComparer<T>.Default;
    }

    public T Value
    {
        get
        {
            lock (Reactive.Gate)
            {
                Reactive.EvalContext?.AddSource(this);
                return _value;
            }
        }
        set
        {
            lock (Reactive.Gate)
            {
                if (_equals.Equals(_value, value)) return;
                Write(value);
            }
        }
    }

    /// <inheritdoc />
    public event Action? Invalidated;

    public event Action<T>? Changed;

    long IReactiveSource.Version => _version;

    void IReactiveSource.Track()
    {
        Reactive.EvalContext?.AddSource(this);
    }

    void IReactiveSource.AddObserver(Reaction r)
    {
        (_observers ??= []).Add(r);
    }

    void IReactiveSource.RemoveObserver(Reaction r)
    {
        _observers?.Remove(r);
    }

    void IReactiveSource.Refresh()
    {
        // A signal is always current.
    }

    /// <summary>Read the current value WITHOUT subscribing (does not become a dependency of the reader).</summary>
    public T Peek()
    {
        lock (Reactive.Gate)
        {
            return _value;
        }
    }

    /// <summary>Set the value and notify unconditionally (skips the equality check).</summary>
    public void Set(T value)
    {
        lock (Reactive.Gate) Write(value);
    }

    /// <summary>Read-modify-write: store <c>update(current)</c> (equality-gated like the setter).</summary>
    public void Update(Func<T, T> update)
    {
        lock (Reactive.Gate)
        {
            var next = update(_value);
            if (_equals.Equals(_value, next)) return;
            Write(next);
        }
    }

    /// <summary>Subscribe and immediately invoke <paramref name="listener" /> with the current value.</summary>
    public IDisposable Subscribe(Action<T> listener)
    {
        lock (Reactive.Gate)
        {
            Changed += listener;
            try
            {
                listener(_value);
            }
            catch
            {
                Changed -= listener;
                throw;
            }

            return new Unsubscriber(() => Changed -= listener);
        }
    }

    public static implicit operator T(Signal<T> s)
    {
        return s.Value;
    }

    public override string ToString()
    {
        lock (Reactive.Gate)
        {
            return _value?.ToString() ?? "";
        }
    }

    // Commit a new value and cascade — always called under the lock.
    private void Write(T value)
    {
        _value = value;
        _version++;
        Reactive.Bump();

        // Mark direct observers Dirty inside a batch, so a fan-out settles each effect once and the whole
        // cascade's effects run after every input is set. This runs BEFORE firing Changed/Invalidated:
        // a user handler is allowed to re-enter and subscribe/dispose an observer of this same signal,
        // which would otherwise mutate `_observers` in the middle of the enumeration below.
        if (_observers is { Count: > 0 })
        {
            Reactive.EnterBatch();
            try
            {
                foreach (var r in _observers) r.MarkStale(NodeState.Dirty, fromSourceWrite: true);
            }
            finally
            {
                Reactive.LeaveBatch();
            }
        }

        Changed?.Invoke(value);
        Invalidated?.Invoke();
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }
}
