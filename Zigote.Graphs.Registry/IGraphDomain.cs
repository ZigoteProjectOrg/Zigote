using Zigote.Graphs.Core;

namespace Zigote.Graphs.Registry;

/// <summary>
///     Contract every graph domain must implement.
///     The graph editor core calls through this interface — it never switches on domain IDs.
/// </summary>
public interface IGraphDomain
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Schema IDs this domain can handle, e.g. ["shader.material", "shader.post_process"].</summary>
    IReadOnlyList<string> SupportedSchemas { get; }

    IReadOnlyList<GraphTypeDefinition> GetTypeDefinitions();
    IReadOnlyList<NodeDefinition> GetNodeDefinitions();

    /// <summary>
    ///     Returns true if an edge can legally be drawn from <paramref name="from" /> to <paramref name="to" />.
    ///     Sets <paramref name="reason" /> to a user-facing message when returning false.
    /// </summary>
    bool CanCreateEdge(
        GraphDocument graph,
        GraphPinEndpoint from,
        GraphPinEndpoint to,
        out string? reason);

    GraphValidationResult Validate(GraphDocument graph);

    GraphCompileResult Compile(GraphDocument graph, GraphCompileContext context);
}

public sealed class GraphCompileContext
{
    public string TargetPlatform { get; init; } = "";
    public Dictionary<string, object> Options { get; } = new();
}

public sealed class GraphCompileResult
{
    public static readonly GraphCompileResult Failed = new() { Success = false };

    public bool Success { get; init; }
    public IReadOnlyList<GraphDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Domain-specific compiled artifact. Cast to the expected type (e.g., ShaderIR).</summary>
    public object? CompiledArtifact { get; init; }
}

/// <summary>Optional: domain provides a live preview rendered into the canvas.</summary>
public interface IGraphPreviewProvider
{
    void RenderPreview(GraphPreviewContext context);
}

public sealed class GraphPreviewContext
{
    public GraphDocument Graph { get; init; } = null!;
    public GraphNode? FocusedNode { get; init; }
}

/// <summary>Optional: domain can draw extra inspector UI for a graph or node.</summary>
public interface IGraphInspectorExtension
{
    void DrawGraphInspector(GraphDocument graph);
    void DrawNodeInspector(GraphNode node);
}
