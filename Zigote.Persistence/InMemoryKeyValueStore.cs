namespace Zigote.Persistence;

/// <summary>
///     Ephemeral <see cref="IKeyValueStore" /> — a locked dictionary, nothing survives the process.
///     For tests, previews, and runs that must not touch the disk.
/// </summary>
public sealed class InMemoryKeyValueStore : IKeyValueStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public bool TryGet(string key, out string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_values) return _values.TryGetValue(key: key, value: out value!);
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_values) _values[key] = value;
    }

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_values) return _values.Remove(key);
    }

    public bool Contains(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_values) return _values.ContainsKey(key);
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_values)
        {
            string[] keys = new string[_values.Count];
            _values.Keys.CopyTo(array: keys, index: 0);
            Array.Sort(array: keys, comparer: StringComparer.Ordinal);
            return keys;
        }
    }

    public void Clear()
    {
        lock (_values) _values.Clear();
    }

    public void Flush() { }

    public void Dispose() { }
}
