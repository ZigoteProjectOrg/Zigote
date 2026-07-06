using System.Runtime.InteropServices;
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
    private readonly Camera2D _defaultCamera = new();
    private readonly Dictionary<(int Blend, int Stage, uint Shader), Material2D> _materials = new();
    private readonly Renderer2D _renderer;
    private readonly Dictionary<string, uint> _shaders = new();
    private readonly Dictionary<string, SpriteTexture?> _textures = new();

    public Sprite2DSystem(ISpriteDevice? device = null)
    {
        Device = device ?? new EngineSpriteDevice();
        _renderer = new Renderer2D(Device);
    }

    public ISpriteDevice Device { get; }

    /// <summary>Draw batches emitted by the last <see cref="Render" /> (diagnostics).</summary>
    public int BatchCount => _renderer.BatchCount;

    public void Dispose()
    {
        Clear();
    }

    /// <summary>Destroy every cached texture/shader (scene close, project switch, host dispose).</summary>
    public void Clear()
    {
        foreach (var tex in _textures.Values) tex?.Destroy();
        _textures.Clear();
        _shaders.Clear();
        _materials.Clear();
        _animElapsed.Clear();
    }

    /// <summary>Drop play-session animation state (play stop). Caches stay warm for edit mode.</summary>
    public void ResetPlayState()
    {
        _animElapsed.Clear();
    }

    /// <summary>Load (and cache) a sprite texture by path; null when missing/undecodable (cached too).</summary>
    public SpriteTexture? GetTexture(string path, SpriteFilter filter = SpriteFilter.Linear,
        bool srgb = true, SpriteWrap wrap = SpriteWrap.Clamp)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var abs = Path.GetFullPath(path);
        if (_textures.TryGetValue(abs, out var cached)) return cached;
        var tex = File.Exists(abs)
            ? SpriteTexture.Load(
                Device,
                abs,
                filter,
                srgb,
                wrap
            )
            : null;
        _textures[abs] = tex;
        return tex;
    }

    /// <summary>Compile (and cache) a custom sprite shader from a .wgsl file; 0 when missing/rejected.</summary>
    public uint GetShader(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        var abs = Path.GetFullPath(path);
        if (_shaders.TryGetValue(abs, out var cached)) return cached;
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
            _animElapsed[node.Id] = (_animElapsed.TryGetValue(node.Id, out var t) ? t : 0f) + dt;
        for (var i = 0; i < node.Children.Count; i++) AdvanceAnimation(node.Children[i], dt);
    }

    /// <summary>
    ///     The play-mode scene-stage camera: a script override (<see cref="Sprites.Camera" />) wins, else
    ///     the first orthographic camera node (center/roll from its world transform, height =
    ///     CameraOrthoSize.Y), else a default 10-unit-high view centered on the origin.
    /// </summary>
    public Mat4 ResolvePlayCamera(SceneNode root, float viewportW, float viewportH)
    {
        if (Sprites.Camera is { } script) return script.ViewProjection(viewportW, viewportH);

        var cam = FindOrthoCamera(root);
        if (cam != null)
        {
            var world = WorldTransform(cam);
            _defaultCamera.Position = new Vec2(world.Position.X, world.Position.Y);
            _defaultCamera.OrthoHeight = MathF.Max(0.01f, cam.CameraOrthoSize.Y);
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

        return _defaultCamera.ViewProjection(viewportW, viewportH);
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
        var overlay = Camera2D.PixelOverlay(viewportW, viewportH);
        _renderer.Begin(
            sceneViewProjection,
            overlay,
            viewportW,
            viewportH
        );
        CollectNode(root, includeScriptQueue);
        if (includeScriptQueue)
        {
            var queue = CollectionsMarshal.AsSpan(Sprites.Draws);
            for (var i = 0; i < queue.Length; i++) _renderer.Draw(in queue[i]);
        }

        _renderer.End();
    }

    private void CollectNode(SceneNode node, bool playMode)
    {
        if (node is { Kind: NodeKind.Sprite, Visible: true }) DrawSpriteNode(node, playMode);
        for (var i = 0; i < node.Children.Count; i++) CollectNode(node.Children[i], playMode);
    }

    private void DrawSpriteNode(SceneNode node, bool playMode)
    {
        var tex = GetTexture(node.TexturePath ?? "");
        if (tex == null) return;

        var cols = Math.Max(1, node.SpriteCols);
        var rows = Math.Max(1, node.SpriteRows);
        var frameIndex = Math.Clamp(node.SpriteFrame, 0, cols * rows - 1);
        if (playMode && node.SpriteFps > 0f && _animElapsed.TryGetValue(node.Id, out var elapsed))
            frameIndex = (int)(elapsed * node.SpriteFps) % (cols * rows);

        var col = frameIndex % cols;
        var row = frameIndex / cols;
        var frame = new SpriteFrame(
            col / (float)cols,
            row / (float)rows,
            (col + 1) / (float)cols,
            (row + 1) / (float)rows,
            tex.Width / cols,
            tex.Height / rows
        );

        var world = WorldTransform(node);
        var ppu = MathF.Max(0.001f, node.SpritePixelsPerUnit);

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
                SortingLayer =
                    (short)Math.Clamp(node.SpriteSortingLayer, short.MinValue, short.MaxValue),
                OrderInLayer =
                    (short)Math.Clamp(node.SpriteOrderInLayer, short.MinValue, short.MaxValue),
                Texture = tex.Handle,
                Material = MaterialFor(node),
            }
        );
    }

    /// <summary>Shared Material2D per (blend, stage, shader) so consecutive same-material sprites batch.</summary>
    private Material2D? MaterialFor(SceneNode node)
    {
        var shader = string.IsNullOrEmpty(node.SpriteShaderPath)
            ? 0u
            : GetShader(node.SpriteShaderPath);
        if (node.SpriteBlend == 0 && node.SpriteStage == 0 && shader == 0)
            return null; // Material2D.Default
        var key = (node.SpriteBlend, node.SpriteStage, shader);
        if (_materials.TryGetValue(key, out var mat)) return mat;
        mat = new Material2D {
            Blend = (Blend2D)Math.Clamp(node.SpriteBlend, 0, 2),
            Stage = (Stage2D)Math.Clamp(node.SpriteStage, 0, 1),
            ShaderHandle = shader,
        };
        _materials[key] = mat;
        return mat;
    }

    private static SceneNode? FindOrthoCamera(SceneNode node)
    {
        if (node is { Kind: NodeKind.Camera, CameraProjection: 1 }) return node;
        for (var i = 0; i < node.Children.Count; i++)
        {
            var found = FindOrthoCamera(node.Children[i]);
            if (found != null) return found;
        }

        return null;
    }

    private static Transform3D WorldTransform(SceneNode node)
    {
        var local = new Transform3D(node.Position, node.Rotation, node.Scale);
        return node.Parent is { } parent
            ? Transform3D.Combine(WorldTransform(parent), local)
            : local;
    }
}