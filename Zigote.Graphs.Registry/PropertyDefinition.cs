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

    /// <summary>Numeric scrub/slider increment. Defaults to 0.01 for floats and 1 for integers.</summary>
    public float? Step { get; init; }

    /// <summary>Optional display suffix, such as Hz, dB, ms, or %.</summary>
    public string? Unit { get; init; }

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

    /// <summary>
    ///     Keeps the property off the canvas node card; it stays editable in the inspector.
    ///     For setup values (e.g. MIDI mappings) that would bury a node's everyday controls.
    /// </summary>
    public bool InspectorOnly { get; init; }

    /// <summary>Editor hint for a Bool property that bypasses its node; the canvas dims the node while true.</summary>
    public const string BypassEditor = "bypass";
}
