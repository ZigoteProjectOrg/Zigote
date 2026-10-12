using Zigote.Graphs.Core;

namespace Zigote.Graphs.Registry;

/// <summary>Domain-independent checks for required input connections and declared defaults.</summary>
public static class GraphInputValidation
{
    public static bool IsMissing(GraphDocument graph, GraphDomainRegistry registry,
        GraphNode node, PinDefinition pin)
    {
        if (!pin.IsRequired || pin.DefaultValue is { IsNull: false }) return false;
        return !graph.Edges.Any(edge =>
        {
            if (edge.To.NodeId != node.Id || edge.To.PinId != pin.Id) return false;
            var source = graph.FindNode(edge.From.NodeId);
            if (source is null) return false;
            return registry.GetNodeDefinition(source.DefinitionId)?.Outputs.Any(p => p.Id == edge.From.PinId) == true
                || source.DynamicPins.Any(p => p.Direction == PinDirection.Output && p.PinId == edge.From.PinId);
        });
    }

    public static IEnumerable<GraphDiagnostic> Validate(GraphDocument graph, GraphDomainRegistry registry)
    {
        foreach (var node in graph.Nodes)
        {
            var definition = registry.GetNodeDefinition(node.DefinitionId);
            if (definition is null) continue;
            foreach (var pin in definition.Inputs)
                if (IsMissing(graph, registry, node, pin))
                    yield return new GraphDiagnostic {
                        Code = "GRAPH_REQUIRED_INPUT",
                        Severity = GraphDiagnosticSeverity.Error,
                        NodeId = node.Id,
                        PinId = pin.Id,
                        DomainId = graph.DomainId,
                        Message = $"Required input '{pin.DisplayName}' has no connection or default value.",
                    };
        }
    }
}
