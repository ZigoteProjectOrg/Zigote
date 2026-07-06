using Zigote.Graphs.Core;

namespace Zigote.Graphs.Registry;

/// <summary>
///     Immutable descriptor for a node type registered by a domain.
///     Node instances (<see cref="GraphNode" />) reference this by <see cref="Id" />.
/// </summary>
public sealed class NodeDefinition
{
    /// <summary>Stable domain-namespaced ID, e.g. "shader.math.add", "logic.flow.branch".</summary>
    public string Id { get; init; } = "";

    public int Version { get; init; } = 1;

    public string DomainId { get; init; } = "";

    /// <summary>Schema the node belongs to within the domain, e.g. "shader.material".</summary>
    public string SchemaId { get; init; } = "";

    public string DisplayName { get; init; } = "";

    /// <summary>Category path for the search menu, e.g. "Math/Trigonometry".</summary>
    public string Category { get; init; } = "";

    public string Description { get; init; } = "";

    public IReadOnlyList<PinDefinition> Inputs { get; init; } = [];
    public IReadOnlyList<PinDefinition> Outputs { get; init; } = [];
    public IReadOnlyList<PropertyDefinition> Properties { get; init; } = [];

    public string[] Tags { get; init; } = [];
    public NodeCapabilities Capabilities { get; init; }
}

[Flags]
public enum NodeCapabilities
{
    None = 0,
    DynamicInputs = 1 << 0,
    DynamicOutputs = 1 << 1,
    Collapsible = 1 << 2,
    Previewable = 1 << 3,
    Deprecated = 1 << 4,
}