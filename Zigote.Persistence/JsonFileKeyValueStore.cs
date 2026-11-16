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
///         A file that exists but cannot be <em>read</em> (a sharing violation from a scanner or a
///         second instance, a permission error) is not corruption and is never overwritten. The store
///         starts empty and retries the read on later calls; once it succeeds, the keys on disk
///         reappear under whatever was changed in the meantime. A save while the file is still
///         unreadable throws <see cref="IOException" /> rather than replace it.
///     </para>
///     <para>
///         With <c>autoFlush</c> (the default) every mutation rewrites the file; fine for
///         preference-sized stores. Pass <c>false</c> to buffer mutations until <see cref="Flush" />
///         or disposal.
///     </para>
/// </summary>
public sealed class JsonFileKeyValueStore : IKeyValueStore
{
    private readonly bool _autoFlush;
    private readonly string _path;
    private readonly SortedDictionary<string, string> _values = new(StringComparer.Ordinal);
    private bool _dirty;

    // Keys removed while the file on disk was still unread: the merge must not resurrect them.
    private HashSet<string>? _removedWhileUnread;

    // The file exists but its content is not in _values yet. While set, nothing may replace it.
    private bool _unreadOnDisk;

    public JsonFileKeyValueStore(string path, bool autoFlush = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _autoFlush = autoFlush;
        _unreadOnDisk = true;
        TryAdoptDiskLocked();
    }

    public bool TryGet(string key, out string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_values)
        {
            TryAdoptDiskLocked();
            return _values.TryGetValue(key: key, value: out value!);
        }
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_values)
        {
            if (_values.TryGetValue(key: key, value: out string? existing) &&
                string.Equals(a: existing, b: value, comparisonType: StringComparison.Ordinal))
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
            TryAdoptDiskLocked();
            bool removed = _values.Remove(key);
            if (_unreadOnDisk)
            {
                // The key may exist only in the file we could not read: remember the intent, and
                // answer true because the removal will take effect when the file is merged.
                (_removedWhileUnread ??= new HashSet<string>(StringComparer.Ordinal)).Add(key);
                removed = true;
            }

            if (!removed) return false;
            MarkDirtyLocked();
            return true;
        }
    }

    public bool Contains(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_values)
        {
            TryAdoptDiskLocked();
            return _values.ContainsKey(key);
        }
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_values)
        {
            TryAdoptDiskLocked();
            string[] keys = new string[_values.Count];
            _values.Keys.CopyTo(array: keys, index: 0);
            return keys;
        }
    }

    public void Clear()
    {
        lock (_values)
        {
            // Clearing is the one mutation that means "replace whatever is there": nothing on disk
            // is wanted any more, read or not.
            bool discardsUnread = _unreadOnDisk;
            _unreadOnDisk = false;
            _removedWhileUnread = null;
            if (_values.Count == 0 && !discardsUnread) return;
            _values.Clear();
            MarkDirtyLocked();
        }
    }

    public void Flush()
    {
        lock (_values)
        {
            if (_dirty)
                SaveLocked();
        }
    }

    public void Dispose() => Flush();

    private void MarkDirtyLocked()
    {
        _dirty = true;
        if (_autoFlush) SaveLocked();
    }

    private void SaveLocked()
    {
        if (_unreadOnDisk && !TryAdoptDiskLocked())
        {
            throw new IOException(
                $"'{_path}' exists but could not be read, so it was not overwritten. " +
                "The pending changes are kept in memory and saved once the file is readable."
            );
        }

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(
            value: _values,
            jsonTypeInfo: PersistenceJsonContext.Default.SortedDictionaryStringString
        );
        string tmp = _path + ".tmp";
        File.WriteAllText(path: tmp, contents: json);
        File.Move(sourceFileName: tmp, destFileName: _path, overwrite: true);
        _dirty = false;
    }

    /// <summary>
    ///     Bring the file's content into <see cref="_values" /> if that has not happened yet. Keys
    ///     set or removed in the meantime win over the file. False only while the file exists and
    ///     cannot be read, or is corrupt and could not be copied aside — the two states in which
    ///     replacing it would destroy data.
    /// </summary>
    private bool TryAdoptDiskLocked()
    {
        if (!_unreadOnDisk) return true;

        SortedDictionary<string, string>? onDisk = null;
        try
        {
            if (File.Exists(_path))
            {
                onDisk = JsonSerializer.Deserialize(
                    json: File.ReadAllText(_path),
                    jsonTypeInfo: PersistenceJsonContext.Default.SortedDictionaryStringString
                );
            }
        }
        catch (JsonException)
        {
            // Corrupt: start (or stay) empty, but only once the bytes are safe somewhere else.
            if (!Quarantine(_path)) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (onDisk is not null)
        {
            foreach ((string key, string value) in onDisk)
            {
                if (_removedWhileUnread?.Contains(key) == true) continue;
                _values.TryAdd(key: key, value: value);
            }
        }

        _unreadOnDisk = false;
        _removedWhileUnread = null;
        return true;
    }

    private static bool Quarantine(string path)
    {
        try
        {
            File.Copy(sourceFileName: path, destFileName: path + ".corrupt", overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
