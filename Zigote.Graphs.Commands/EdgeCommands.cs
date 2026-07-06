using Zigote.Graphs.Core;

namespace Zigote.Graphs.Commands;

public sealed class AddEdgeCommand(GraphEdge edge) : IGraphCommand
{
    public void Execute(GraphDocument graph)
    {
        graph.Edges.Add(edge);
    }

    public void Undo(GraphDocument graph)
    {
        graph.Edges.RemoveAll(e => e.Id == edge.Id);
    }
}

public sealed class DeleteEdgeCommand(Guid edgeId) : IGraphCommand
{
    private GraphEdge? _removed;

    public void Execute(GraphDocument graph)
    {
        _removed = graph.Edges.Find(e => e.Id == edgeId);
        if (_removed is not null) graph.Edges.Remove(_removed);
    }

    public void Undo(GraphDocument graph)
    {
        if (_removed is not null) graph.Edges.Add(_removed);
    }
}