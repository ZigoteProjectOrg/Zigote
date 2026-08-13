using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Scripting.Metadata;

namespace Zigote.Scripting.Serialization;

/// <summary>
///     Serializes and deserializes exported script fields as a flat JSON object
///     so their values survive script reload and scene save/load.
/// </summary>
public static class ScriptSerializer
{
    /// <summary>
    ///     Serialize the exported fields of a component instance into a dictionary of
    ///     <c>fieldName → JSON string</c> for storage on the SceneNode.
    /// </summary>
    public static Dictionary<string, string> Serialize(Component instance, ScriptMetadata meta)
    {
        var result = new Dictionary<string, string>();
        foreach (var field in meta.ExportedFields)
        {
            var value = field.GetValue(instance);
            if (value is null) continue;
            result[field.Name] = ValueToJson(value, field.Kind);
        }

        return result;
    }

    /// <summary>
    ///     Restore exported field values from a stored dictionary onto a freshly created instance.
    ///     Missing fields are silently skipped (handles renamed/removed properties gracefully).
    /// </summary>
    public static void Deserialize(
        Component instance,
        ScriptMetadata meta,
        IReadOnlyDictionary<string, string> stored)
    {
        foreach (var field in meta.ExportedFields)
            if (stored.TryGetValue(field.Name, out var json))
                DeserializeField(instance, field, json);
    }

    /// <summary>
    ///     Restore a single exported field's value from its JSON onto an instance. Used both by the
    ///     bulk <see cref="Deserialize" /> and for play-mode live tuning (push one inspector edit to the
    ///     running component).
    /// </summary>
    public static void DeserializeField(Component instance, ExportedField field, string json)
    {
        try
        {
            var value = JsonToValue(json, field.Kind);
            if (value != null) field.SetValue(instance, value);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ScriptSerializer] Could not restore '{field.Name}': {ex.Message}"
            );
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ValueToJson(object value, ExportedFieldKind kind)
    {
        return kind switch {
            ExportedFieldKind.Vec2 when value is Vec2 v =>
                $"{{\"x\":{v.X.ToString(CultureInfo.InvariantCulture)},\"y\":{v.Y.ToString(CultureInfo.InvariantCulture)}}}",
            ExportedFieldKind.Vec3 when value is Vec3 v =>
                $"{{\"x\":{v.X.ToString(CultureInfo.InvariantCulture)},\"y\":{v.Y.ToString(CultureInfo.InvariantCulture)},\"z\":{v.Z.ToString(CultureInfo.InvariantCulture)}}}",
            ExportedFieldKind.Color when value is Color c =>
                $"{{\"r\":{c.R.ToString(CultureInfo.InvariantCulture)},\"g\":{c.G.ToString(CultureInfo.InvariantCulture)},\"b\":{c.B.ToString(CultureInfo.InvariantCulture)},\"a\":{c.A.ToString(CultureInfo.InvariantCulture)}}}",
            // Source-gen'd primitive metadata — exported-field restore runs at play time, which under
            // NativeAOT has no reflection-based serializer.
            _ => JsonSerializer.Serialize(value, value.GetType(), ScriptJsonContext.Default),
        };
    }

    private static object? JsonToValue(string json, ExportedFieldKind kind)
    {
        switch (kind)
        {
            case ExportedFieldKind.Bool:
                return JsonSerializer.Deserialize(json, ScriptJsonContext.Default.Boolean);
            case ExportedFieldKind.Int:
                return JsonSerializer.Deserialize(json, ScriptJsonContext.Default.Int32);
            case ExportedFieldKind.Float:
                return JsonSerializer.Deserialize(json, ScriptJsonContext.Default.Single);
            case ExportedFieldKind.Double:
                return JsonSerializer.Deserialize(json, ScriptJsonContext.Default.Double);
            case ExportedFieldKind.String:
                return JsonSerializer.Deserialize(json, ScriptJsonContext.Default.String);
            case ExportedFieldKind.Vec2:
            {
                var n = JsonNode.Parse(json)!;
                return new Vec2(n["x"]!.GetValue<float>(), n["y"]!.GetValue<float>());
            }
            case ExportedFieldKind.Vec3:
            {
                var n = JsonNode.Parse(json)!;
                return new Vec3(
                    n["x"]!.GetValue<float>(),
                    n["y"]!.GetValue<float>(),
                    n["z"]!.GetValue<float>()
                );
            }
            case ExportedFieldKind.Color:
            {
                var n = JsonNode.Parse(json)!;
                return new Color(
                    n["r"]!.GetValue<float>(),
                    n["g"]!.GetValue<float>(),
                    n["b"]!.GetValue<float>(),
                    n["a"]!.GetValue<float>()
                );
            }
            default: return null;
        }
    }
}

[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string))]
internal partial class ScriptJsonContext : JsonSerializerContext;
