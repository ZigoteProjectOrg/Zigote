using Zigote.Graphs.Core;

namespace Zigote.Graphs.Commands;

public sealed class ChangeNodePropertyCommand(
    Guid nodeId,
    string propertyKey,
    GraphValue oldValue,
    GraphValue newValue)
    : IGraphCommand
{
    public void Execute(GraphDocument graph)
    {
        var node = graph.FindNode(nodeId);
        if (node is null) return;
        node.Properties[propertyKey] = newValue;
    }

    public void Undo(GraphDocument graph)
    {
        var node = graph.FindNode(nodeId);
        if (node is null) return;
        if (oldValue.IsNull) node.Properties.Remove(propertyKey);
        else node.Properties[propertyKey] = oldValue;
    }
}

public sealed class AddParameterCommand(GraphParameter parameter) : IGraphCommand
{
    public void Execute(GraphDocument graph) => graph.Parameters.Add(parameter);

    public void Undo(GraphDocument graph) => graph.Parameters.RemoveAll(p => p.Id == parameter.Id);
}

public sealed class DeleteParameterCommand(Guid parameterId) : IGraphCommand
{
    private GraphParameter? _removed;

    public void Execute(GraphDocument graph)
    {
        _removed = graph.Parameters.Find(p => p.Id == parameterId);
        if (_removed is not null) graph.Parameters.Remove(_removed);
    }

    public void Undo(GraphDocument graph)
    {
        if (_removed is not null) graph.Parameters.Add(_removed);
    }
}

public sealed class RenameParameterCommand(Guid parameterId, string oldName, string newName)
    : IGraphCommand
{
    public void Execute(GraphDocument graph)
    {
        var p = graph.Parameters.Find(p => p.Id == parameterId);
        if (p is not null) p.Name = newName;
    }

    public void Undo(GraphDocument graph)
    {
        var p = graph.Parameters.Find(p => p.Id == parameterId);
        if (p is not null) p.Name = oldName;
    }
}
