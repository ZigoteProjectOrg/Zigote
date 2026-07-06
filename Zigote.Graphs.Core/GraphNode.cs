namespace Zigote.Graphs.Core;

/// <summary>
///     A node instance inside a graph document. References a domain-registered definition by ID.
///     Does not duplicate the full definition — only stores instance-specific state.
/// </summary>
public sealed class GraphNode
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Stable ID that maps to a <c>NodeDefinition</c> in the domain registry.</summary>
    public string DefinitionId { get; set; } = "";

    /// <summary>Definition schema version at the time this node was created. Used for migration.</summary>
    public int DefinitionVersion { get; set; } = 1;

    /// <summary>Instance-specific property overrides (e.g., constant values on a Constant node).</summary>
    public Dictionary<string, GraphValue> Properties { get; } = new();

    /// <summary>
    ///     Extra pins added at runtime by nodes that support dynamic pin counts
    ///     (e.g., Make Struct, Sequence, Switch).
    /// </summary>
    public List<GraphDynamicPin> DynamicPins { get; } = [];

    /// <summary>Domain-owned per-node metadata (e.g., inline comments, preview state).</summary>
    public Dictionary<string, GraphValue> Metadata { get; } = new();
}

/// <summary>A pin that is added dynamically to a node instance rather than declared in its definition.</summary>
public sealed class GraphDynamicPin
{
    /// <summary>Stable pin ID within this node instance.</summary>
    public string PinId { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public PinDirection Direction { get; set; }
    public GraphTypeRef Type { get; set; }
}

public enum PinDirection
{
    Input,
    Output,
}