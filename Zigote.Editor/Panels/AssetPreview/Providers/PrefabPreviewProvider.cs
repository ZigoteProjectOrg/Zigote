using System.Text.Json;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels.AssetPreview.Providers;

/// <summary>
///     Previews ".prefab" assets: shows the template JSON in the read-only code view and reports the
///     prefab name, template root, and node count parsed generically (handling the
///     <c>ReferenceHandler.Preserve</c> <c>$values</c> wrapper the scene serializer uses).
/// </summary>
public sealed class PrefabPreviewProvider : IAssetPreviewProvider
{
    public bool CanHandle(string ext)
    {
        return ext == ".prefab";
    }

    public Widget BuildPreview(string path, ThemeData theme)
    {
        return new CodeTextPreviewProvider().BuildPreview(path, theme);
    }

    public IEnumerable<(string Key, string Value)> ExtraMetadata(string path)
    {
        var rows = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String)
                rows.Add(("Prefab", n.GetString()!));

            if (root.TryGetProperty("Template", out var tmpl) &&
                tmpl.ValueKind == JsonValueKind.Object)
            {
                if (tmpl.TryGetProperty("Name", out var rn) && rn.ValueKind == JsonValueKind.String)
                    rows.Add(("Root", rn.GetString()!));
                rows.Add(("Nodes", CountNodes(tmpl).ToString()));
            }
        }
        catch
        {
            rows.Add(("Prefab", "unparseable"));
        }

        return rows;
    }

    private static int CountNodes(JsonElement node)
    {
        var count = 1; // this node
        if (!node.TryGetProperty("Children", out var children)) return count;

        // Preserve wraps arrays as { "$values": [...] }; accept a raw array too.
        var arr = children.ValueKind == JsonValueKind.Array ? children
            : children.ValueKind == JsonValueKind.Object &&
              children.TryGetProperty("$values", out var v) ? v
            : default;

        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object)
                    count += CountNodes(c);

        return count;
    }
}