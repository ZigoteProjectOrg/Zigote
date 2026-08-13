using System.Text.Json;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels.AssetPreview.Providers;

/// <summary>
///     Previews ".scene" assets: shows the JSON in the read-only code view and reports the node
///     count / root name parsed generically from the document (no coupling to the SceneGraph model).
/// </summary>
public sealed class SceneInfoPreviewProvider : IAssetPreviewProvider
{
    public bool CanHandle(string ext) => ext == ".scene";

    public Widget BuildPreview(string path, ThemeData theme)
    {
        // Reuse the monospace JSON view for the body.
        return new CodeTextPreviewProvider().BuildPreview(path: path, theme: theme);
    }

    public IEnumerable<(string Key, string Value)> ExtraMetadata(string path)
    {
        var rows = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            string? name = FindRootName(root);
            if (name is not null) rows.Add(("Root", name));

            int count = CountNodes(root);
            rows.Add(("Nodes", count.ToString()));

            if (root.TryGetProperty(propertyName: "EnvironmentPath", value: out var env) &&
                env.ValueKind == JsonValueKind.String)
            {
                string? ev = env.GetString();
                if (!string.IsNullOrEmpty(ev)) rows.Add(("Environment", ev!));
            }
        }
        catch
        {
            rows.Add(("Scene", "unparseable JSON"));
        }

        return rows;
    }

    private static string? FindRootName(JsonElement root)
    {
        if (root.TryGetProperty(propertyName: "Root", value: out var r) &&
            r.ValueKind == JsonValueKind.Object &&
            r.TryGetProperty(propertyName: "Name", value: out var n) &&
            n.ValueKind == JsonValueKind.String)
            return n.GetString();
        if (root.TryGetProperty(propertyName: "Name", value: out var topName) &&
            topName.ValueKind == JsonValueKind.String)
            return topName.GetString();
        return null;
    }

    /// <summary>Count every object that exposes a "Children" array, recursively (node-like shape).</summary>
    private static int CountNodes(JsonElement element)
    {
        int count = 0;
        Walk(element);
        return count;

        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (e.TryGetProperty(propertyName: "Children", value: out var children) &&
                        children.ValueKind == JsonValueKind.Array)
                    {
                        count++; // this is a node
                        foreach (var c in children.EnumerateArray()) Walk(c);
                    }
                    else
                    {
                        // Descend into nested objects (e.g. a wrapping "Root").
                        foreach (var prop in e.EnumerateObject()) Walk(prop.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Walk(item);
                    break;
            }
        }
    }
}
