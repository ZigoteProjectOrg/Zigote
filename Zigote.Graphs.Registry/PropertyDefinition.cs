using Zigote.Graphs.Core;

namespace Zigote.Graphs.Registry;

/// <summary>Declares an inspector-editable property on a node definition.</summary>
public sealed class PropertyDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public GraphTypeRef Type { get; init; }
    public GraphValue DefaultValue { get; init; } = GraphValue.Null();
    public string? Tooltip { get; init; }

    /// <summary>For numeric types: inclusive [Min, Max] range for the inspector slider.</summary>
    public float? Min { get; init; }

    public float? Max { get; init; }

    /// <summary>
    ///     When set on an <see cref="GraphTypeRef.Int" /> property, the inspector renders a dropdown of
    ///     these labels and stores the selected index. Used for operation/mode enums (Math op, Mix mode, …).
    /// </summary>
    public string[]? EnumLabels { get; init; }

    /// <summary>
    ///     Optional custom-editor hint the inspector honours (e.g. "gradient" → a gradient editor for a
    ///     String property). Keeps the generic panel domain-agnostic — it switches on the hint, not the node type.
    /// </summary>
    public string? Editor { get; init; }
}