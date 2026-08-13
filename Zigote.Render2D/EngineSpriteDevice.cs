using Zigote.Core.Engine;

namespace Zigote.Render2D;

/// <summary>
///     Forwards the sprite pipeline onto the native engine via <see cref="ZigoteEngine.Instance" />.
///     Null-safe: every member no-ops (creates return 0) when no engine is running, so code built
///     against the device works headlessly.
/// </summary>
public sealed class EngineSpriteDevice : ISpriteDevice
{
    public uint CreateTexture(ReadOnlySpan<byte> rgba, int width, int height, SpriteFilter filter,
        bool srgb,
        SpriteWrap wrap)
    {
        var engine = ZigoteEngine.Instance;
        if (engine is null || width <= 0 || height <= 0) return 0;
        return engine.SpritesTextureCreate(
            rgba: rgba,
            width: (uint)width,
            height: (uint)height,
            filter: (uint)filter,
            srgb: srgb ? 1u : 0u,
            wrap: (uint)wrap
        );
    }

    public uint CreateTextureFromFile(string path, SpriteFilter filter, bool srgb, SpriteWrap wrap,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        var engine = ZigoteEngine.Instance;
        if (engine is null) return 0;
        uint handle =
            engine.SpritesTextureCreateFile(
                path: path,
                filter: (uint)filter,
                srgb: srgb ? 1u : 0u,
                wrap: (uint)wrap,
                outW: out uint w,
                outH: out uint h
            );
        width = (int)w;
        height = (int)h;
        return handle;
    }

    public void DestroyTexture(uint texture) =>
        ZigoteEngine.Instance?.SpritesTextureDestroy(texture);

    public uint CreateShader(string wgsl) => ZigoteEngine.Instance?.SpritesShaderCreate(wgsl) ?? 0;

    public void Begin(ReadOnlySpan<float> sceneViewProj, ReadOnlySpan<float> overlayViewProj,
        float viewportW,
        float viewportH)
    {
        ZigoteEngine.Instance?.SpritesBegin(
            sceneViewProj: sceneViewProj,
            overlayViewProj: overlayViewProj,
            viewportW: viewportW,
            viewportH: viewportH
        );
    }

    public void Submit(uint texture, uint texture2, uint shader, Blend2D blend, Stage2D stage,
        ReadOnlySpan<float> materialParams, ReadOnlySpan<float> instances, int count)
    {
        if (count <= 0) return;
        ZigoteEngine.Instance?.SpritesDraw(
            texture: texture,
            texture2: texture2,
            shader: shader,
            blend: (uint)blend,
            stage: (uint)stage,
            materialParams: materialParams,
            instances: instances,
            count: (uint)count
        );
    }
}
