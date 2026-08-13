using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zigote.Core.Assets;

/// <summary>
///     Bidirectional map between <see cref="AssetId" /> GUIDs and project-relative paths.
///     <para>
///         On project open, load the registry from disk. When an asset is first referenced
///         (import, drag-drop, path field), call <see cref="Register" /> to get a stable GUID.
///         When a file is renamed in the asset browser, call <see cref="RenamePath" /> — all
///         scene references holding the <see cref="AssetId" /> resolve correctly without any
///         scene save/load cycle.
///     </para>
///     <para>
///         Path contract: keys are <b>project-relative</b> and compared case-insensitively.
///         Callers that hold absolute paths must normalise through <see cref="AssetPath" />
///         before registering/resolving, or the same asset will mint duplicate ids.
///     </para>
/// </summary>
public sealed class AssetRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        TypeInfoResolver = RegistryJsonContext.Default, // registry load works under NativeAOT
    };

    private readonly Dictionary<AssetId, string> _idToPath = new();
    private readonly Dictionary<string, AssetId> _pathToId = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<AssetId, string> All => _idToPath;

    /// <summary>
    ///     Return the <see cref="AssetId" /> for <paramref name="relativePath" />, registering a new
    ///     GUID if it is not yet tracked.
    /// </summary>
    public AssetId Register(string relativePath)
    {
        // Canonical key form (forward slashes) so the same file registered from Windows and
        // macOS/Linux resolves to one id. Rooted paths pass through untouched — ToRelative with no
        // root would strip a unix path's leading '/' (callers like the streaming tests register
        // absolute scratch paths deliberately).
        if (!Path.IsPathRooted(relativePath))
            relativePath = AssetPath.ToRelative(relativePath, null);
        if (_pathToId.TryGetValue(relativePath, out var existing)) return existing;
        var id = AssetId.New();
        _idToPath[id] = relativePath;
        _pathToId[relativePath] = id;
        return id;
    }

    /// <summary>
    ///     Resolve a GUID to its current project-relative path, or <see langword="null" /> if
    ///     unknown.
    /// </summary>
    public string? Resolve(AssetId id)
    {
        _idToPath.TryGetValue(id, out var path);
        return path;
    }

    /// <summary>
    ///     Look up the <see cref="AssetId" /> for a known path, or <see langword="null" /> if
    ///     unregistered.
    /// </summary>
    public AssetId? Find(string relativePath)
    {
        return _pathToId.TryGetValue(relativePath, out var id) ? id : null;
    }

    /// <summary>
    ///     Update the registry when a file is renamed or moved.
    ///     The GUID is preserved — only the path entry changes.
    ///     No-op if <paramref name="oldPath" /> is not registered.
    /// </summary>
    public void RenamePath(string oldPath, string newPath)
    {
        if (!_pathToId.TryGetValue(oldPath, out var id)) return;
        _pathToId.Remove(oldPath);
        _pathToId[newPath] = id;
        _idToPath[id] = newPath;
    }

    /// <summary>Remove a GUID+path pair (e.g. file was deleted from the project).</summary>
    public void Remove(string relativePath)
    {
        if (!_pathToId.TryGetValue(relativePath, out var id)) return;
        _pathToId.Remove(relativePath);
        _idToPath.Remove(id);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>Serialize the registry to a JSON file at <paramref name="registryPath" />.</summary>
    public void Save(string registryPath)
    {
        // { "guid": "relative/path.ext", ... }
        var dict = _idToPath.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
        File.WriteAllText(registryPath, JsonSerializer.Serialize(dict, JsonOpts));
    }

    /// <summary>
    ///     Load a registry previously written by <see cref="Save" />. Returns an empty registry if the
    ///     file does not
    ///     exist.
    /// </summary>
    public static AssetRegistry Load(string registryPath)
    {
        var reg = new AssetRegistry();
        if (!File.Exists(registryPath)) return reg;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(registryPath),
                JsonOpts
            );
            if (dict is null) return reg;
            foreach (var (key, path) in dict)
                if (Guid.TryParse(key, out var g))
                {
                    var id = new AssetId(g);
                    reg._idToPath[id] = path;
                    reg._pathToId[path] = id;
                }
        }
        catch
        {
            // Corrupt/missing registry — start fresh rather than crashing.
        }

        return reg;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class RegistryJsonContext : JsonSerializerContext;
