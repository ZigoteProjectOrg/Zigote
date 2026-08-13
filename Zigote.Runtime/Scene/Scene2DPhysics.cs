using System.Text.Json;
using Zigote.Core.Math3D;
using Zigote.Physics2D;
using Zigote.Render2D;

namespace Zigote.Runtime.Scene;

/// <summary>
///     One collider produced by baking the scene: a box (<paramref name="HalfExtents" />) or a circle
///     (<paramref name="Radius" />), already in world space. <paramref name="Source" /> is the node it
///     came from — tile runs carry their tilemap node.
/// </summary>
public readonly record struct Baked2DShape(
    bool IsCircle,
    Vec2 Center,
    Vec2 HalfExtents,
    float Radius,
    uint Layer,
    bool IsTrigger,
    bool OneWayUp,
    SceneNode Source);

/// <summary>
///     Builds a <see cref="CollisionWorld2D" /> from the authored scene: per-node box/circle
///     colliders plus the solid tiles of every <see cref="NodeKind.Tilemap" />.
///     <para>
///         Pure and headless — no GPU, no engine — so play mode, game export and tests all bake the
///         same world, and the editor can call it to draw exactly the shapes physics will see.
///     </para>
/// </summary>
public static class Scene2DPhysics
{
    /// <summary>
    ///     Add every authored 2D collider under <paramref name="root" /> to <paramref name="world" />.
    ///     Each collider's <c>UserData</c> is the <see cref="SceneNode" /> that produced it (tile runs
    ///     carry their tilemap node), so hits can be traced back to the scene.
    /// </summary>
    public static void Build(SceneNode root, CollisionWorld2D world,
        Func<string?, Tileset?>? tilesetLoader = null)
    {
        Bake(
            root: root,
            tilesetLoader: tilesetLoader,
            emit: shape =>
            {
                if (shape.IsCircle)
                {
                    world.AddCircle(
                        center: shape.Center,
                        radius: shape.Radius,
                        layer: shape.Layer,
                        isTrigger: shape.IsTrigger,
                        userData: shape.Source
                    );
                }
                else
                {
                    world.AddBox(
                        center: shape.Center,
                        halfExtents: shape.HalfExtents,
                        layer: shape.Layer,
                        isTrigger: shape.IsTrigger,
                        oneWayUp: shape.OneWayUp,
                        userData: shape.Source
                    );
                }
            }
        );
    }

    /// <summary>
    ///     Emit the baked shapes without building a world. The editor's collider overlay draws these,
    ///     so what it shows is exactly what the simulation receives — merged tile runs included.
    /// </summary>
    public static void Bake(SceneNode root, Func<string?, Tileset?>? tilesetLoader,
        Action<Baked2DShape> emit) =>
        Collect(node: root, emit: emit, loader: tilesetLoader);

    private static void Collect(SceneNode node, Action<Baked2DShape> emit,
        Func<string?, Tileset?>? loader)
    {
        if (node.Visible)
        {
            if (node.Collider2DEnabled) AddNodeCollider(node: node, emit: emit);
            if (node is { Kind: NodeKind.Tilemap, TilemapCollision: true })
                AddTilemapColliders(node: node, emit: emit, loader: loader);
        }

        for (int i = 0; i < node.Children.Count; i++)
            Collect(node: node.Children[i], emit: emit, loader: loader);
    }

    private static void AddNodeCollider(SceneNode node, Action<Baked2DShape> emit)
    {
        var w = WorldTransform(node);
        var center = new Vec2(
            x: w.Position.X + node.Collider2DOffset.X,
            y: w.Position.Y + node.Collider2DOffset.Y
        );

        if (node.Collider2DShape == 1)
        {
            // Circles cannot be non-uniformly scaled in an AABB world — take the larger axis so the
            // authored shape is never smaller than what the editor drew.
            float scale = MathF.Max(x: MathF.Abs(w.Scale.X), y: MathF.Abs(w.Scale.Y));
            float r = MathF.Max(x: 1e-4f, y: node.Collider2DRadius * scale);
            emit(
                new Baked2DShape(
                    IsCircle: true,
                    Center: center,
                    HalfExtents: new Vec2(x: r, y: r),
                    Radius: r,
                    Layer: node.Collider2DLayer,
                    IsTrigger: node.Collider2DIsTrigger,
                    OneWayUp: false,
                    Source: node
                )
            );
            return;
        }

        emit(
            new Baked2DShape(
                IsCircle: false,
                Center: center,
                HalfExtents: new Vec2(
                    x: MathF.Max(x: 1e-4f, y: node.Collider2DSize.X * MathF.Abs(w.Scale.X)),
                    y: MathF.Max(x: 1e-4f, y: node.Collider2DSize.Y * MathF.Abs(w.Scale.Y))
                ),
                Radius: 0f,
                Layer: node.Collider2DLayer,
                IsTrigger: node.Collider2DIsTrigger,
                OneWayUp: node.Collider2DOneWayUp,
                Source: node
            )
        );
    }

