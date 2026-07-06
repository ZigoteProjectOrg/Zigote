using Zigote.Core.Math3D;

namespace Zigote.Ecs.Scene;

/// <summary>
///     Canonical local transform component. During play this is the source of truth for a node's
///     placement (scripts + physics write it); the editor's SceneNode mirrors it for rendering.
///     Blittable POD (Vec3 = 3 floats, Quat = 4 floats) so flecs stores it inline.
/// </summary>
public struct Transform
{
    public Vec3 Position;
    public Quat Rotation;
    public Vec3 Scale;

    public static Transform Identity => new() {
        Position = Vec3.Zero,
        Rotation = Quat.Identity,
        Scale = Vec3.One,
    };
}