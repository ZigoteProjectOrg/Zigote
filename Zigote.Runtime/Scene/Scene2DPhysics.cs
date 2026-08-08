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
            root,
            tilesetLoader,
            shape =>
            {
                if (shape.IsCircle)
                    world.AddCircle(
                        shape.Center,
                        shape.Radius,
                        shape.Layer,
                        shape.IsTrigger,
                        shape.Source
                    );
                else
                    world.AddBox(
                        shape.Center,
                        shape.HalfExtents,
                        shape.Layer,
                        shape.IsTrigger,
                        shape.OneWayUp,
                        shape.Source
                    );
            }
        );
    }

    /// <summary>
    ///     Emit the baked shapes without building a world. The editor's collider overlay draws these,
    ///     so what it shows is exactly what the simulation receives — merged tile runs included.
    /// </summary>
    public static void Bake(SceneNode root, Func<string?, Tileset?>? tilesetLoader,
        Action<Baked2DShape> emit)
    {
        Collect(root, emit, tilesetLoader);
    }

    private static void Collect(SceneNode node, Action<Baked2DShape> emit,
        Func<string?, Tileset?>? loader)
    {
        if (node.Visible)
        {
            if (node.Collider2DEnabled) AddNodeCollider(node, emit);
            if (node is { Kind: NodeKind.Tilemap, TilemapCollision: true })
                AddTilemapColliders(node, emit, loader);
        }

        for (var i = 0; i < node.Children.Count; i++) Collect(node.Children[i], emit, loader);
    }

    private static void AddNodeCollider(SceneNode node, Action<Baked2DShape> emit)
    {
        var w = WorldTransform(node);
        var center = new Vec2(
            w.Position.X + node.Collider2DOffset.X,
            w.Position.Y + node.Collider2DOffset.Y
        );

        if (node.Collider2DShape == 1)
        {
            // Circles cannot be non-uniformly scaled in an AABB world — take the larger axis so the
            // authored shape is never smaller than what the editor drew.
            var scale = MathF.Max(MathF.Abs(w.Scale.X), MathF.Abs(w.Scale.Y));
            var r = MathF.Max(1e-4f, node.Collider2DRadius * scale);
            emit(
                new Baked2DShape(
                    true,
                    center,
                    new Vec2(r, r),
                    r,
                    node.Collider2DLayer,
                    node.Collider2DIsTrigger,
                    false,
                    node
                )
            );
            return;
        }

        emit(
            new Baked2DShape(
                false,
                center,
                new Vec2(
                    MathF.Max(1e-4f, node.Collider2DSize.X * MathF.Abs(w.Scale.X)),
                    MathF.Max(1e-4f, node.Collider2DSize.Y * MathF.Abs(w.Scale.Y))
                ),
                0f,
                node.Collider2DLayer,
                node.Collider2DIsTrigger,
                node.Collider2DOneWayUp,
                node
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
        var stepX = MathF.Max(1e-4f, node.TileWorldSize) * w.Scale.X;
        var stepY = MathF.Max(1e-4f, node.TileWorldSize) * w.Scale.Y;
        var halfY = stepY * 0.5f;

        foreach (var layer in node.TilemapLayers)
        {
            if (!layer.Visible || layer.IsEmpty) continue;

            for (var ty = layer.OriginY; ty < layer.OriginY + layer.Height; ty++)
            {
                var runStart = int.MinValue;
                var runOneWay = false;

                for (var tx = layer.OriginX; tx <= layer.OriginX + layer.Width; tx++)
                {
                    // One past the end closes any open run without a duplicated tail block.
                    var inside = tx < layer.OriginX + layer.Width;
                    var tile = inside ? layer.GetTile(tx, ty) : Tileset.EmptyTile;
                    var solid = inside && tileset.IsSolid(tile);
                    var oneWay = solid && tileset.IsOneWay(tile);

                    if (runStart != int.MinValue && (!solid || oneWay != runOneWay))
                    {
                        EmitRun(
                            runStart,
                            tx - 1,
                            runOneWay
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
                    var tiles = to - from + 1;
                    emit(
                        new Baked2DShape(
                            false,
                            new Vec2(
                                w.Position.X + (from + tiles * 0.5f) * stepX,
                                w.Position.Y + (ty + 0.5f) * stepY
                            ),
                            new Vec2(tiles * stepX * 0.5f, halfY),
                            0f,
                            node.Collider2DLayer,
                            false,
                            oneWay,
                            node
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
            var abs = Path.GetFullPath(path);
            return File.Exists(abs) ? Tileset.Load(abs) : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static Transform3D WorldTransform(SceneNode node)
    {
        var local = new Transform3D(node.Position, node.Rotation, node.Scale);
        return node.Parent is { } parent
            ? Transform3D.Combine(WorldTransform(parent), local)
            : local;
    }
}