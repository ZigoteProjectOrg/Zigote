using Zigote.Graphs.Commands;
using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;

namespace Zigote.Graphs.Editor;

/// <summary>One connection policy shared by drag feedback, search, and final creation.</summary>
public static class GraphConnections
{
    public static (PinDefinition Pin, PinDirection Direction)? Resolve(GraphDocument graph,
        GraphDomainRegistry registry, GraphPinEndpoint endpoint)
    {
        var node = graph.FindNode(endpoint.NodeId);
        var def = node is null ? null : registry.GetNodeDefinition(node.DefinitionId);
        if (def is null) return null;
        foreach (var pin in def.Inputs)
        {
            if (pin.Id == endpoint.PinId)
                return (pin, PinDirection.Input);
        }

        foreach (var pin in def.Outputs)
        {
            if (pin.Id == endpoint.PinId)
                return (pin, PinDirection.Output);
        }

        return null;
    }

    public static bool CanConnect(GraphDocument graph, GraphDomainRegistry registry,
        GraphPinEndpoint a, GraphPinEndpoint b, out string reason, Guid? ignoreEdge = null)
    {
        reason = "";
        var pa = Resolve(graph: graph, registry: registry, endpoint: a);
        var pb = Resolve(graph: graph, registry: registry, endpoint: b);
        if (pa is null || pb is null)
        {
            reason = "This socket is no longer available.";
            return false;
        }

        if (ignoreEdge.HasValue && pa.Value.Direction != PinDirection.Output)
        {
            reason = "Stored cables must run from an output to an input.";
            return false;
        }

        if (a.NodeId == b.NodeId)
        {
            reason = "A node cannot connect to itself.";
            return false;
        }

        if (pa.Value.Direction == pb.Value.Direction)
        {
            reason = "Connect an output to an input.";
            return false;
        }

        if (pa.Value.Pin.Type != pb.Value.Pin.Type && pa.Value.Pin.Type != GraphTypeRef.Any &&
            pb.Value.Pin.Type != GraphTypeRef.Any)
        {
            reason = "These socket types are incompatible.";
            return false;
        }

        var from = pa.Value.Direction == PinDirection.Output ? a : b;
        var to = pa.Value.Direction == PinDirection.Input ? a : b;
        if (graph.Edges.Any(e => e.Id != ignoreEdge && e.From == from && e.To == to))
        {
            reason = "These sockets are already connected.";
            return false;
        }

        var input = pa.Value.Direction == PinDirection.Input ? pa.Value.Pin : pb.Value.Pin;
        if (!input.AllowsMultipleConnections &&
            graph.Edges.Any(e => e.Id != ignoreEdge && e.To == to))
        {
            reason = "This input already has a connection. Disconnect it first.";
            return false;
        }

        if (registry.TryGetDomain(domainId: graph.DomainId, domain: out var domain) &&
            domain is not null
            && !domain.CanCreateEdge(
                graph: graph,
                from: from,
                to: to,
                reason: out string? explanation
            ))
        {
            reason = explanation ?? "This connection is not allowed.";
            return false;
        }

        return true;
    }

    // Never temporarily insert a candidate into the live document: validation/search must be read-only.
    public static GraphDocument WithCandidate(GraphDocument graph, GraphNode candidate)
    {
        var preview = new GraphDocument {
            DomainId = graph.DomainId,
            SchemaId = graph.SchemaId,
            Name = graph.Name,
            FormatVersion = graph.FormatVersion,
            SchemaVersion = graph.SchemaVersion,
        };
        preview.Nodes.AddRange(graph.Nodes);
        preview.Nodes.Add(candidate);
        preview.Edges.AddRange(graph.Edges);
        preview.Parameters.AddRange(graph.Parameters);
        foreach (var pair in graph.Metadata) preview.Metadata[pair.Key] = pair.Value;
        return preview;
    }
}

/// <summary>A user gesture is one undo step and one compile, including add-and-connect.</summary>
internal sealed class GraphGesture(params IGraphCommand[] commands) : IGraphCommand
{
    public void Execute(GraphDocument graph)
    {
        foreach (var command in commands) command.Execute(graph);
    }

    public void Undo(GraphDocument graph)
    {
        for (int i = commands.Length - 1; i >= 0; i--) commands[i].Undo(graph);
    }
}

internal sealed class CollapseNodesCommand(IEnumerable<Guid> nodes, bool collapsed) : IGraphCommand
{
    private readonly Guid[] _nodes = [.. nodes];
    private readonly Dictionary<Guid, bool> _old = [];

    public void Execute(GraphDocument graph)
    {
        foreach (var id in _nodes)
        {
            if (graph.EditorData.NodeLayouts.TryGetValue(key: id, value: out var layout))
            {
                _old[id] = layout.IsCollapsed;
                layout.IsCollapsed = collapsed;
            }
        }
    }

    public void Undo(GraphDocument graph)
    {
        foreach (var pair in _old)
        {
            if (graph.EditorData.NodeLayouts.TryGetValue(key: pair.Key, value: out var layout))
                layout.IsCollapsed = pair.Value;
        }
    }
}
