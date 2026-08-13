using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zigote.Core.State;
using Zigote.Persistence;

namespace Zigote.Preferences;

/// <summary>
///     The composition root of the preferences layer: hands out <see cref="Preference{T}" />
///     instances (one per key, cached — two live signals over one entry would race) and owns the
///     <see cref="IKeyValueStore" /> they write through, including its disposal. Values are
///     serialized as JSON; the reflection overload is fine under JIT, the
///     <see cref="JsonTypeInfo{T}" /> overload is the NativeAOT path (the <c>SaveStore</c> split).
///     <para>
///         Declarative usage: group preferences in a plain class, the same shape as any signal store —
///         <c>
///             public Preference&lt;bool&gt; ShowGrid { get; } = store.Preference("editor.showGrid",
///             true);
///         </c>
///         .
///         The backend (memory, JSON file, SQLite) is chosen here and nothing downstream changes.
///     </para>
/// </summary>
public sealed class PreferenceStore : IDisposable
{
    private readonly Dictionary<string, IPreference> _entries = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _options;
    private readonly List<PreferencesProvider> _providers = [];
    private readonly IKeyValueStore _storage;
    private bool _disposed;

    public PreferenceStore(IKeyValueStore storage, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _storage = storage;
        _options = options ?? JsonSerializerOptions.Default;
    }

    /// <summary>Providers constructed against this store, in construction order.</summary>
    public IReadOnlyList<PreferencesProvider> Providers
    {
        get
        {
            lock (_entries) return _providers.ToArray();
        }
    }

    public void Dispose()
    {
        lock (_entries)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _storage.Dispose();
    }

    /// <summary>
    ///     The preference behind <paramref name="key" />, created on first request. Later calls return
    ///     the same instance (the first call's default and comparer win); asking for the same key with
    ///     a different <typeparamref name="T" /> throws <see cref="InvalidOperationException" />.
    /// </summary>
    public Preference<T> Preference<T>(
        string key,
        T defaultValue,
        IEqualityComparer<T>? comparer = null)
    {
        return GetOrCreate(
            key: key,
            defaultValue: defaultValue,
            comparer: comparer,
            serialize: value => JsonSerializer.Serialize(value: value, options: _options),
            deserialize: raw => JsonSerializer.Deserialize<T>(json: raw, options: _options)!
        );
    }

    /// <summary>Reflection-free variant for NativeAOT; otherwise identical to the default overload.</summary>
    public Preference<T> Preference<T>(
        string key,
        T defaultValue,
        JsonTypeInfo<T> typeInfo,
        IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return GetOrCreate(
            key: key,
            defaultValue: defaultValue,
            comparer: comparer,
            serialize: value => JsonSerializer.Serialize(value: value, jsonTypeInfo: typeInfo),
            deserialize: raw => JsonSerializer.Deserialize(json: raw, jsonTypeInfo: typeInfo)!
        );
    }

    /// <summary>
    ///     Every known preference back to its default and the backing storage cleared — including
    ///     keys that were persisted but never materialized as a <see cref="Preference{T}" /> this
    ///     run. Runs as one reactive batch, so dependent effects settle once.
    /// </summary>
    public void ResetAll()
    {
        IPreference[] snapshot;
        lock (_entries)
        {
            snapshot = new IPreference[_entries.Count];
            _entries.Values.CopyTo(array: snapshot, index: 0);
        }

        Reactive.Sync(() => Reactive.Batch(() =>
                {
                    _storage.Clear();
                    foreach (var entry in snapshot) entry.Reset();
                }
            )
        );
    }

    /// <summary>Durability barrier — forwards to the backend (a no-op for write-through backends).</summary>
    public void Flush() => _storage.Flush();

    internal void RegisterProvider(PreferencesProvider provider)
    {
        lock (_entries) _providers.Add(provider);
    }

    internal bool TryGetRaw(string key, out string raw) =>
        _storage.TryGet(key: key, value: out raw);

    internal void SetRaw(string key, string raw) => _storage.Set(key: key, value: raw);

    internal void RemoveRaw(string key) => _storage.Remove(key);

    private Preference<T> GetOrCreate<T>(
        string key,
        T defaultValue,
        IEqualityComparer<T>? comparer,
        Func<T, string> serialize,
        Func<string, T> deserialize)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_entries)
        {
            ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
            if (_entries.TryGetValue(key: key, value: out var existing))
            {
                if (existing is Preference<T> typed) return typed;
                throw new InvalidOperationException(
                    $"Preference '{key}' already exists as {existing.GetType().Name}; " +
                    $"it cannot also be a Preference<{typeof(T).Name}>."
                );
            }

            var created = new Preference<T>(
                store: this,
                key: key,
                defaultValue: defaultValue,
                comparer: comparer ?? EqualityComparer<T>.Default,
                serialize: serialize,
                deserialize: deserialize
            );
            _entries.Add(key: key, value: created);
            return created;
        }
    }
}
