using Zigote.Core;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Editor.History;
using Zigote.Render2D;
using Zigote.Runtime.Scene;
using Vec2 = Zigote.Core.Math3D.Vec2;

namespace Zigote.Editor.Panels;

/// <summary>
///     The viewport's 2D authoring mode: an orthographic pan/zoom camera over the XY plane, a grid,
///     tile painting, and 2D collider visualisation.
///     <para>
///         The 2D camera deliberately rides the existing orbit-camera fields — yaw/pitch pinned to 0
///         (a straight-on front view down -Z), <c>_orbitTarget.XY</c> as the pan centre and
///         <c>_orbitDistance</c> as the visible world height. That means the change-gated render
///         signature, framing, gizmo drags and picking all keep working with no extra state to sync.
///     </para>
///     <para>
///         Sprites and tiles are drawn by the native sprite pass with a TRUE orthographic
///         view-projection (<see cref="Camera2DViewProjection" />), so tile edges stay pixel-exact.
///         The 3D pass has no orthographic mode (the native camera FFI takes only fovy/near/far), so
///         meshes in a 2D scene render through the authored perspective camera and will not line up
///         with the sprite plane — a mixed 2D/3D scene should be authored in Orbit mode.
///     </para>
/// </summary>
public sealed partial class ViewportPanel
{
    /// <summary>Visible world height limits for the 2D camera (zoom range).</summary>
    private const float Min2DHeight = 0.05f;

    private const float Max2DHeight = 4000f;

    /// <summary>Grid lines fade out below this on-screen spacing (px) to avoid a solid wash.</summary>
    private const float MinGridPixels = 6f;

    /// <summary>Raised when the Pick tool adopts a tile, so the palette can highlight it.</summary>
    public Action<int>? OnTilePicked;

    private object? _framedScene;
    private (int X, int Y)? _hoverCell;

    private bool _isPanning2D;
    private (int X, int Y)? _rectAnchor;
    private PaintTilesCommand? _stroke;
    private bool _strokePainting;

    /// <summary>True while the viewport is in 2D authoring mode.</summary>
    public bool Is2D => _cameraMode == CameraNavigationMode.TwoD;

    /// <summary>Visible world height of the 2D camera.</summary>
    private float Ortho2DHeight => Math.Clamp(
        value: _orbitDistance,
        min: Min2DHeight,
        max: Max2DHeight
    );

    // ── Palette state (driven by TilePalettePanel) ────────────────────────────

    /// <summary>The tilemap being painted; null disables tile tools.</summary>
    public SceneNode? ActiveTilemap { get; set; }

    /// <summary>Index into <see cref="SceneNode.TilemapLayers" /> of the layer being painted.</summary>
    public int ActiveLayerIndex { get; set; }

    /// <summary>Tile index the paint/rect/fill tools stamp.</summary>
    public int ActiveTile { get; set; }

    public TileTool ActiveTool { get; set; } = TileTool.Paint;

    /// <summary>Show the tile grid and snap 2D drags to it.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>Snap dragged 2D nodes to whole tiles.</summary>
    public bool SnapToGrid { get; set; } = true;

    /// <summary>Draw authored 2D colliders and baked tile collision.</summary>
    public bool ShowColliders2D { get; set; }

    /// <summary>World size of one grid cell — the active tilemap's tile size, else one unit.</summary>
    private float GridWorldSize =>
        ActiveTilemap is { } t ? MathF.Max(x: 1e-3f, y: t.TileWorldSize) : 1f;

    private TilemapLayer? ActiveLayer =>
        ActiveTilemap is { } t && ActiveLayerIndex >= 0 && ActiveLayerIndex < t.TilemapLayers.Count
            ? t.TilemapLayers[ActiveLayerIndex]
            : null;

    // ── Tile painting ─────────────────────────────────────────────────────────

    /// <summary>Does this pointer event belong to a tile tool rather than selection/gizmos?</summary>
    private bool TileToolsActive => Is2D && !_state.IsPlaying && ActiveLayer is not null;

