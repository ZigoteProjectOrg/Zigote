using Zigote.Core.Engine;

namespace Zigote.Game.Scene;

/// <summary>
///     Container for the 3D scene hierarchy.
///     The root node is a logical "World" node; all game objects are its descendants.
/// </summary>
public sealed class Scene3D
{
    public SceneNode3D Root { get; } = new("World");

    public SceneNode3D Add(string name, Node3DKind kind = Node3DKind.Empty,
        SceneNode3D? parent = null)
    {
        var node = new SceneNode3D(name: name, kind: kind) {
            Handle = ZigoteEngine.Instance!.SceneAddChildNode(
                parentHandle: 0,
                name: name,
                kind: (byte)kind
            ),
        };
        (parent ?? Root).AddChild(node);
        return node;
    }

    public SceneNode3D? Find(string name) => Root.Name == name
        ? Root
        : Root.Descendants().FirstOrDefault(n => n.Name == name);

    public void Remove(SceneNode3D node)
    {
        if (node.Handle != 0)
        {
            ZigoteEngine.Instance!.SceneRemoveNode(node.Handle);
            node.Handle = 0;
        }

        node.Parent?.RemoveChild(node);
    }

    public void Sync()
    {
        Root.Sync();
        foreach (var node in Root.Descendants()) node.Sync();
    }
}
