using Zigote.Graphs.Core;

namespace Zigote.Graphs.Commands;

/// <summary>All graph mutations go through this interface to keep undo/redo and dirty tracking consistent.</summary>
public interface IGraphCommand
{
    void Execute(GraphDocument graph);
    void Undo(GraphDocument graph);
}
