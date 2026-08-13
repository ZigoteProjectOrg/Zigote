using Zigote.Core.Math3D;
using Zigote.Render2D;

namespace Zigote.Scripting;

/// <summary>
///     The contract the host implements to back the <see cref="Sprites" /> scripting API with the real
///     2D renderer: resource creation resolves against the game's content root and the live GPU
///     device.
///     Strongly-typed like <see cref="IVfxBackend" /> so a headless test can inject a fake.
/// </summary>
public interface ISpritesBackend
{
    /// <summary>
    ///     The sprite device, for advanced use (e.g. constructing a
    ///     <see cref="DynamicTextureAtlas" />).
    /// </summary>
    ISpriteDevice Device { get; }

    /// <summary>Load (and cache) a texture by content-root-relative path. Null if missing/undecodable.</summary>
    SpriteTexture? LoadTexture(string path, SpriteFilter filter, bool srgb, SpriteWrap wrap);

    /// <summary>Compile a custom sprite shader (WGSL, sprite contract). 0 if rejected.</summary>
    uint CreateShader(string wgsl);
}

/// <summary>
///     2D sprite drawing for game scripts. Immediate-mode with <b>tick-queue</b> semantics (the
///     <see cref="DebugDraw" /> model): the host clears the queue at the top of each fixed tick via
///     <see cref="BeginTick" />, scripts <see cref="Draw(in SpriteDraw)" /> during their update, and
///     the
///     viewport <i>reads</i> (never consumes) the last completed tick's queue at render time — so a
///     fast render frame re-renders the previous tick's sprites and a slow frame renders only the
///     final tick's. Script draws share the same sorting-layer/order space as editor-authored Sprite
///     nodes, so they interleave freely.
///     <para>
///         Resources (textures, shaders, atlases) are load-once and script-owned — create them in
///         <c>OnCreate</c>, call <see cref="SpriteTexture.Destroy" /> in <c>OnDestroy</c>. Outside
///         play
///         every call is a safe no-op. The scene-stage camera defaults to the scene's orthographic
///         camera node (or a 10-unit-high centered view); <see cref="SetCamera" /> overrides it.
///     </para>
/// </summary>
public static class Sprites
{
    internal static readonly List<SpriteDraw> Draws = [];
    private static readonly Camera2D CameraInstance = new();

    /// <summary>Set by the host (or a test) to route calls to the real 2D renderer.</summary>
    public static ISpritesBackend? Backend { get; set; }

    public static bool IsAvailable => Backend != null;

    /// <summary>The sprite draws queued by the last completed tick (read-only; the host renders them).</summary>
    public static IReadOnlyList<SpriteDraw> Queue => Draws;

    /// <summary>The script camera override for the scene stage, or null when no script set one.</summary>
    public static Camera2D? Camera { get; private set; }

    /// <summary>Host: clear the queue at the top of each fixed tick, before scripts run.</summary>
    public static void BeginTick() => Draws.Clear();

    /// <summary>Host: drop everything and detach on play stop so nothing lingers.</summary>
    public static void Clear()
    {
        Draws.Clear();
        Camera = null;
    }

    /// <summary>Load (and cache) a texture by content-root-relative path. Null outside play or on failure.</summary>
    public static SpriteTexture? LoadTexture(string path, SpriteFilter filter = SpriteFilter.Linear,
        bool srgb = true, SpriteWrap wrap = SpriteWrap.Clamp)
    {
        return Backend?.LoadTexture(
            path: path,
            filter: filter,
            srgb: srgb,
            wrap: wrap
        );
    }

    /// <summary>
    ///     Compile a custom sprite material shader (WGSL; see the contract in the engine's
    ///     sprite_shader_source.wgsl). Returns the shader handle for
    ///     <see cref="Material2D.ShaderHandle" />,
    ///     or 0 when rejected / outside play.
    /// </summary>
    public static uint CreateShader(string wgsl) => Backend?.CreateShader(wgsl) ?? 0;

    /// <summary>
    ///     Create a dynamic texture atlas over the live device: pack sprites at runtime
    ///     (procedural art, composited avatars, decals) into one texture — one batch, one draw.
    ///     Null outside play.
    /// </summary>
    public static DynamicTextureAtlas? CreateAtlas(int initialSize = 512, int maxSize = 4096,
        int padding = 2, SpriteFilter filter = SpriteFilter.Linear)
    {
        return Backend is { } b
            ? new DynamicTextureAtlas(
                device: b.Device,
                initialSize: initialSize,
                maxSize: maxSize,
                padding: padding,
                filter: filter
            )
            : null;
    }

    /// <summary>Override the scene-stage 2D camera (world center + vertical world-units visible).</summary>
    public static void SetCamera(Vec2 center, float orthoHeight, float rotation = 0f,
        float zoom = 1f)
    {
        CameraInstance.Position = center;
        CameraInstance.OrthoHeight = orthoHeight;
        CameraInstance.Rotation = rotation;
        CameraInstance.Zoom = zoom;
        Camera = CameraInstance;
    }

    /// <summary>Drop the script camera override (back to the scene's ortho camera / default view).</summary>
    public static void ClearCamera() => Camera = null;

    /// <summary>Queue one sprite for this tick (no-op outside play).</summary>
    public static void Draw(in SpriteDraw draw)
    {
        if (Backend == null) return;
        Draws.Add(draw);
    }

    /// <summary>Queue an untinted, unrotated sprite of the full texture.</summary>
    public static void Draw(SpriteTexture texture, Vec2 position, Vec2 size,
        short layer = 0, short order = 0)
    {
        Draw(
            texture: texture,
            position: position,
            size: size,
            frame: texture.FullFrame,
            color: new Vec4(
                x: 1,
                y: 1,
                z: 1,
                w: 1
            ),
            rotation: 0f,
            layer: layer,
            order: order
        );
    }

    /// <summary>Queue a sprite with a sub-rect frame (sprite sheet / atlas), tint and rotation.</summary>
    public static void Draw(SpriteTexture texture, Vec2 position, Vec2 size, in SpriteFrame frame,
        Vec4 color, float rotation = 0f, short layer = 0, short order = 0,
        Material2D? material = null)
    {
        if (Backend == null) return;
        Draws.Add(
            new SpriteDraw {
                X = position.X,
                Y = position.Y,
                Z = 0f,
                Rotation = rotation,
                Width = size.X,
                Height = size.Y,
                PivotX = 0.5f,
                PivotY = 0.5f,
                Frame = frame,
                Color = color,
                SortingLayer = layer,
                OrderInLayer = order,
                Texture = texture.Handle,
                Material = material,
            }
        );
    }
}