    /// <summary>
    ///     Bake a tilemap's solid tiles into boxes, merging horizontally adjacent tiles that share the
    ///     same one-way flag into a single box. A 100×100 solid floor becomes 100 boxes instead of
    ///     10 000, which is what keeps the broadphase cheap.
    /// </summary>
    // ponytail: horizontal run merging only. Full 2D rectangle merging would cut the count further on
    // tall solid masses (walls, pits); add it if the collider count ever shows up in a profile.
    private static void AddTilemapColliders(SceneNode node, Action<Baked2DShape> emit,
        Func<string?, Tileset?>? loader)
    {
        var tileset = loader?.Invoke(node.TilesetPath) ?? LoadTileset(node.TilesetPath);
        if (tileset is null) return;

        var w = WorldTransform(node);
        float stepX = MathF.Max(x: 1e-4f, y: node.TileWorldSize) * w.Scale.X;
        float stepY = MathF.Max(x: 1e-4f, y: node.TileWorldSize) * w.Scale.Y;
        float halfY = stepY * 0.5f;

        foreach (var layer in node.TilemapLayers)
        {
            if (!layer.Visible || layer.IsEmpty) continue;

            for (int ty = layer.OriginY; ty < layer.OriginY + layer.Height; ty++)
            {
                int runStart = int.MinValue;
                bool runOneWay = false;

                for (int tx = layer.OriginX; tx <= layer.OriginX + layer.Width; tx++)
                {
                    // One past the end closes any open run without a duplicated tail block.
                    bool inside = tx < layer.OriginX + layer.Width;
                    int tile = inside ? layer.GetTile(x: tx, y: ty) : Tileset.EmptyTile;
                    bool solid = inside && tileset.IsSolid(tile);
                    bool oneWay = solid && tileset.IsOneWay(tile);

                    if (runStart != int.MinValue && (!solid || oneWay != runOneWay))
                    {
                        EmitRun(
                            from: runStart,
                            to: tx - 1,
                            oneWay: runOneWay
                        );
                        runStart = int.MinValue;
                    }

                    if (solid && runStart == int.MinValue)
                    {
                        runStart = tx;
                        runOneWay = oneWay;
                    }
                }

                continue;

                void EmitRun(int from, int to, bool oneWay)
                {
                    int tiles = to - from + 1;
                    emit(
                        new Baked2DShape(
                            IsCircle: false,
                            Center: new Vec2(
                                x: w.Position.X + ((from + (tiles * 0.5f)) * stepX),
                                y: w.Position.Y + ((ty + 0.5f) * stepY)
                            ),
                            HalfExtents: new Vec2(x: tiles * stepX * 0.5f, y: halfY),
                            Radius: 0f,
                            Layer: node.Collider2DLayer,
                            IsTrigger: false,
                            OneWayUp: oneWay,
                            Source: node
                        )
                    );
                }
            }
        }
    }

    private static Tileset? LoadTileset(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            string abs = Path.GetFullPath(path);
            return File.Exists(abs) ? Tileset.Load(abs) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    private static Transform3D WorldTransform(SceneNode node)
    {
        var local = new Transform3D(
            position: node.Position,
            rotation: node.Rotation,
            scale: node.Scale
        );
        return node.Parent is { } parent
            ? Transform3D.Combine(parent: WorldTransform(parent), child: local)
            : local;
    }
}
