using Zigote.Core.Math3D;
using Zigote.Core.Physics;

namespace Zigote.Game.Scene;

public enum Node3DKind
{
    Empty,
    Mesh,
    Light,
    Camera,
    Script,
}

public enum ProjectionKind
{
    Perspective,
    Orthographic,
}

public enum LightKind
{
    Directional,
    Point,
    Spot,
}

public enum RenderLayer
{
    World3D,
    Scene2D,
}

public sealed class Camera3D
{
    public float FovyDegrees { get; set; } = 60f;
    public float Near { get; set; } = 0.1f;
    public float Far { get; set; } = 1000f;
    public ProjectionKind Kind { get; set; } = ProjectionKind.Perspective;
    public Vec2 OrthographicSize { get; set; } = new(2f, 2f);

    public Mat4 ProjMatrix(float aspect)
    {
        return Kind switch {
            ProjectionKind.Perspective => Mat4.PerspectiveRhZo(
                FovyDegrees * (MathF.PI / 180f),
                aspect,
                Near,
                Far
            ),
            _ => Mat4.OrthographicRhZo(
                -OrthographicSize.X * 0.5f,
                OrthographicSize.X * 0.5f,
                -OrthographicSize.Y * 0.5f,
                OrthographicSize.Y * 0.5f,
                Near,
                Far
            ),
        };
    }
}

public sealed class MeshRenderer3D
{
    public int MeshHandle { get; set; } = -1;
    public int MaterialHandle { get; set; } = -1;
    public bool Visible { get; set; } = true;
    public RenderLayer Layer { get; set; } = RenderLayer.World3D;
}

public sealed class Light3D
{
    public LightKind Kind { get; set; } = LightKind.Directional;
    public Vec3 Color { get; set; } = Vec3.One;
    public float Intensity { get; set; } = 1f;
    public float Range { get; set; } = 10f;
    public float InnerAngle { get; set; } = 0.3f;
    public float OuterAngle { get; set; } = 0.5f;
    public bool CastShadows { get; set; }
}

public sealed class RigidBody3D
{
    public PhysicsShapeType ShapeType { get; set; } = PhysicsShapeType.Box;
    public Vec3 HalfExtents { get; set; } = new(0.5f, 0.5f, 0.5f);
    public float Mass { get; set; } = 1f;
    public bool IsStatic { get; set; }
    public bool UseGravity { get; set; } = true;
    public float Friction { get; set; } = 0.2f;
    public float Restitution { get; set; } = 0f;

    // Runtime tracking — assigned by World.AttachPhysics, not serialized
    public uint BodyId { get; internal set; } = PhysicsWorld.InvalidBodyId;
}