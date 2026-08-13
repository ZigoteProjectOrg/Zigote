namespace Zigote.Graphs.Core;

/// <summary>
///     The editable source asset for any graph domain.
///     Serialized as .zggraph. Runtime uses compiled outputs, not this object.
/// </summary>
public sealed class GraphDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>
    ///     Identifies which domain owns this graph (e.g., "zigote.shader", "zigote.render").
    ///     Never use an enum here — new domains must not require changing this class.
    /// </summary>
    public string DomainId { get; set; } = "";

    /// <summary>
    ///     Identifies the schema within the domain (e.g., "shader.material", "render.frame").
    /// </summary>
    public string SchemaId { get; set; } = "";

    public int FormatVersion { get; set; } = 1;
    public int SchemaVersion { get; set; } = 1;

    public List<GraphNode> Nodes { get; } = [];
    public List<GraphEdge> Edges { get; } = [];
    public List<GraphParameter> Parameters { get; } = [];

    /// <summary>Domain-owned graph-level metadata.</summary>
    public Dictionary<string, GraphValue> Metadata { get; } = new();

    /// <summary>Editor-only layout data. Stripped from runtime builds.</summary>
    public GraphEditorData EditorData { get; set; } = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    public GraphNode? FindNode(Guid id) => Nodes.Find(n => n.Id == id);

    public IEnumerable<GraphEdge> EdgesFrom(Guid nodeId) =>
        Edges.Where(e => e.From.NodeId == nodeId);

    public IEnumerable<GraphEdge> EdgesTo(Guid nodeId) => Edges.Where(e => e.To.NodeId == nodeId);

    public IEnumerable<GraphEdge> EdgesAtPin(Guid nodeId, string pinId)
    {
        return Edges.Where(e =>
            (e.From.NodeId == nodeId && e.From.PinId == pinId) ||
            (e.To.NodeId == nodeId && e.To.PinId == pinId)
        );
    }
}
