using Zigote.Render2D;
using Zigote.Scripting;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Backs the generic <see cref="Sprites" /> scripting API in play mode over the host-owned
///     <see cref="Sprite2DSystem" />, so script-loaded textures share the host's path cache (and its
///     lifecycle) and script draws render through the same sorted/batched pipeline as Sprite nodes.
/// </summary>
public sealed class RuntimeSpritesBackend(Sprite2DSystem sprites) : ISpritesBackend
{
    public ISpriteDevice Device => sprites.Device;

    public SpriteTexture? LoadTexture(string path, SpriteFilter filter, bool srgb, SpriteWrap wrap)
    {
        string resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        return sprites.GetTexture(
            path: resolved,
            filter: filter,
            srgb: srgb,
            wrap: wrap
        );
    }

    public uint CreateShader(string wgsl) => Device.CreateShader(wgsl);
}
