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
        if (!node.ScriptExports.TryGetValue(key: field.Name, value: out string? currentJson))
            currentJson = meta.DefaultExports.GetValueOrDefault(field.Name);

        void SaveJson(string json)
        {
            node.ScriptExports[field.Name] = json;
            _state.ApplyLiveScriptExport(
                node: node,
                field: field,
                json: json
            ); // live-tune the running component in play mode
            _state.NotifySceneChanged();
        }

        switch (field.Kind)
        {
            case ExportedFieldKind.Bool:
            {
                bool cur = "true".Equals(
                    value: currentJson,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                );
                return PropRow.Toggle(
                    label: field.DisplayName,
                    value: cur,
                    onChange: v => SaveJson(v ? "true" : "false"),
                    theme: _theme
                );
            }
            case ExportedFieldKind.Int:
            {
                float cur = int.TryParse(s: currentJson, result: out int i) ? i : 0f;
                return PropRow.Float(
                    label: field.DisplayName,
                    value: cur,
                    onChange: v => SaveJson(((int)v).ToString()),
                    theme: _theme,
                    min: (float)(field.RangeMin ?? 0),
                    max: (float)(field.RangeMax ?? 1000),
                    step: 1f
                );
            }
            case ExportedFieldKind.Float:
            {
                float cur = float.TryParse(
                    s: currentJson,
                    style: NumberStyles.Float,
                    provider: CultureInfo.InvariantCulture,
                    result: out float f
                )
                    ? f
                    : 0f;
                return PropRow.Float(
                    label: field.DisplayName,
                    value: cur,
                    onChange: v => SaveJson(v.ToString(CultureInfo.InvariantCulture)),
                    theme: _theme,
                    min: (float)(field.RangeMin ?? 0f),
                    max: (float)(field.RangeMax ?? 100f),
                    step: 0.1f
                );
            }
            case ExportedFieldKind.String:
            {
                string cur = currentJson != null
                    ? JsonSerializer.Deserialize<string>(currentJson) ?? ""
                    : "";
                return PropRow.Text(
                    label: field.DisplayName,
                    value: cur,
                    onChange: v => SaveJson(JsonSerializer.Serialize(v)),
                    theme: _theme,
                    app: _app
                );
            }
            case ExportedFieldKind.Vec3:
            {
                var cur = Vec3.Zero;
                if (currentJson != null)
                {
                    try
                    {
                        var n = JsonNode.Parse(currentJson)!;
                        cur = new Vec3(
                            x: n["x"]!.GetValue<float>(),
                            y: n["y"]!.GetValue<float>(),
                            z: n["z"]!.GetValue<float>()
                        );
                    }
                    catch
                    {
                        /* use default */
                    }
                }

                return field.IsColor
                    ? PropRow.Vec3Color(
                        label: field.DisplayName,
                        current: cur,
                        setter: v => SaveJson(
                            $"{{\"x\":{v.X.ToString(format: "G", provider: CultureInfo.InvariantCulture)},\"y\":{v.Y.ToString(format: "G", provider: CultureInfo.InvariantCulture)},\"z\":{v.Z.ToString(format: "G", provider: CultureInfo.InvariantCulture)}}}"
                        ),
                        theme: _theme
                    )
                    : PropRow.Vec3(
                        label: field.DisplayName,
                        current: cur,
                        setter: v => SaveJson(
                            $"{{\"x\":{v.X.ToString(format: "G", provider: CultureInfo.InvariantCulture)},\"y\":{v.Y.ToString(format: "G", provider: CultureInfo.InvariantCulture)},\"z\":{v.Z.ToString(format: "G", provider: CultureInfo.InvariantCulture)}}}"
                        ),
                        theme: _theme
                    );
            }
            default:
                return PropRow.Text(
                    label: field.DisplayName,
                    value: currentJson ?? "",
                    onChange: v => SaveJson(v),
                    theme: _theme,
                    app: _app
                );
        }
    }

    private static string? ResolveProjectPath(string? scriptPath)
    {
        if (string.IsNullOrEmpty(scriptPath)) return null;
        if (scriptPath.EndsWith(
                value: ".csproj",
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) return scriptPath;
        string? dir = Path.GetDirectoryName(scriptPath);
        return dir != null
            ? Directory.GetFiles(path: dir, searchPattern: "*.csproj").FirstOrDefault()
            : null;
    }
}
