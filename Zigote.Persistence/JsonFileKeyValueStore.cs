using System.Text.Json;

namespace Zigote.Persistence;

/// <summary>
///     File-backed <see cref="IKeyValueStore" />: one JSON object per store, ordinal-sorted keys,
///     indented — diff-friendly and hand-inspectable. Writes are atomic (<c>&lt;path&gt;.tmp</c>, then
///     rename over the final file, the <c>SaveStore</c> idiom) so a crash mid-write never corrupts the
///     previous state. Loading never throws: a corrupt file is copied aside to
///     <c>&lt;path&gt;.corrupt</c> and the store starts empty — data is quarantined, never silently
///     destroyed.
///     <para>
///         With <paramref name="autoFlush" /> (the default) every mutation rewrites the file; fine for
///         preference-sized stores. Pass <c>false</c> to buffer mutations until <see cref="Flush" /> or
///         disposal.
///     </para>
/// </summary>
public sealed class JsonFileKeyValueStore(string path, bool autoFlush = true) : IKeyValueStore
{
    private readonly SortedDictionary<string, string> _values = Load(path);
    private bool _dirty;

    public bool TryGet(string key, out string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_values)
        {
            return _values.TryGetValue(key, out value!);
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_values)
        {
            if (_values.TryGetValue(key, out var existing) &&
                string.Equals(existing, value, StringComparison.Ordinal))
                return;
            _values[key] = value;
            MarkDirtyLocked();
        }
    }

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_values)
        {
            if (!_values.Remove(key)) return false;
            MarkDirtyLocked();
            return true;
        }
    }

    public bool Contains(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_values)
        {
            return _values.ContainsKey(key);
        }
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_values)
        {
            var keys = new string[_values.Count];
            _values.Keys.CopyTo(keys, 0);
            return keys;
        }
    }

    public void Clear()
    {
        lock (_values)
        {
            if (_values.Count == 0) return;
            _values.Clear();
            MarkDirtyLocked();
        }
    }

    public void Flush()
    {
        lock (_values)
        {
            if (_dirty) SaveLocked();
        }
    }

    public void Dispose()
    {
        Flush();
    }

    private void MarkDirtyLocked()
    {
        _dirty = true;
        if (autoFlush) SaveLocked();
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(
            _values,
            PersistenceJsonContext.Default.SortedDictionaryStringString
        );
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, true);
        _dirty = false;
    }

    private static SortedDictionary<string, string> Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) return new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize(
                json,
                PersistenceJsonContext.Default.SortedDictionaryStringString
            );
            return loaded is null
                ? new SortedDictionary<string, string>(StringComparer.Ordinal)
                : new SortedDictionary<string, string>(loaded, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Quarantine(path);
            return new SortedDictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void Quarantine(string path)
    {
        try
        {
            File.Copy(path, path + ".corrupt", true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Quarantine is best-effort; the original file stays in place either way.
        }
    }
}
