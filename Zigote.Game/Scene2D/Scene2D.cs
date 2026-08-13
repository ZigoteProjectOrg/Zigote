using Zigote.Core.Math3D;
using Zigote.Game.Resources;
using Zigote.Game.Scene;

namespace Zigote.Game.Scene2D;

public sealed class SpriteOptions
{
    public string Name { get; set; } = "sprite";
    public Vec2 Position { get; set; }
    public Vec2 Size { get; set; } = new(1, 1);

    public Vec4 Color { get; set; } = new(
        1,
        1,
        1,
        1
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
            new Vec3(opts.Position.X, opts.Position.Y, opts.Z),
            Quat.Identity,
            new Vec3(opts.Size.X, opts.Size.Y, 1f)
        );

        var mesh = Mesh3D.CreateQuad();
        mesh.Name = opts.Name;
        var meshHandle = world.AddMesh(mesh);

        Material3D material;
        if (opts.ImagePixels is { } pixels)
        {
            material = Material3D.FromPixels(
                opts.Name,
                pixels,
                opts.ImageWidth,
                opts.ImageHeight
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
        var matHandle = world.AddMaterial(material);

        node.MeshRenderer = new MeshRenderer3D {
            MeshHandle = meshHandle,
            MaterialHandle = matHandle,
            Layer = RenderLayer.Scene2D,
        };

        return node;
    }
}
