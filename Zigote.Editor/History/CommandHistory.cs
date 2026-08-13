namespace Zigote.Editor.History;

/// <summary>
///     Manages the undo and redo stacks for editor actions.
/// </summary>
public sealed class CommandHistory
{
    private readonly Stack<ICommand> _redo = new();
    private readonly Stack<ICommand> _undo = new();

    private bool _interacting;
    private bool _interactionPushed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    ///     Begin a coalescing interaction (e.g. a drag-scrub). The first command pushed during the
    ///     interaction becomes the single undo entry; subsequent compatible commands merge into it.
    /// </summary>
    public void BeginInteraction()
    {
        _interacting = true;
        _interactionPushed = false;
    }

    /// <summary>End a coalescing interaction; the next command pushes normally again.</summary>
    public void EndInteraction() => _interacting = false;

    public void Execute(ICommand command)
    {
        command.Execute();

        if (_interacting && _interactionPushed && _undo.Count > 0 &&
            _undo.Peek().TryMergeWith(command))
        {
            // Absorbed into the interaction's first command — do not push a new entry.
        }
        else
        {
            _undo.Push(command);
            if (_interacting) _interactionPushed = true;
        }

        _redo.Clear();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var cmd = _undo.Pop();
        cmd.Undo();
        _redo.Push(cmd);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var cmd = _redo.Pop();
        cmd.Execute();
        _undo.Push(cmd);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
