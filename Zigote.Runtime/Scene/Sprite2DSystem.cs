using System.Runtime.InteropServices;
using System.Text.Json;
using Zigote.Core.Math3D;
using Zigote.Render2D;
using Zigote.Scripting;

namespace Zigote.Runtime.Scene;

/// <summary>
///     The shared 2D sprite renderer both hosts drive: collects <see cref="NodeKind.Sprite" /> nodes
///     (edit mode and play mode) plus the script tick-queue (<see cref="Sprites.Queue" />, play only),
///     sorts/batches them through <see cref="Renderer2D" />, and uploads to the native sprite pass —
///     call <see cref="Render" /> once per frame BEFORE <c>Render3D</c>.
///     <para>
///         HOST-OWNED lifecycle (EditorState in the editor, GameHost in the player), deliberately
///         session-independent so sprites render while authoring: the texture/shader caches are keyed
///         on absolute canonical paths and survive play sessions; <see cref="Clear" /> destroys them
///         on
///         scene close / project switch / host dispose. Play-mode animation state is session-scoped
///         (<see cref="ResetPlayState" /> on stop) and never mutates the authored SpriteFrame.
///     </para>
/// </summary>
public sealed class Sprite2DSystem : IDisposable
{
    private readonly Dictionary<int, float> _animElapsed = new();

    // Second index on the UNRESOLVED path, so the per-frame lookup never has to canonicalise.
    // Both are cleared together in Clear().
    private readonly Dictionary<string, SpriteTexture?> _byRawPath = new();
    private readonly Camera2D _defaultCamera = new();
    private readonly Dictionary<(int Blend, int Stage, uint Shader), Material2D> _materials = new();
    private readonly Renderer2D _renderer;
    private readonly Dictionary<string, uint> _shaders = new();
    private readonly Dictionary<string, SpriteTexture?> _textures = new();
    private readonly Dictionary<string, (Tileset Set, SpriteFrame[] Frames)?> _tilesets = new();

    private readonly Dictionary<string, (Tileset Set, SpriteFrame[] Frames)?> _tilesetsByRawPath =
        new();

    // This frame's camera world-XY bounds, used to cull tiles (see ComputeCullRect).
    private float _cullMaxX, _cullMaxY, _cullMinX, _cullMinY;
    private bool _cullValid;

    public Sprite2DSystem(ISpriteDevice? device = null)
    {
        Device = device ?? new EngineSpriteDevice();
        _renderer = new Renderer2D(Device);
    }

    public ISpriteDevice Device { get; }

    /// <summary>Draw batches emitted by the last <see cref="Render" /> (diagnostics).</summary>
    public int BatchCount => _renderer.BatchCount;

    public void Dispose() => Clear();

    /// <summary>Destroy every cached texture/shader (scene close, project switch, host dispose).</summary>
    public void Clear()
    {
        foreach (var tex in _textures.Values) tex?.Destroy();
        _textures.Clear();
        _byRawPath.Clear();
        _tilesets.Clear();
        _shaders.Clear();
        _materials.Clear();
        _animElapsed.Clear();
    }

    /// <summary>Drop play-session animation state (play stop). Caches stay warm for edit mode.</summary>
    public void ResetPlayState() => _animElapsed.Clear();

    /// <summary>Load (and cache) a sprite texture by path; null when missing/undecodable (cached too).</summary>
    public SpriteTexture? GetTexture(string path, SpriteFilter filter = SpriteFilter.Linear,
        bool srgb = true, SpriteWrap wrap = SpriteWrap.Clamp)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // Hit the cache on the path as given BEFORE canonicalising. This runs once per sprite per
        // frame, and Path.GetFullPath allocates a fresh string every call — with a few hundred
        // sprites that was tens of KB of garbage per frame and periodic gen0 pauses, for a lookup
        // that almost always resolves to something already loaded.
        if (_byRawPath.TryGetValue(key: path, value: out var byRaw)) return byRaw;

        string abs = Path.GetFullPath(path);
        if (_textures.TryGetValue(key: abs, value: out var cached))
        {
            _byRawPath[path] = cached;
            return cached;
        }

