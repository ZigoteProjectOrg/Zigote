using Zigote.Core.Math3D;

namespace Zigote.Ecs.Scene;

/// <summary>
///     The minimal scene-node surface the <see cref="EcsSceneBridge" /> needs to mirror a node tree to
///     flecs entities and back. The editor's SceneNode implements this; tests use a lightweight fake.
///     Keeping the bridge generic over this interface keeps Zigote.ECS free of editor types and lets the
///     hand-off be unit-tested headlessly (Zigote.Tests cannot reference Zigote.Editor).
/// </summary>
public interface IEcsSceneNode
{
    /// <summary>Stable per-node id (the bridge keys its entity map on this).</summary>
    int Id { get; }

    string Name { get; }

    Vec3 Position { get; set; }
    Quat Rotation { get; set; }
    Vec3 Scale { get; set; }

    IReadOnlyList<IEcsSceneNode> Children { get; }
}