    // ── Camera ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The orthographic view-projection the sprite pass draws 2D content with. Y-up, centred on
    ///     the pan target.
    /// </summary>
    private Mat4 Camera2DViewProjection()
    {
        // Opening a different scene re-frames onto its 2D camera. Keyed on the SceneGraph identity,
        // not on SceneChanged — that fires on every edit, and re-framing mid-paint would be hostile.
        if (!ReferenceEquals(objA: _framedScene, objB: _state.Scene))
        {
            _framedScene = _state.Scene;
            Enter2DMode();
        }

        return new Camera2D {
            Position = new Vec2(x: _orbitTarget.X, y: _orbitTarget.Y),
            OrthoHeight = Ortho2DHeight,
        }.ViewProjection(
            viewportW: MathF.Max(x: 1f, y: Bounds.Width),
            viewportH: MathF.Max(x: 1f, y: Bounds.Height)
        );
    }

    /// <summary>
    ///     Enter 2D mode: pin the camera to a straight-on front view of the XY plane, adopting the
    ///     scene's orthographic camera when it has one — a 2D scene opens showing what the game will
    ///     show, instead of whatever corner the 3D orbit camera happened to be near.
    /// </summary>
    private void Enter2DMode()
    {
        _orbitYaw = 0f;
        _orbitPitch = 0f;
        if (FindOrthoCamera2D(_state.Scene.Root) is { } cam)
        {
            _orbitTarget = new Vec3(x: cam.Position.X, y: cam.Position.Y, z: 0f);
            _orbitDistance = Math.Clamp(
                value: cam.CameraOrthoSize.Y,
                min: Min2DHeight,
                max: Max2DHeight
            );
        }
        else
        {
            _orbitTarget = new Vec3(x: _orbitTarget.X, y: _orbitTarget.Y, z: 0f);
            if (_orbitDistance is < Min2DHeight or > Max2DHeight) _orbitDistance = 10f;
        }

        _gizmoMode = GizmoMode.Translate; // rotate/scale gizmos are 3D-oriented
        ResetTileStroke();
    }

