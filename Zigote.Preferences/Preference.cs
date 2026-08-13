using Zigote.Core.State;

namespace Zigote.Preferences;

/// <summary>
///     A persisted signal: reads delegate to an internal <see cref="Signal{T}" /> (so dependency
///     tracking, <c>Computed</c>, <c>Effect</c>, <c>Watch</c>, and <c>Reactive.Batch</c> behave
///     exactly as for any signal) and equality-gated writes flow through to the owning store's
///     <c>IKeyValueStore</c>. Obtain instances from
///     <see cref="PreferenceStore.Preference{T}(string, T, IEqualityComparer{T}?)" /> — the store
///     guarantees one live instance per key.
///     <para>
///         Loads never throw: a missing or unparseable persisted value yields <see cref="Default" />
///         with <see cref="IsSet" /> false (the corrupt entry stays in storage for inspection). A
///         failing storage write propagates to the setter's caller — durability failures are not
///         silent. Values should be immutable (records, primitives, enums), the same rule signals
///         already have: mutating a stored object in place persists nothing.
///     </para>
/// </summary>
public sealed class Preference<T> : IReadableSignal<T>, IPreference
{
    private readonly IEqualityComparer<T> _comparer;
    private readonly Func<string, T> _deserialize;
    private readonly Func<T, string> _serialize;
    private readonly Signal<T> _signal;
    private readonly PreferenceStore _store;

    internal Preference(
        PreferenceStore store,
        string key,
        T defaultValue,
        IEqualityComparer<T> comparer,
        Func<T, string> serialize,
        Func<string, T> deserialize)
    {
        _store = store;
        Key = key;
        Default = defaultValue;
        _comparer = comparer;
        _serialize = serialize;
        _deserialize = deserialize;

        var value = defaultValue;
        bool isSet = false;
        if (store.TryGetRaw(key: key, raw: out string raw))
        {
            try
            {
                value = _deserialize(raw);
                isSet = true;
            }
            catch (Exception)
            {
                // Corrupt persisted value: fall back to the default, leave the entry in place.
            }
        }

        _signal = new Signal<T>(initialValue: value, comparer: comparer);
        IsSet = isSet;
    }

    /// <summary>The value this preference has when nothing is persisted (or after <see cref="Reset" />).</summary>
    public T Default { get; }

    public string Key { get; }

    /// <summary>
    ///     True when a persisted value backs the current one; false means <see cref="Default" /> is
    ///     live.
    /// </summary>
    public bool IsSet { get; private set; }

    public Type ValueType => typeof(T);

    /// <summary>Back to <see cref="Default" />; removes the persisted entry so the next load is unset too.</summary>
    public void Reset()
    {
        Reactive.Sync(() =>
            {
                if (IsSet)
                {
                    _store.RemoveRaw(Key);
                    IsSet = false;
                }

                _signal.Value = Default;
            }
        );
    }

    /// <summary>
    ///     Get: tracked read — subscribes the running reaction like any signal read. Set:
    ///     equality-gated write-through; an unchanged value neither notifies nor touches storage. The
    ///     first explicit set always persists, even when equal to <see cref="Default" /> — the user
    ///     chose it.
    /// </summary>
    public T Value
    {
        get => _signal.Value;
        set => SetValue(value);
    }

    public event Action? Invalidated
    {
        add => _signal.Invalidated += value;
        remove => _signal.Invalidated -= value;
    }

    /// <summary>Fires after the value actually changed. Handlers run untracked, like signal handlers.</summary>
    public event Action<T>? Changed
    {
        add => _signal.Changed += value;
        remove => _signal.Changed -= value;
    }

    /// <summary>Read the current value without subscribing the running reaction.</summary>
    public T Peek() => _signal.Peek();

    /// <summary>Atomic read-modify-write; runs under the reactive graph's lock.</summary>
    public void Update(Func<T, T> update) => Reactive.Sync(() => SetValue(update(_signal.Peek())));

    /// <summary>
    ///     Invokes <paramref name="listener" /> immediately with the current value, then on every
    ///     change.
    /// </summary>
    public IDisposable Subscribe(Action<T> listener) => _signal.Subscribe(listener);

    public override string ToString() => $"Preference({Key} = {_signal.Peek()})";

    // Persist-before-notify, all under the graph's re-entrant lock: compare + storage write +
    // signal set are atomic against concurrent writers, and a failing write leaves both the
    // signal and storage unchanged.
    private void SetValue(T value)
    {
        Reactive.Sync(() =>
            {
                if (IsSet && _comparer.Equals(x: _signal.Peek(), y: value)) return;
                _store.SetRaw(key: Key, raw: _serialize(value));
                IsSet = true;
                _signal.Value = value;
            }
        );
    }
}
