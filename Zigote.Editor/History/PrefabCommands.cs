using Zigote.Core.Assets;
using Zigote.Editor.Scene;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.History;

/// <summary>
///     Turn a node into a prefab asset (written once on first Execute) and link the source node to it
///     as
///     the first instance. Undo unlinks the node; the <c>.prefab</c> file + registry entry are kept (a
///     re-Execute re-links without rewriting), matching how asset creation is not itself undone.
/// </summary>
public sealed class CreatePrefabCommand(EditorState state, SceneNode source) : ICommand
{
    private readonly AssetId _oldSource = source.PrefabSource;
    private AssetId _created = AssetId.Empty;

    public AssetId PrefabId => _created;

    public void Execute()
    {
        if (_created.IsEmpty) _created = state.Prefabs.CreatePrefab(source);
        source.PrefabSource = _created;
        state.SaveAssets();
        state.NotifyAssetsChanged();
        state.NotifySceneChanged();
    }

    public void Undo()
    {
        source.PrefabSource = _oldSource;
        state.NotifySceneChanged();
    }

    public bool TryMergeWith(ICommand other)
    {
        return false;
    }
}

/// <summary>
///     Instantiate a prefab asset as a new subtree under <paramref name="parent" /> (built once,
///     reused
///     across redo). Mirrors <see cref="AddNodeCommand" /> for lifecycle.
/// </summary>
public sealed class InstantiatePrefabCommand(EditorState state, AssetId prefab, SceneNode parent)
    : ICommand
{
    public SceneNode? Node { get; private set; }

    public void Execute()
    {
        Node ??= state.Prefabs.InstantiateNode(prefab);
        if (Node is null) return;
        parent.AddChild(Node);
        state.Select(Node);
        state.NotifySceneChanged();
    }

    public void Undo()
    {
        if (Node is null) return;
        Node.RemoveFromNative();
        parent.RemoveChild(Node);
        if (state.Selected == Node) state.Select(null);
        state.NotifySceneChanged();
    }

    public bool TryMergeWith(ICommand other)
    {
        return false;
    }
}
