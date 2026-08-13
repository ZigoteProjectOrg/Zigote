namespace Zigote.Core.State;

/// <summary>
///     A writable signal that follows a source: it holds whatever you last wrote, but as soon as any
///     signal read by <c>compute</c> changes, it snaps back to the freshly computed value. The primitive
///     for widget-local state that tracks a prop — a selected index that resets when the list changes, a
///     draft field that reloads when the edited entity changes (Angular's <c>linkedSignal</c>).
///     <code>
///     var selected = Linked.From(() => items.Value.FirstOrDefault());  // resets when items change
///     selected.Value = items.Value[3];                                 // manual override, sticks…
///     items.Value = newList;                                           // …until the source moves
///     </code>
///     <para>
///         Reads track like any signal, so computeds/effects/Watch depend on it normally. It is a
///         <see cref="Signal{T}" /> driven by an <see cref="Effect" />, nothing more — dispose it to stop
///         following (the held value stays readable and writable).
///     </para>
/// </summary>
public sealed class LinkedSignal<T> : IReadableSignal<T>, IDisposable
{
    private readonly Func<T> _compute;
    private readonly Signal<T> _current;
    private readonly Effect _link;

    internal LinkedSignal(Func<T> compute, IEqualityComparer<T>? comparer)
    {
        _compute = compute;
        _current = new Signal<T>(default!, comparer);

        // Tracked: re-runs whenever a source of `compute` changes, and each run overwrites whatever was
        // written by hand since the last one. It writes a signal it never reads, so there is no cycle.
        _link = new Effect(() => _current.Value = _compute());
    }

    /// <summary>Get: tracked read. Set: manual override, kept until the next source change.</summary>
    public T Value
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <inheritdoc />
    public event Action? Invalidated
    {
        add => _current.Invalidated += value;
        remove => _current.Invalidated -= value;
    }

    /// <summary>Fires after the value actually changed — by a manual write or by a source-driven reset.</summary>
    public event Action<T>? Changed
    {
        add => _current.Changed += value;
        remove => _current.Changed -= value;
    }

    /// <summary>Stop following the source. The current value remains readable and writable.</summary>
    public void Dispose()
    {
        _link.Dispose();
    }

    /// <summary>Read the current value without subscribing the running reaction.</summary>
    public T Peek()
    {
        return _current.Peek();
    }

    /// <summary>Drop a manual override now, without waiting for the source to change.</summary>
    public void Reset()
    {
        Reactive.Sync(() => _current.Value = Reactive.Untracked(_compute));
    }

    /// <summary>Subscribe and immediately invoke <paramref name="listener" /> with the current value.</summary>
    public IDisposable Subscribe(Action<T> listener)
    {
        return _current.Subscribe(listener);
    }

    public override string ToString()
    {
        return $"Linked({_current})";
    }
}

public static class Linked
{
    /// <summary>
    ///     A writable signal seeded (and re-seeded) by <paramref name="compute" /> — see
    ///     <see cref="LinkedSignal{T}" />. Dependencies are tracked automatically, like a computed.
    /// </summary>
    public static LinkedSignal<T> From<T>(Func<T> compute, IEqualityComparer<T>? comparer = null)
    {
        return new LinkedSignal<T>(compute, comparer);
    }
}