        var tex = File.Exists(abs)
            ? SpriteTexture.Load(
                device: Device,
                path: abs,
                filter: filter,
                srgb: srgb,
                wrap: wrap
            )
            : null;
        _textures[abs] = tex;
        _byRawPath[path] = tex;
        return tex;
    }

    /// <summary>Compile (and cache) a custom sprite shader from a .wgsl file; 0 when missing/rejected.</summary>
    public uint GetShader(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        string abs = Path.GetFullPath(path);
        if (_shaders.TryGetValue(key: abs, value: out uint cached)) return cached;
        uint handle = 0;
        try
        {
            if (File.Exists(abs)) handle = Device.CreateShader(File.ReadAllText(abs));
        }
        catch (IOException)
        {
            // unreadable file behaves like a rejected shader: cached 0 → default material
        }

        _shaders[abs] = handle; // negative results cached too — no per-frame retry storm
        return handle;
    }

    /// <summary>Advance play-mode sprite animations one fixed tick (called from the session's fixed loop).</summary>
    public void AdvanceAnimation(SceneNode node, float dt)
    {
        if (node is { Kind: NodeKind.Sprite, SpriteFps: > 0f })
        {
            _animElapsed[node.Id] =
                (_animElapsed.TryGetValue(key: node.Id, value: out float t) ? t : 0f) + dt;
        }

        for (int i = 0; i < node.Children.Count; i++)
            AdvanceAnimation(node: node.Children[i], dt: dt);
    }

    /// <summary>
    ///     The play-mode scene-stage camera: a script override (<see cref="Sprites.Camera" />) wins, else
    ///     the first orthographic camera node (center/roll from its world transform, height =
    ///     CameraOrthoSize.Y), else a default 10-unit-high view centered on the origin.
    /// </summary>
    public Mat4 ResolvePlayCamera(SceneNode root, float viewportW, float viewportH)
    {
        if (Sprites.Camera is { } script)
            return script.ViewProjection(viewportW: viewportW, viewportH: viewportH);

        var cam = FindOrthoCamera(root);
        if (cam != null)
        {
            var world = WorldTransform(cam);
            _defaultCamera.Position = new Vec2(x: world.Position.X, y: world.Position.Y);
            _defaultCamera.OrthoHeight = MathF.Max(x: 0.01f, y: cam.CameraOrthoSize.Y);
            _defaultCamera.Rotation = world.Rotation.ToEulerRadians().Z;
            _defaultCamera.Zoom = 1f;
        }
        else
        {
            _defaultCamera.Position = Vec2.Zero;
            _defaultCamera.OrthoHeight = 10f;
            _defaultCamera.Rotation = 0f;
            _defaultCamera.Zoom = 1f;
        }

        return _defaultCamera.ViewProjection(viewportW: viewportW, viewportH: viewportH);
    }

    /// <summary>
    ///     Collect, sort and upload this frame's sprites. <paramref name="sceneViewProjection" /> is the
    ///     scene-stage camera (the editor passes its perspective view-proj in edit mode so sprites stay
    ///     coherent with gizmos/picking; hosts pass <see cref="ResolvePlayCamera" /> in play mode).
    ///     Script queue draws are included only when <paramref name="includeScriptQueue" />.
    /// </summary>
    public void Render(SceneNode root, in Mat4 sceneViewProjection, float viewportW,
        float viewportH,
        bool includeScriptQueue)
    {
        ComputeCullRect(in sceneViewProjection);
        var overlay = Camera2D.PixelOverlay(viewportW: viewportW, viewportH: viewportH);
        _renderer.Begin(
            sceneViewProjection: sceneViewProjection,
            overlayViewProjection: overlay,
            viewportW: viewportW,
            viewportH: viewportH
        );
        CollectNode(node: root, playMode: includeScriptQueue);
        if (includeScriptQueue)
        {
            var queue = CollectionsMarshal.AsSpan(Sprites.Draws);
            for (int i = 0; i < queue.Length; i++) _renderer.Draw(in queue[i]);
        }

        _renderer.End();
    }

    private void CollectNode(SceneNode node, bool playMode)
    {
        if (node.Visible)
        {
            switch (node.Kind)
            {
                case NodeKind.Sprite: DrawSpriteNode(node: node, playMode: playMode); break;
                case NodeKind.Tilemap: DrawTilemapNode(node); break;
            }
        }

        for (int i = 0; i < node.Children.Count; i++)
            CollectNode(node: node.Children[i], playMode: playMode);
    }

    private void DrawSpriteNode(SceneNode node, bool playMode)
    {
        var tex = GetTexture(node.TexturePath ?? "");
        if (tex == null) return;

        int cols = Math.Max(val1: 1, val2: node.SpriteCols);
        int rows = Math.Max(val1: 1, val2: node.SpriteRows);
        int frameIndex = Math.Clamp(value: node.SpriteFrame, min: 0, max: (cols * rows) - 1);
        if (playMode && node.SpriteFps > 0f &&
            _animElapsed.TryGetValue(key: node.Id, value: out float elapsed))
            frameIndex = (int)(elapsed * node.SpriteFps) % (cols * rows);

        int col = frameIndex % cols;
        int row = frameIndex / cols;
        var frame = new SpriteFrame(
            U0: col / (float)cols,
            V0: row / (float)rows,
            U1: (col + 1) / (float)cols,
            V1: (row + 1) / (float)rows,
            PixelWidth: tex.Width / cols,
            PixelHeight: tex.Height / rows
        );

        var world = WorldTransform(node);
        float ppu = MathF.Max(x: 0.001f, y: node.SpritePixelsPerUnit);

        _renderer.Draw(
            new SpriteDraw {
                X = world.Position.X,
                Y = world.Position.Y,
                Z = world.Position.Z,
                Rotation = world.Rotation.ToEulerRadians().Z,
                Width = frame.PixelWidth / ppu * world.Scale.X,
                Height = frame.PixelHeight / ppu * world.Scale.Y,
                PivotX = node.SpritePivotX,
                PivotY = node.SpritePivotY,
                Frame = frame,
                Color = node.SpriteColor,
                FlipX = node.SpriteFlipX,
                FlipY = node.SpriteFlipY,
                CornerRadius = node.SpriteCornerRadius,
                BorderWidth = node.SpriteBorderWidth,
                SortingLayer =
                    (short)Math.Clamp(
                        value: node.SpriteSortingLayer,
                        min: short.MinValue,
                        max: short.MaxValue
                    ),
                OrderInLayer =
                    (short)Math.Clamp(
                        value: node.SpriteOrderInLayer,
                        min: short.MinValue,
                        max: short.MaxValue
                    ),
                Texture = tex.Handle,
                Material = MaterialFor(node),
            }
        );
    }

    /// <summary>Shared Material2D per (blend, stage, shader) so consecutive same-material sprites batch.</summary>
    private Material2D? MaterialFor(SceneNode node)
    {
        uint shader = string.IsNullOrEmpty(node.SpriteShaderPath)
            ? 0u
            : GetShader(node.SpriteShaderPath);
        return MaterialFor(blend: node.SpriteBlend, stage: node.SpriteStage, shader: shader);
    }

    private Material2D? MaterialFor(int blend, int stage, uint shader)
    {
        if (blend == 0 && stage == 0 && shader == 0) return null; // Material2D.Default
        var key = (blend, stage, shader);
        if (_materials.TryGetValue(key: key, value: out var mat)) return mat;
        mat = new Material2D {
            Blend = (Blend2D)Math.Clamp(value: blend, min: 0, max: 2),
            Stage = (Stage2D)Math.Clamp(value: stage, min: 0, max: 1),
            ShaderHandle = shader,
        };
        _materials[key] = mat;
        return mat;
    }

    // ── Tilemaps ──────────────────────────────────────────────────────────────

    /// <summary>Load (and cache) a tileset plus its computed UV frames; null when missing/invalid.</summary>
    public (Tileset Set, SpriteFrame[] Frames)? GetTileset(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_tilesetsByRawPath.TryGetValue(key: path, value: out var byRaw)) return byRaw;

        string abs = Path.GetFullPath(path);
        if (_tilesets.TryGetValue(key: abs, value: out var cached))
        {
            _tilesetsByRawPath[path] = cached;
            return cached;
        }

        (Tileset, SpriteFrame[])? entry = null;
        try
        {
            if (File.Exists(abs))
            {
                var set = Tileset.Load(abs);
                entry = (set, set.BuildFrames());
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // Corrupt/unreadable tileset behaves like a missing one; the null is cached too, so a
            // broken path costs one failed read rather than one per frame.
        }

        _tilesets[abs] = entry;
        _tilesetsByRawPath[path] = entry;
        return entry;
    }

    /// <summary>Drop a cached tileset so the next frame re-reads it (the editor calls this on save).</summary>
    public void InvalidateTileset(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _tilesets.Remove(Path.GetFullPath(path));
        _tilesetsByRawPath.Remove(path);
    }

    /// <summary>
    ///     Emit one sprite instance per visible tile. Every tile shares the tileset texture and one
    ///     material, so the batcher collapses an entire tilemap into a single GPU draw call.
    /// </summary>
    private void DrawTilemapNode(SceneNode node)
    {
        if (node.TilemapLayers.Count == 0) return;
        if (GetTileset(node.TilesetPath) is not { } ts) return;
        var tex = GetTexture(ts.Set.TexturePath);
        if (tex == null || ts.Frames.Length == 0) return;

        var world = WorldTransform(node);
        float size = MathF.Max(x: 1e-4f, y: node.TileWorldSize);
        float stepX = size * world.Scale.X;
        float stepY = size * world.Scale.Y;
        float rot = world.Rotation.ToEulerRadians().Z;
        float cos = MathF.Cos(rot);
        float sin = MathF.Sin(rot);
        var material = MaterialFor(blend: node.TilemapBlend, stage: node.TilemapStage, shader: 0u);
        var tint = node.TilemapColor;

        // Cull to the camera rect in this node's tile space. Skipped for a rotated map (the rect no
        // longer maps to a tile range) — rotated tilemaps are rare and still draw correctly, just
        // without the early-out.
        bool cull = _cullValid && MathF.Abs(rot) < 1e-4f && stepX > 1e-6f && stepY > 1e-6f;
        int minTx = int.MinValue;
        int maxTx = int.MaxValue;
        int minTy = int.MinValue;
        int maxTy = int.MaxValue;
        if (cull)
        {
            minTx = (int)MathF.Floor((_cullMinX - world.Position.X) / stepX) - 1;
            maxTx = (int)MathF.Ceiling((_cullMaxX - world.Position.X) / stepX) + 1;
            minTy = (int)MathF.Floor((_cullMinY - world.Position.Y) / stepY) - 1;
            maxTy = (int)MathF.Ceiling((_cullMaxY - world.Position.Y) / stepY) + 1;
        }

        foreach (var layer in node.TilemapLayers)
        {
            if (!layer.Visible || layer.IsEmpty || layer.Opacity <= 0f) continue;

            int x0 = Math.Max(val1: layer.OriginX, val2: minTx);
            int x1 = Math.Min(val1: layer.OriginX + layer.Width - 1, val2: maxTx);
            int y0 = Math.Max(val1: layer.OriginY, val2: minTy);
            int y1 = Math.Min(val1: layer.OriginY + layer.Height - 1, val2: maxTy);
            if (x0 > x1 || y0 > y1) continue;

            var color = new Vec4(
                x: tint.X,
                y: tint.Y,
                z: tint.Z,
                w: tint.W * Math.Clamp(value: layer.Opacity, min: 0f, max: 1f)
            );
            short sortLayer = (short)Math.Clamp(
                value: layer.SortingLayer,
                min: short.MinValue,
                max: short.MaxValue
            );
            short order = (short)Math.Clamp(
                value: layer.OrderInLayer,
                min: short.MinValue,
                max: short.MaxValue
            );

            for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
            {
                int tile = layer.GetTile(x: tx, y: ty);
                if (tile < 0 || tile >= ts.Frames.Length) continue;

                // Cell centre in the node's local 2D space, rotated into world.
                float lx = (tx + 0.5f) * stepX;
                float ly = (ty + 0.5f) * stepY;
                _renderer.Draw(
                    new SpriteDraw {
                        X = world.Position.X + (lx * cos) - (ly * sin),
                        Y = world.Position.Y + (lx * sin) + (ly * cos),
                        Z = world.Position.Z,
                        Rotation = rot,
                        Width = stepX,
                        Height = stepY,
                        PivotX = 0.5f,
                        PivotY = 0.5f,
                        Frame = ts.Frames[tile],
                        Color = color,
                        SortingLayer = sortLayer,
                        OrderInLayer = order,
                        Texture = tex.Handle,
                        Material = material,
                    }
                );
            }
        }
    }

    /// <summary>
    ///     World-XY bounds of the view frustum, from the view-projection inverse over the NDC box
    ///     (wgpu clip space: xy ∈ [-1,1], z ∈ [0,1]). Tight under the 2D ortho camera; merely
    ///     conservative under the perspective edit camera, so it never culls a visible tile.
    /// </summary>
    private void ComputeCullRect(in Mat4 viewProjection)
    {
        var inv = viewProjection.Inverse();
        _cullMinX = float.MaxValue;
        _cullMinY = float.MaxValue;
        _cullMaxX = float.MinValue;
        _cullMaxY = float.MinValue;

        for (int i = 0; i < 8; i++)
        {
            var ndc = new Vec4(
                x: (i & 1) == 0 ? -1f : 1f,
                y: (i & 2) == 0 ? -1f : 1f,
                z: (i & 4) == 0 ? 0f : 1f,
                w: 1f
            );
            var p = inv.MulVec4(ndc);
            if (MathF.Abs(p.W) < 1e-6f)
            {
                _cullValid = false; // corner at infinity — draw everything rather than guess
                return;
            }

            float invW = 1f / p.W;
            float x = p.X * invW;
            float y = p.Y * invW;
            _cullMinX = MathF.Min(x: _cullMinX, y: x);
            _cullMaxX = MathF.Max(x: _cullMaxX, y: x);
            _cullMinY = MathF.Min(x: _cullMinY, y: y);
            _cullMaxY = MathF.Max(x: _cullMaxY, y: y);
        }

        _cullValid = _cullMaxX >= _cullMinX && _cullMaxY >= _cullMinY;
    }

    private static SceneNode? FindOrthoCamera(SceneNode node)
    {
        if (node is { Kind: NodeKind.Camera, CameraProjection: 1 }) return node;
        for (int i = 0; i < node.Children.Count; i++)
        {
            var found = FindOrthoCamera(node.Children[i]);
            if (found != null) return found;
        }

        return null;
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
