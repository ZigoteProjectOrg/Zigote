using System.Reflection;
using Zigote.Scripting.Serialization;

namespace Zigote.Scripting.Metadata;

/// <summary>Cached metadata about a single <see cref="Component" /> subtype.</summary>
public sealed class ScriptMetadata
{
    private IReadOnlyDictionary<string, string>? _defaultExports;
    public required Type Type { get; init; }
    public required string DisplayName { get; init; }
    public ExportedField[] ExportedFields { get; init; } = [];

    public string FullName => Type.FullName ?? Type.Name;

    /// <summary>
    ///     Default values of the exported fields (fieldName → JSON), read once from a freshly
    ///     constructed instance and cached. The inspector falls back to these when a node has no stored
    ///     override, so a newly-attached script shows its real defaults instead of zeros.
    /// </summary>
    public IReadOnlyDictionary<string, string> DefaultExports =>
        _defaultExports ??= ComputeDefaultExports();

    private IReadOnlyDictionary<string, string> ComputeDefaultExports()
    {
        try
        {
            if (Activator.CreateInstance(Type) is Component instance)
                return ScriptSerializer.Serialize(instance: instance, meta: this);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ScriptMeta] Could not read defaults for '{DisplayName}': {ex.Message}"
            );
        }

        return new Dictionary<string, string>();
    }

    /// <summary>Build metadata from reflection. Called once per type at load time.</summary>
    public static ScriptMetadata From(Type type)
    {
        return new ScriptMetadata {
            Type = type,
            DisplayName = type.Name,
            ExportedFields = DiscoverExports(type),
        };
    }

    private static ExportedField[] DiscoverExports(Type type)
    {
        var result = new List<ExportedField>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<ExportAttribute>() is null) continue;
            var kind = ResolveKind(prop.PropertyType);
            if (kind is null) continue;
            if (!prop.CanRead || !prop.CanWrite) continue;

            result.Add(
                new ExportedField {
                    Name = prop.Name,
                    DisplayName = prop.GetCustomAttribute<EditorNameAttribute>()?.Name ?? prop.Name,
                    Tooltip = prop.GetCustomAttribute<EditorTooltipAttribute>()?.Tooltip ?? "",
                    Kind = kind.Value,
                    RangeMin = prop.GetCustomAttribute<EditorRangeAttribute>()?.Min,
                    RangeMax = prop.GetCustomAttribute<EditorRangeAttribute>()?.Max,
                    IsColor = prop.GetCustomAttribute<EditorColorAttribute>() != null,
                    Property = prop,
                }
            );
        }

        return result.ToArray();
    }

    private static ExportedFieldKind? ResolveKind(Type t)
    {
        if (t == typeof(bool)) return ExportedFieldKind.Bool;
        if (t == typeof(int)) return ExportedFieldKind.Int;
        if (t == typeof(float)) return ExportedFieldKind.Float;
        if (t == typeof(double)) return ExportedFieldKind.Double;
        if (t == typeof(string)) return ExportedFieldKind.String;
        string name = t.FullName ?? t.Name;
        if (name.EndsWith("Vec2")) return ExportedFieldKind.Vec2;
        if (name.EndsWith("Vec3")) return ExportedFieldKind.Vec3;
        if (name.EndsWith("Color")) return ExportedFieldKind.Color;
        return null;
    }
}
