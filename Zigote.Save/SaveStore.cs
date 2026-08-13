using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Zigote.Save;

/// <summary>
///     Slot-based, versioned save-game persistence. One JSON file per slot (<c>&lt;slot&gt;.save</c>
///     inside
///     <paramref name="directory" />, created lazily on first write) wrapping the game's state in a
///     small
///     envelope: <c>{"version", "savedAtUnixMs", "payload"}</c>. Reads NEVER throw for I/O, bad JSON,
///     or
///     version problems — they return a typed <see cref="SaveReadResult{T}" />. Older envelopes are
///     upgraded
///     through the registered payload migrations (fromVersion → fromVersion+1, chained up to
///     <paramref name="currentVersion" />). Writes are atomic: serialize to
///     <c>&lt;slot&gt;.save.tmp</c>, then
///     rename over the final file, so a crash mid-write never corrupts an existing save. The
///     <see cref="JsonTypeInfo{T}" /> overloads are the NativeAOT path; the reflection defaults are
///     fine under JIT.
/// </summary>
public sealed class SaveStore(
    string directory,
    int currentVersion,
    JsonSerializerOptions? options = null)
{
    private const string Extension = ".save";

    private static readonly char[] InvalidSlotChars = Path.GetInvalidFileNameChars();

    private readonly Dictionary<int, Func<JsonNode, JsonNode>> _migrations = new();
    private readonly JsonSerializerOptions _options = options ?? JsonSerializerOptions.Default;

    public void RegisterMigration(int fromVersion, Func<JsonNode, JsonNode> migrate)
    {
        _migrations[fromVersion] = migrate;
    }

    public SaveWriteResult Write<T>(string slot, T state)
    {
        return WriteCore(slot, () => JsonSerializer.SerializeToNode(state, _options));
    }

    public SaveWriteResult Write<T>(string slot, T state, JsonTypeInfo<T> typeInfo)
    {
        return WriteCore(slot, () => JsonSerializer.SerializeToNode(state, typeInfo));
    }

    public SaveReadResult<T> Read<T>(string slot)
    {
        return ReadCore<T>(slot, node => node is null ? default : node.Deserialize<T>(_options));
    }

    public SaveReadResult<T> Read<T>(string slot, JsonTypeInfo<T> typeInfo)
    {
        return ReadCore<T>(slot, node => node is null ? default : node.Deserialize(typeInfo));
    }

    public bool Exists(string slot)
    {
        return IsValidSlot(slot) && File.Exists(PathOf(slot));
    }

    public bool Delete(string slot)
    {
        if (!IsValidSlot(slot)) return false;
        var path = PathOf(slot);
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>All readable slots, newest first. Corrupt/unreadable files are skipped (best-effort).</summary>
    public IReadOnlyList<SaveSlotInfo> List()
    {
        if (!Directory.Exists(directory)) return [];

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*" + Extension);
        }
        catch (Exception)
        {
            return [];
        }

        var infos = new List<SaveSlotInfo>(files.Length);
        foreach (var file in files)
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject envelope
                    || envelope["version"] is not JsonValue versionValue
                    || !versionValue.TryGetValue(out int version)
                    || envelope["savedAtUnixMs"] is not JsonValue savedAtValue
                    || !savedAtValue.TryGetValue(out long savedAtMs))
                    continue;

                infos.Add(
                    new SaveSlotInfo(
                        Path.GetFileNameWithoutExtension(file),
                        version,
                        DateTimeOffset.FromUnixTimeMilliseconds(savedAtMs),
                        new FileInfo(file).Length
                    )
                );
            }
            catch (Exception)
            {
                // Skip: List is a browse operation, not a validator — Read reports the real status.
            }

        infos.Sort(static (a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return infos;
    }

    private SaveWriteResult WriteCore(string slot, Func<JsonNode?> serializePayload)
    {
        if (!IsValidSlot(slot))
            return new SaveWriteResult(SaveStatus.InvalidSlot, $"Invalid slot name '{slot}'.");

        JsonNode? payload;
        try
        {
            payload = serializePayload();
        }
        catch (Exception e)
        {
            return new SaveWriteResult(SaveStatus.IoError, e.Message);
        }

        var envelope = new JsonObject {
            ["version"] = currentVersion,
            ["savedAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["payload"] = payload,
        };

        var path = PathOf(slot);
        var tmpPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = File.Create(tmpPath))
            using (var writer = new Utf8JsonWriter(
                       stream,
                       new JsonWriterOptions { Indented = _options.WriteIndented }
                   ))
            {
                envelope.WriteTo(writer, _options);
            }

            File.Move(tmpPath, path, true);
        }
        catch (Exception e)
        {
            try
            {
                File.Delete(tmpPath);
            }
            catch (Exception)
            {
                // Best effort — the next successful Write overwrites it anyway.
            }

            return new SaveWriteResult(SaveStatus.IoError, e.Message);
        }

        return new SaveWriteResult(SaveStatus.Ok);
    }

    private SaveReadResult<T> ReadCore<T>(string slot, Func<JsonNode?, T?> deserializePayload)
    {
        if (!IsValidSlot(slot))
            return new SaveReadResult<T>(
                SaveStatus.InvalidSlot,
                default,
                $"Invalid slot name '{slot}'."
            );

        string text;
        try
        {
            var path = PathOf(slot);
            if (!File.Exists(path)) return new SaveReadResult<T>(SaveStatus.NotFound);
            text = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            return new SaveReadResult<T>(SaveStatus.IoError, default, e.Message);
        }

        int version;
        JsonNode? payload;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject envelope
                || envelope["version"] is not JsonValue versionValue
                || !versionValue.TryGetValue(out version)
                || !envelope.TryGetPropertyValue("payload", out payload))
                return new SaveReadResult<T>(
                    SaveStatus.Corrupt,
                    default,
                    "Malformed save envelope."
                );

            // Detach so migrations can move the payload's children into a rebuilt node.
            envelope.Remove("payload");
        }
        catch (JsonException e)
        {
            return new SaveReadResult<T>(SaveStatus.Corrupt, default, e.Message);
        }

        if (version > currentVersion)
            return new SaveReadResult<T>(
                SaveStatus.FutureVersion,
                default,
                $"Save is version {version}; this build reads up to {currentVersion}."
            );

        while (version < currentVersion)
        {
            if (payload is null)
                return new SaveReadResult<T>(
                    SaveStatus.Corrupt,
                    default,
                    "Null payload cannot be migrated."
                );
            if (!_migrations.TryGetValue(version, out var migrate))
                return new SaveReadResult<T>(
                    SaveStatus.MigrationMissing,
                    default,
                    $"No migration registered from version {version}."
                );
            try
            {
                payload = migrate(payload);
            }
            catch (Exception e)
            {
                return new SaveReadResult<T>(
                    SaveStatus.MigrationFailed,
                    default,
                    $"Migration {version}→{version + 1} failed: {e.Message}"
                );
            }

            version++;
        }

        try
        {
            return new SaveReadResult<T>(SaveStatus.Ok, deserializePayload(payload));
        }
        catch (Exception e)
        {
            return new SaveReadResult<T>(SaveStatus.Corrupt, default, e.Message);
        }
    }

    private string PathOf(string slot)
    {
        return Path.Combine(directory, slot + Extension);
    }

    // A slot is a bare file stem, never a path: separators, "..", drive colons, and any char the
    // filesystem rejects all fail here so a slot can never address outside the store directory.
    private static bool IsValidSlot(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot) || slot.Contains("..")) return false;
        foreach (var c in slot)
            if (c is '/' or '\\' or ':' || Array.IndexOf(InvalidSlotChars, c) >= 0)
                return false;
        return true;
    }
}
