using System.Reflection;

namespace Zigote.Scripting.Metadata;

public enum ExportedFieldKind
{
    Bool,
    Int,
    Float,
    Double,
    String,
    Vec2,
    Vec3,
    Color,
}

/// <summary>Metadata for a single [Export]-marked property.</summary>
public sealed class ExportedField
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string Tooltip { get; init; } = "";
    public ExportedFieldKind Kind { get; init; }
    public double? RangeMin { get; init; }
    public double? RangeMax { get; init; }
    public bool IsColor { get; init; }
    public required PropertyInfo Property { get; init; }

    public object? GetValue(Component instance) => Property.GetValue(instance);

    public void SetValue(Component instance, object? value)
    {
        try
        {
            Property.SetValue(obj: instance, value: value);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ScriptMeta] SetValue '{Name}' failed: {ex.Message}");
        }
    }
}
