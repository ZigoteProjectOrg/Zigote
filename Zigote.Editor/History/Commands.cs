using Zigote.Editor.Scene;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.History;

public class AddNodeCommand(EditorState state, SceneNode parent, SceneNode node) : ICommand
{
    public void Execute()
    {
        parent.AddChild(node);
        state.Select(node);
        state.NotifySceneChanged();
    }

    public void Undo()
    {
        parent.RemoveChild(node);
        if (state.Selected == node) state.Select(null);
        state.NotifySceneChanged();
    }

    public bool TryMergeWith(ICommand other)
    {
        return false;
    }
}

public class DeleteNodeCommand : ICommand
{
    private readonly int _index;
    private readonly SceneNode _node;
    private readonly SceneNode _parent;
    private readonly EditorState _state;
    private readonly bool _wasSelected;

    public DeleteNodeCommand(EditorState state, SceneNode node)
    {
        _state = state;
        _node = node;
        _parent = node.Parent!;
        _index = _parent.Children.IndexOf(node);
        _wasSelected = _state.Selected == node;
    }

    public void Execute()
    {
        // Free the native scene objects for the whole subtree (handles zeroed), then detach in C#.
        // Undo re-inserts and NotifySceneChanged re-creates the native nodes from the zeroed handles.
        _node.RemoveFromNative();
        _parent.RemoveChild(_node);
        if (_wasSelected) _state.Select(null);
        _state.NotifySceneChanged();
    }

    public void Undo()
    {
        _node.Parent = null; // Reset to allow AddChild
        _parent.Children.Insert(_index, _node);
        _node.Parent = _parent;
        if (_wasSelected) _state.Select(_node);
        _state.NotifySceneChanged();
    }

    public bool TryMergeWith(ICommand other)
    {
        return false;
    }
}

public class ReparentNodeCommand : ICommand
{
    private readonly SceneNode _newParent;
    private readonly SceneNode _node;
    private readonly int _oldIndex;
    private readonly SceneNode _oldParent;
    private readonly EditorState _state;

    public ReparentNodeCommand(EditorState state, SceneNode node, SceneNode newParent)
    {
        _state = state;
        _node = node;
        _oldParent = node.Parent!;
        _oldIndex = _oldParent.Children.IndexOf(node);
        _newParent = newParent;
    }

    public void Execute()
    {
        _oldParent.RemoveChild(_node);
        _newParent.AddChild(_node);
        _state.NotifySceneChanged();
    }

    public void Undo()
    {
        _newParent.RemoveChild(_node);
        _node.Parent = null;
        _oldParent.Children.Insert(_oldIndex, _node);
        _node.Parent = _oldParent;
        _state.NotifySceneChanged();
    }

    public bool TryMergeWith(ICommand other)
    {
        return false;
    }
}

public class ChangePropertyCommand<T>(EditorState state, T oldValue, T newValue, Action<T> setter)
    : ICommand
{
    // Mutable so a drag-scrub can coalesce later ticks into this single command (keeps oldValue,
    // adopts each successive newValue). See CommandHistory.BeginInteraction/Execute.
    private T _new = newValue;

    public void Execute()
    {
        setter(_new);
        state.NotifySceneChanged();
    }

    public void Undo()
    {
        setter(oldValue);
        state.NotifySceneChanged();
    }

    public bool TryMergeWith(ICommand other)
    {
        if (other is ChangePropertyCommand<T> c)
        {
            _new = c._new;
            return true;
        }

        return false;
    }
}

/// <summary>
///     Applies a batch of mutations as a single undo step, firing one <see cref="EditorState.NotifySceneChanged" />
///     for the whole batch (one native sync + one relayout). Used for multi-field / multi-node edits like
///     applying a material preset to a model and all its sub-meshes.
/// </summary>
public sealed class CompositeCommand(EditorState state, Action apply, Action revert) : ICommand
{
    public void Execute()
    {
        apply();
        state.NotifySceneChanged();
    }

    public void Undo()
    {
        revert();
        state.NotifySceneChanged();
    }

    public bool TryMergeWith(ICommand other)
    {
        return false;
    }
}