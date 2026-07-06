namespace Zigote.Editor.Panels.AssetPreview;

/// <summary>
///     Type-agnostic metadata for an asset on disk. Common fields (name/extension/size/modified)
///     are filled by <see cref="For" />; type-specific rows are contributed by an
///     <see cref="IAssetPreviewProvider" /> via <c>ExtraMetadata</c>.
/// </summary>
public sealed class AssetMetadata
{
    /// <summary>File name including extension.</summary>
    public string Name { get; init; } = "";

    /// <summary>Lower-case extension including the leading dot (e.g. ".png"), or "" for none.</summary>
    public string Extension { get; init; } = "";

    /// <summary>Absolute path to the asset.</summary>
    public string FullPath { get; init; } = "";

    /// <summary>Raw byte size, or -1 if unknown / unreadable.</summary>
    public long SizeBytes { get; init; } = -1;

    /// <summary>Human-readable size (e.g. "1.4 MB"), or "—" if unknown.</summary>
    public string SizeHuman { get; init; } = "—";

    /// <summary>Last-modified timestamp formatted for display, or "—".</summary>
    public string Modified { get; init; } = "—";

    /// <summary>Ordered type-specific rows shown beneath the common fields.</summary>
    public List<(string Key, string Value)> Rows { get; } = [];

    /// <summary>Build the common metadata for <paramref name="path" />. Never throws.</summary>
    public static AssetMetadata For(string path)
    {
        string name;
        string ext;
        try
        {
            name = Path.GetFileName(path);
            ext = Path.GetExtension(path).ToLowerInvariant();
        }
        catch
        {
            name = path;
            ext = "";
        }

        long size = -1;
        var sizeHuman = "—";
        var modified = "—";
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                size = info.Length;
                sizeHuman = HumanSize(size);
                modified = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            }
        }
        catch
        {
            /* stat failed — leave defaults */
        }

        return new AssetMetadata {
            Name = name,
            Extension = ext,
            FullPath = path,
            SizeBytes = size,
            SizeHuman = sizeHuman,
            Modified = modified,
        };
    }

    /// <summary>Format a byte count as a short human-readable string.</summary>
    public static string HumanSize(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = -1;
        do
        {
            v /= 1024.0;
            u++;
        } while (v >= 1024.0 && u < units.Length - 1);

        return v >= 100 ? $"{v:0} {units[u]}" : $"{v:0.0} {units[u]}";
    }
}