    /// <summary>First orthographic camera node — the same one Sprite2DSystem plays through.</summary>
    private static SceneNode? FindOrthoCamera2D(SceneNode node)
    {
        if (node is { Kind: NodeKind.Camera, CameraProjection: 1 }) return node;
        foreach (var child in node.Children)
        {
            if (FindOrthoCamera2D(child) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>World point on the Z=0 plane under a viewport-space point.</summary>
    private Vec2 ScreenToWorld2D(Offset p)
    {
        float w = MathF.Max(x: 1f, y: Bounds.Width);
        float h = MathF.Max(x: 1f, y: Bounds.Height);
        float halfH = Ortho2DHeight * 0.5f;
        float halfW = halfH * (w / h);
        float nx = ((p.X - Bounds.X) / w * 2f) - 1f;
        float ny = 1f - ((p.Y - Bounds.Y) / h * 2f); // screen Y down → world Y up
        return new Vec2(x: _orbitTarget.X + (nx * halfW), y: _orbitTarget.Y + (ny * halfH));
    }

    /// <summary>Viewport-space point for a world point on the Z=0 plane.</summary>
    private Vec2 WorldToScreen2D(Vec2 world)
    {
        float w = MathF.Max(x: 1f, y: Bounds.Width);
        float h = MathF.Max(x: 1f, y: Bounds.Height);
        float halfH = Ortho2DHeight * 0.5f;
        float halfW = halfH * (w / h);
        float nx = (world.X - _orbitTarget.X) / halfW;
        float ny = (world.Y - _orbitTarget.Y) / halfH;
        return new Vec2(
            x: Bounds.X + ((nx + 1f) * 0.5f * w),
            y: Bounds.Y + ((1f - ny) * 0.5f * h)
        );
    }

    /// <summary>Drag the world with the cursor: one screen pixel moves one screen pixel of world.</summary>
    private void Pan2D(Offset delta)
    {
        float h = MathF.Max(x: 1f, y: Bounds.Height);
        float worldPerPixel = Ortho2DHeight / h;
        _orbitTarget = new Vec3(
            x: _orbitTarget.X - (delta.X * worldPerPixel),
            y: _orbitTarget.Y + (delta.Y * worldPerPixel), // screen Y down → world Y up
            z: 0f
        );
        MarkNeedsPaint();
    }

    /// <summary>
    ///     Zoom about the cursor so the world point under the pointer stays put — the behaviour every
    ///     2D editor has, and the difference between comfortable and unusable at high zoom.
    /// </summary>
    private void Zoom2DAt(Offset cursor, float steps)
    {
        var before = ScreenToWorld2D(cursor);
        _orbitDistance = Math.Clamp(
            value: Ortho2DHeight * MathF.Pow(x: 1.15f, y: -steps),
            min: Min2DHeight,
            max: Max2DHeight
        );
        var after = ScreenToWorld2D(cursor);
        _orbitTarget = new Vec3(
            x: _orbitTarget.X + (before.X - after.X),
            y: _orbitTarget.Y + (before.Y - after.Y),
            z: 0f
        );
        MarkNeedsPaint();
    }

    /// <summary>Snap a world position to the grid when snapping is on.</summary>
    private Vec3 SnapWorld2D(Vec3 world)
    {
        if (!SnapToGrid) return world;
        float g = GridWorldSize;
        return new Vec3(
            x: MathF.Round(world.X / g) * g,
            y: MathF.Round(world.Y / g) * g,
            z: world.Z
        );
    }

    // ── Tile coordinates ──────────────────────────────────────────────────────

    /// <summary>Tile coordinate under a viewport point, in the active tilemap's local tile space.</summary>
    private (int X, int Y)? CellAt(Offset p)
    {
        if (ActiveTilemap is not { } map) return null;
        var world = ScreenToWorld2D(p);
        var origin = WorldOrigin2D(map);
        float step = MathF.Max(x: 1e-4f, y: map.TileWorldSize);
        return (
            (int)MathF.Floor((world.X - origin.X) / step),
            (int)MathF.Floor((world.Y - origin.Y) / step)
        );
    }

    /// <summary>The tilemap node's world position — tile (0,0)'s lower-left corner.</summary>
    private static Vec2 WorldOrigin2D(SceneNode node)
    {
        var pos = node.Position;
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            pos = new Vec3(
                x: p.Position.X + (pos.X * p.Scale.X),
                y: p.Position.Y + (pos.Y * p.Scale.Y),
                z: p.Position.Z + (pos.Z * p.Scale.Z)
            );
        }

        return new Vec2(x: pos.X, y: pos.Y);
    }

    /// <summary>Begin a stroke. Returns true when the tile tool consumed the press.</summary>
    private bool BeginTileStroke(Offset point)
    {
        if (!TileToolsActive || CellAt(point) is not { } cell) return false;
        var layer = ActiveLayer!;

        if (ActiveTool == TileTool.Pick)
        {
            int picked = layer.GetTile(x: cell.X, y: cell.Y);
            if (picked != Tileset.EmptyTile)
            {
                ActiveTile = picked;
                OnTilePicked?.Invoke(picked);
            }

            return true;
        }

        if (ActiveTool == TileTool.Rect)
        {
            _rectAnchor = cell;
            return true;
        }

        _stroke = new PaintTilesCommand(state: _state, layer: layer);
        _strokePainting = true;

        if (ActiveTool == TileTool.Fill) FloodFill(layer: layer, start: cell);
        else PaintCell(cell);
        return true;
    }

    /// <summary>Continue a stroke while dragging.</summary>
    private void ContinueTileStroke(Offset point)
    {
        if (!TileToolsActive) return;
        _hoverCell = CellAt(point);
        if (!_strokePainting || _stroke is null || _hoverCell is not { } cell) return;
        if (ActiveTool is TileTool.Paint or TileTool.Erase) PaintCell(cell);
    }

    /// <summary>Commit the stroke as one undo entry.</summary>
    private void EndTileStroke(Offset point)
    {
        if (!TileToolsActive)
        {
            ResetTileStroke();
            return;
        }

        if (ActiveTool == TileTool.Rect && _rectAnchor is { } anchor && CellAt(point) is { } end)
        {
            var layer = ActiveLayer!;
            _stroke = new PaintTilesCommand(state: _state, layer: layer);
            int tile = ActiveTile;
            for (int y = Math.Min(val1: anchor.Y, val2: end.Y);
                 y <= Math.Max(val1: anchor.Y, val2: end.Y);
                 y++)
            for (int x = Math.Min(val1: anchor.X, val2: end.X);
                 x <= Math.Max(val1: anchor.X, val2: end.X);
                 x++)
                _stroke.Paint(x: x, y: y, tile: tile);
        }

        PushStroke();
        ResetTileStroke();
    }

    private void PaintCell((int X, int Y) cell)
    {
        int tile = ActiveTool == TileTool.Erase ? Tileset.EmptyTile : ActiveTile;
        if (_stroke?.Paint(x: cell.X, y: cell.Y, tile: tile) == true) MarkNeedsPaint();
    }

    /// <summary>
    ///     Flood the contiguous region sharing the clicked cell's tile, bounded to the layer rect
    ///     grown by one ring — so filling an empty area cannot run away across an unbounded plane.
    /// </summary>
    private void FloodFill(TilemapLayer layer, (int X, int Y) start)
    {
        int target = layer.GetTile(x: start.X, y: start.Y);
        int tile = ActiveTool == TileTool.Erase ? Tileset.EmptyTile : ActiveTile;
        if (target == tile) return;

        int minX = layer.IsEmpty ? start.X : Math.Min(val1: layer.OriginX, val2: start.X) - 1;
        int minY = layer.IsEmpty ? start.Y : Math.Min(val1: layer.OriginY, val2: start.Y) - 1;
        int maxX = layer.IsEmpty
            ? start.X
            : Math.Max(val1: layer.OriginX + layer.Width - 1, val2: start.X) + 1;
        int maxY = layer.IsEmpty
            ? start.Y
            : Math.Max(val1: layer.OriginY + layer.Height - 1, val2: start.Y) + 1;

        var seen = new HashSet<(int, int)>();
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue(start);
        seen.Add(start);

        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();
            if (x < minX || y < minY || x > maxX || y > maxY) continue;
            if (layer.GetTile(x: x, y: y) != target) continue;
            _stroke?.Paint(x: x, y: y, tile: tile);

            foreach ((int nx, int ny) in new[] {
                         (x + 1, y),
                         (x - 1, y),
                         (x, y + 1),
                         (x, y - 1),
                     })
            {
                if (seen.Add((nx, ny)))
                    queue.Enqueue((nx, ny));
            }
        }

        MarkNeedsPaint();
    }

    private void PushStroke()
    {
        if (_stroke is { HasEdits: true } stroke)
        {
            stroke.Open = false;
            // The cells were already written as the stroke was drawn; Execute re-applies the same
            // values (a no-op) and records the command so the whole stroke undoes as one step.
            _state.History.Execute(stroke);
        }

        _stroke = null;
    }

    private void ResetTileStroke()
    {
        _stroke = null;
        _strokePainting = false;
        _rectAnchor = null;
    }

    // ── Overlays ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Grid, tile cursor and 2D collider outlines. Editor chrome, so it is stroked into the UI
    ///     paint list alongside the gizmos — the scene content itself (sprites, tiles) is GPU-drawn.
    /// </summary>
    private void Draw2DOverlay(PaintList paint)
    {
        if (!Is2D) return;
        if (ShowGrid) DrawGrid2D(paint);
        if (ShowColliders2D) DrawColliders2D(paint);
        DrawTileCursor(paint);
    }

    private void DrawGrid2D(PaintList paint)
    {
        float step = GridWorldSize;
        float h = MathF.Max(x: 1f, y: Bounds.Height);
        float pixelsPerCell = step / Ortho2DHeight * h;
        // Coarsen the grid until lines are readable, so zooming out never paints a solid block.
        while (pixelsPerCell < MinGridPixels && step < Max2DHeight)
        {
            step *= 4f;
            pixelsPerCell *= 4f;
        }

        var min = ScreenToWorld2D(new Offset(x: Bounds.X, y: Bounds.Y + Bounds.Height));
        var max = ScreenToWorld2D(new Offset(x: Bounds.X + Bounds.Width, y: Bounds.Y));
        var color = new Color(
            r: 1f,
            g: 1f,
            b: 1f,
            a: 0.07f
        );
        var axisColor = new Color(
            r: 1f,
            g: 1f,
            b: 1f,
            a: 0.22f
        );

        float x0 = MathF.Floor(min.X / step) * step;
        for (float x = x0; x <= max.X; x += step)
        {
            float sx = WorldToScreen2D(new Vec2(x: x, y: 0f)).X;
            bool isAxis = MathF.Abs(x) < step * 0.5f;
            paint.AddRect(
                bounds: new Rect(
                    x: sx,
                    y: Bounds.Y,
                    width: 1f,
                    height: Bounds.Height
                ),
                color: isAxis ? axisColor : color
            );
        }

        float y0 = MathF.Floor(min.Y / step) * step;
        for (float y = y0; y <= max.Y; y += step)
        {
            float sy = WorldToScreen2D(new Vec2(x: 0f, y: y)).Y;
            bool isAxis = MathF.Abs(y) < step * 0.5f;
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X,
                    y: sy,
                    width: Bounds.Width,
                    height: 1f
                ),
                color: isAxis ? axisColor : color
            );
        }
    }

    /// <summary>Outline the cell under the cursor (or the pending rectangle while dragging Rect).</summary>
    private void DrawTileCursor(PaintList paint)
    {
        if (!TileToolsActive || ActiveTilemap is not { } map) return;

        (int cx, int cy) = (0, 0);
        (int w, int h) = (1, 1);
        if (_rectAnchor is { } anchor && _hoverCell is { } drag)
        {
            cx = Math.Min(val1: anchor.X, val2: drag.X);
            cy = Math.Min(val1: anchor.Y, val2: drag.Y);
            w = Math.Abs(drag.X - anchor.X) + 1;
            h = Math.Abs(drag.Y - anchor.Y) + 1;
        }
        else if (_hoverCell is { } cell)
            (cx, cy) = cell;
        else
            return;

        var origin = WorldOrigin2D(map);
        float step = MathF.Max(x: 1e-4f, y: map.TileWorldSize);
        // World rect → screen: Y flips, so the world top-left is the screen top-left.
        var topLeft = WorldToScreen2D(
            new Vec2(x: origin.X + (cx * step), y: origin.Y + ((cy + h) * step))
        );
        var bottomRight = WorldToScreen2D(
            new Vec2(x: origin.X + ((cx + w) * step), y: origin.Y + (cy * step))
        );

        StrokeRect(
            paint: paint,
            r: new Rect(
                x: topLeft.X,
                y: topLeft.Y,
                width: MathF.Max(x: 1f, y: bottomRight.X - topLeft.X),
                height: MathF.Max(x: 1f, y: bottomRight.Y - topLeft.Y)
            ),
            color: ActiveTool == TileTool.Erase
                ? new Color(
                    r: 1f,
                    g: 0.45f,
                    b: 0.4f,
                    a: 0.95f
                )
                : new Color(
                    r: 0.45f,
                    g: 0.85f,
                    b: 1f,
                    a: 0.95f
                )
        );
    }

    /// <summary>
    ///     Draw exactly what <see cref="Scene2DPhysics" /> would bake, so what you see is what the
    ///     simulation gets — including merged tile runs rather than per-tile boxes.
    /// </summary>
    private void DrawColliders2D(PaintList paint)
    {
        var solid = new Color(
            r: 0.35f,
            g: 1f,
            b: 0.55f,
            a: 0.85f
        );
        var trigger = new Color(
            r: 1f,
            g: 0.85f,
            b: 0.3f,
            a: 0.85f
        );
        var oneWay = new Color(
            r: 0.45f,
            g: 0.75f,
            b: 1f,
            a: 0.85f
        );

        Scene2DPhysics.Bake(
            root: _state.Scene.Root,
            tilesetLoader: path => _state.Sprites2D.GetTileset(path)?.Set,
            emit: shape =>
            {
                var topLeft = WorldToScreen2D(
                    new Vec2(
                        x: shape.Center.X - shape.HalfExtents.X,
                        y: shape.Center.Y + shape.HalfExtents.Y
                    )
                );
                var bottomRight = WorldToScreen2D(
                    new Vec2(
                        x: shape.Center.X + shape.HalfExtents.X,
                        y: shape.Center.Y - shape.HalfExtents.Y
                    )
                );
                StrokeRect(
                    paint: paint,
                    r: new Rect(
                        x: topLeft.X,
                        y: topLeft.Y,
                        width: MathF.Max(x: 1f, y: bottomRight.X - topLeft.X),
                        height: MathF.Max(x: 1f, y: bottomRight.Y - topLeft.Y)
                    ),
                    color: shape.IsTrigger ? trigger : shape.OneWayUp ? oneWay : solid
                );
            }
        );
    }

    private static void StrokeRect(PaintList paint, Rect r, Color color)
    {
        paint.AddRect(
            bounds: new Rect(
                x: r.X,
                y: r.Y,
                width: r.Width,
                height: 1f
            ),
            color: color
        );
        paint.AddRect(
            bounds: new Rect(
                x: r.X,
                y: r.Y + r.Height - 1f,
                width: r.Width,
                height: 1f
            ),
            color: color
        );
        paint.AddRect(
            bounds: new Rect(
                x: r.X,
                y: r.Y,
                width: 1f,
                height: r.Height
            ),
            color: color
        );
        paint.AddRect(
            bounds: new Rect(
                x: r.X + r.Width - 1f,
                y: r.Y,
                width: 1f,
                height: r.Height
            ),
            color: color
        );
    }
}
