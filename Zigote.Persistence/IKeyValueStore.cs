namespace Zigote.Persistence;

/// <summary>
///     The storage boundary of the persistence layer: an opaque, synchronous, thread-safe
///     <c>string → string</c> map. Higher layers (Zigote.Preferences) put JSON-encoded values behind
///     string keys; implementations must round-trip both verbatim and never interpret them — one
///     file/table per store, so a key can never escape onto the filesystem.
///     <para>
///         Contract: all members are callable from any thread. <see cref="TryGet" /> never throws for
///         a missing key. <see cref="Set" /> may throw (disk full, locked database) — durability
///         failures must not be silent. <see cref="Flush" /> is a durability barrier: buffering
///         backends persist pending writes, write-through backends treat it as a no-op. Disposal
///         implies a flush. Keys are opaque non-empty strings — dot-separated namespacing
///         (<c>"editor.showGrid"</c>) is a convention, not a rule, and every member throws
///         <see cref="ArgumentException" /> on a null or empty key.
///     </para>
/// </summary>
public interface IKeyValueStore : IDisposable
{
    bool TryGet(string key, out string value);

    void Set(string key, string value);

    bool Remove(string key);

    bool Contains(string key);

    IReadOnlyList<string> Keys();

    void Clear();

    void Flush();
}
