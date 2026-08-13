using Zigote.Graphs.Core;

namespace Zigote.Graphs.Commands;

public sealed class AddNodeCommand(GraphNode node, float x, float y) : IGraphCommand
{
    private readonly NodeLayoutData _layout = new() {
        X = x,
        Y = y,
        Width = 160,
        Height = 80,
    };

    public void Execute(GraphDocument graph)
    {
        graph.Nodes.Add(node);
        graph.EditorData.NodeLayouts[node.Id] = _layout;
    }

    public void Undo(GraphDocument graph)
    {
        graph.Nodes.RemoveAll(n => n.Id == node.Id);
        graph.EditorData.NodeLayouts.Remove(node.Id);
        // Remove any edges that were connected to this node
        graph.Edges.RemoveAll(e => e.From.NodeId == node.Id || e.To.NodeId == node.Id);
    }
}

public sealed class DeleteNodeCommand(Guid nodeId) : IGraphCommand
{
    private readonly List<GraphEdge> _removedEdges = [];
    private GraphNode? _removed;
    private NodeLayoutData? _removedLayout;

    public void Execute(GraphDocument graph)
    {
        _removed = graph.FindNode(nodeId);
        if (_removed is null) return;

        graph.Nodes.Remove(_removed);
        graph.EditorData.NodeLayouts.TryGetValue(key: nodeId, value: out _removedLayout);
        graph.EditorData.NodeLayouts.Remove(nodeId);

        _removedEdges.Clear();
        _removedEdges.AddRange(
            graph.Edges.Where(e => e.From.NodeId == nodeId || e.To.NodeId == nodeId)
        );
        foreach (var edge in _removedEdges) graph.Edges.Remove(edge);
    }

    public void Undo(GraphDocument graph)
    {
        if (_removed is null) return;
        graph.Nodes.Add(_removed);
        if (_removedLayout is not null) graph.EditorData.NodeLayouts[nodeId] = _removedLayout;
        graph.Edges.AddRange(_removedEdges);
    }
}

public sealed class MoveNodeCommand(Guid nodeId, float newX, float newY) : IGraphCommand
{
    private float _oldX, _oldY;

    public void Execute(GraphDocument graph)
    {
        if (!graph.EditorData.NodeLayouts.TryGetValue(key: nodeId, value: out var layout)) return;
        _oldX = layout.X;
        _oldY = layout.Y;
        layout.X = newX;
        layout.Y = newY;
    }

    public void Undo(GraphDocument graph)
    {
        if (graph.EditorData.NodeLayouts.TryGetValue(key: nodeId, value: out var layout))
        {
            layout.X = _oldX;
            layout.Y = _oldY;
        }
    }
}

public sealed class ResizeNodeCommand(
    Guid nodeId,
    float oldWidth,
    float newWidth,
    float oldHeight,
    float newHeight)
    : IGraphCommand
{
    public void Execute(GraphDocument graph)
    {
        if (graph.EditorData.NodeLayouts.TryGetValue(key: nodeId, value: out var layout))
        {
            layout.Width = newWidth;
            layout.Height = newHeight;
        }
    }

    public void Undo(GraphDocument graph)
    {
        if (graph.EditorData.NodeLayouts.TryGetValue(key: nodeId, value: out var layout))
        {
            layout.Width = oldWidth;
            layout.Height = oldHeight;
        }
    }
}
