using Zigote.Graphs.Core;

namespace Zigote.Graphs.Commands;

/// <summary>Undo/redo stack scoped to a single <see cref="GraphDocument" />.</summary>
public sealed class GraphCommandStack
{
    private readonly GraphDocument _graph;
    private readonly Stack<IGraphCommand> _redo = new();
    private readonly Stack<IGraphCommand> _undo = new();

    // The command on top of the undo stack at the last save. It uniquely identifies the applied state
    // (the undo stack is a path), so undoing or redoing back to this point reports clean — even after a
    // new branch replaced the old redo history. Null means "saved at the empty document".
    private IGraphCommand? _savedTop;

    public GraphCommandStack(GraphDocument graph) => _graph = graph;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>True when the applied state differs from the last <see cref="MarkClean" /> checkpoint.</summary>
    public bool IsDirty => CurrentTop() != _savedTop;

    public event Action? Changed;

    private IGraphCommand? CurrentTop() => _undo.Count > 0 ? _undo.Peek() : null;

    public void Execute(IGraphCommand command)
    {
        command.Execute(_graph);
        _undo.Push(command);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var cmd = _undo.Pop();
        cmd.Undo(_graph);
        _redo.Push(cmd);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var cmd = _redo.Pop();
        cmd.Execute(_graph);
        _undo.Push(cmd);
        Changed?.Invoke();
    }

    public void MarkClean() => _savedTop = CurrentTop();

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _savedTop = null;
    }
}
