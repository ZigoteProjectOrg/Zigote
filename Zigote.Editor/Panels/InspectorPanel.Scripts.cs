using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zigote.Core.Math3D;
using Zigote.Runtime.Scene;
using Zigote.Scripting.Metadata;
// Dropdown<T> must be referenced with a concrete type — alias for clarity:

namespace Zigote.Editor.Panels;

public sealed partial class InspectorPanel
{
    private PropRow BuildExportedFieldRow(ExportedField field, ScriptMetadata meta, SceneNode node)
    {
        // Fall back to the script's compiled-in default when the node has no stored override, so a
        // freshly attached script shows its real defaults (e.g. Speed = 90) instead of zeros.
        if (!node.ScriptExports.TryGetValue(field.Name, out var currentJson))
            currentJson = meta.DefaultExports.GetValueOrDefault(field.Name);

        void SaveJson(string json)
        {
            node.ScriptExports[field.Name] = json;
            _state.ApplyLiveScriptExport(
                node,
                field,
                json
            ); // live-tune the running component in play mode
            _state.NotifySceneChanged();
        }

        switch (field.Kind)
        {
            case ExportedFieldKind.Bool:
            {
                var cur = "true".Equals(currentJson, StringComparison.OrdinalIgnoreCase);
                return PropRow.Toggle(
                    field.DisplayName,
                    cur,
                    v => SaveJson(v ? "true" : "false"),
                    _theme
                );
            }
            case ExportedFieldKind.Int:
            {
                var cur = int.TryParse(currentJson, out var i) ? i : 0f;
                return PropRow.Float(
                    field.DisplayName,
                    cur,
                    v => SaveJson(((int)v).ToString()),
                    _theme,
                    (float)(field.RangeMin ?? 0),
                    (float)(field.RangeMax ?? 1000),
                    1f
                );
            }
            case ExportedFieldKind.Float:
            {
                var cur = float.TryParse(
                    currentJson,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var f
                )
                    ? f
                    : 0f;
                return PropRow.Float(
                    field.DisplayName,
                    cur,
                    v => SaveJson(v.ToString(CultureInfo.InvariantCulture)),
                    _theme,
                    (float)(field.RangeMin ?? 0f),
                    (float)(field.RangeMax ?? 100f),
                    0.1f
                );
            }
            case ExportedFieldKind.String:
            {
                var cur = currentJson != null
                    ? JsonSerializer.Deserialize<string>(currentJson) ?? ""
                    : "";
                return PropRow.Text(
                    field.DisplayName,
                    cur,
                    v => SaveJson(JsonSerializer.Serialize(v)),
                    _theme,
                    _app
                );
            }
            case ExportedFieldKind.Vec3:
            {
                var cur = Vec3.Zero;
                if (currentJson != null)
                    try
                    {
                        var n = JsonNode.Parse(currentJson)!;
                        cur = new Vec3(
                            n["x"]!.GetValue<float>(),
                            n["y"]!.GetValue<float>(),
                            n["z"]!.GetValue<float>()
                        );
                    }
                    catch
                    {
                        /* use default */
                    }

                return field.IsColor
                    ? PropRow.Vec3Color(
                        field.DisplayName,
                        cur,
                        v => SaveJson(
                            $"{{\"x\":{v.X.ToString("G", CultureInfo.InvariantCulture)},\"y\":{v.Y.ToString("G", CultureInfo.InvariantCulture)},\"z\":{v.Z.ToString("G", CultureInfo.InvariantCulture)}}}"
                        ),
                        _theme
                    )
                    : PropRow.Vec3(
                        field.DisplayName,
                        cur,
                        v => SaveJson(
                            $"{{\"x\":{v.X.ToString("G", CultureInfo.InvariantCulture)},\"y\":{v.Y.ToString("G", CultureInfo.InvariantCulture)},\"z\":{v.Z.ToString("G", CultureInfo.InvariantCulture)}}}"
                        ),
                        _theme
                    );
            }
            default:
                return PropRow.Text(
                    field.DisplayName,
                    currentJson ?? "",
                    v => SaveJson(v),
                    _theme,
                    _app
                );
        }
    }

    private static string? ResolveProjectPath(string? scriptPath)
    {
        if (string.IsNullOrEmpty(scriptPath)) return null;
        if (scriptPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return scriptPath;
        var dir = Path.GetDirectoryName(scriptPath);
        return dir != null
            ? Directory.GetFiles(dir, "*.csproj").FirstOrDefault()
            : null;
    }
}