using Zigote.Core.Math3D;
using Zigote.Game.Resources;
using Zigote.Game.Scene;

namespace Zigote.Game.Scene2D;

public sealed class SpriteOptions
{
    public string Name { get; set; } = "sprite";
    public Vec2 Position { get; set; }
    public Vec2 Size { get; set; } = new(x: 1, y: 1);

    public Vec4 Color { get; set; } = new(
        x: 1,
        y: 1,
        z: 1,
        w: 1
    );

    public float Z { get; set; }
    public byte[]? ImagePixels { get; set; }
    public uint ImageWidth { get; set; }
    public uint ImageHeight { get; set; }
    public RenderEffect Effect { get; set; } = RenderEffect.Standard;
}

public static class Scene2D
{
    /// <summary>Orthographic camera for 2D scenes.</summary>
    public static Camera3D OrthographicCamera(Vec2 viewportSize, float near = -1f, float far = 100f)
    {
        return new Camera3D {
            Kind = ProjectionKind.Orthographic,
            OrthographicSize = viewportSize,
            Near = near,
            Far = far,
        };
    }

    /// <summary>Spawns a 2D sprite as a scene node with a quad mesh and flat or textured material.</summary>
    public static SceneNode3D SpawnSprite(World world, SpriteOptions opts)
    {
        var node = world.CreateNode(opts.Name);
        node.LocalTransform = new Transform3D(
            position: new Vec3(x: opts.Position.X, y: opts.Position.Y, z: opts.Z),
            rotation: Quat.Identity,
            scale: new Vec3(x: opts.Size.X, y: opts.Size.Y, z: 1f)
        );

        var mesh = Mesh3D.CreateQuad();
        mesh.Name = opts.Name;
        int meshHandle = world.AddMesh(mesh);

        Material3D material;
        if (opts.ImagePixels is { } pixels)
        {
            material = Material3D.FromPixels(
                name: opts.Name,
                pixels: pixels,
                width: opts.ImageWidth,
                height: opts.ImageHeight
            );
            material.EmissiveFactor = Vec3.One;
            material.RoughnessFactor = 1f;
        }
        else
        {
            material = Material3D.Flat(opts.Color);
            material.EmissiveFactor = opts.Color.Xyz();
        }

        material.Effect = opts.Effect;
        material.Name = opts.Name;
        int matHandle = world.AddMaterial(material);

        node.MeshRenderer = new MeshRenderer3D {
            MeshHandle = meshHandle,
            MaterialHandle = matHandle,
            Layer = RenderLayer.Scene2D,
        };

        return node;
    }
}
