using Zigote.Editor.History;
using Zigote.Editor.Scene;

namespace Zigote.Editor;

/// <summary>
///     Connects a scene node property to the editor's command history.
///     <para>
///         Reading <see cref="Value" /> returns the live property value. Calling
///         <see cref="Set" /> executes a <c>ChangePropertyCommand&lt;T&gt;</c> — one undo step
///         per discrete edit, coalesced into one entry during a scrub drag via
///         <see cref="BeginEdit" />/<see cref="EndEdit" />.
///     </para>
/// </summary>
public sealed class NodeBind<T>
{
    private readonly Func<T> _get;
    private readonly Action<T> _set;
    private readonly EditorState _state;

    internal NodeBind(EditorState state, Func<T> get, Action<T> set)
    {
        _state = state;
        _get = get;
        _set = set;
    }

    public T Value => _get();

    /// <summary>Execute a command that sets the property to <paramref name="newValue" /> (undoable).</summary>
    public void Set(T newValue)
    {
        _state.History.Execute(
            new ChangePropertyCommand<T>(
                _state,
                _get(),
                newValue,
                _set
            )
        );
    }

    /// <summary>
    ///     Start a coalescing interaction so subsequent <see cref="Set" /> calls merge into one undo
    ///     entry.
    /// </summary>
    public void BeginEdit()
    {
        _state.History.BeginInteraction();
    }

    /// <summary>End the coalescing interaction.</summary>
    public void EndEdit()
    {
        _state.History.EndInteraction();
    }

    /// <summary>
    ///     Set the property and run <paramref name="afterSet" /> on both Execute and Undo — for
    ///     properties whose change also requires a panel rebuild or secondary sync.
    /// </summary>
    public void SetWithSideEffect(T newValue, Action afterSet)
    {
        _state.History.Execute(
            new ChangePropertyCommand<T>(
                _state,
                _get(),
                newValue,
                v =>
                {
                    _set(v);
                    afterSet();
                }
            )
        );
    }
}

/// <summary>Factory helpers for <see cref="NodeBind{T}" />.</summary>
public static class NodeBind
{
    /// <summary>Create a binding from typed getter/setter lambdas on <paramref name="node" />.</summary>
    public static NodeBind<T> To<TNode, T>(EditorState state, TNode node,
        Func<TNode, T> getter, Action<TNode, T> setter)
    {
        return new NodeBind<T>(state, () => getter(node), v => setter(node, v));
    }
}