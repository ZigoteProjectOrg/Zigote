namespace Zigote.Render2D;

/// <summary>
///     The 2D pipeline seam: texture/shader lifetime + per-frame Begin/Submit. The engine-backed
///     implementation is <see cref="EngineSpriteDevice" />; headless tests inject a fake.
/// </summary>
public interface ISpriteDevice
{
    uint CreateTexture(ReadOnlySpan<byte> rgba, int width, int height, SpriteFilter filter,
        bool srgb, SpriteWrap wrap);

    uint CreateTextureFromFile(string path, SpriteFilter filter, bool srgb, SpriteWrap wrap,
        out int width,
        out int height);

    void DestroyTexture(uint texture);
    uint CreateShader(string wgsl);

    void Begin(ReadOnlySpan<float> sceneViewProj, ReadOnlySpan<float> overlayViewProj,
        float viewportW,
        float viewportH);

    void Submit(uint texture, uint texture2, uint shader, Blend2D blend, Stage2D stage,
        ReadOnlySpan<float> materialParams, ReadOnlySpan<float> instances, int count);
}
