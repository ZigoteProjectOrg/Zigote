namespace Zigote.Render2D;

public enum SpriteFilter
{
    Nearest = 0,
    Linear = 1,
}

public enum SpriteWrap
{
    Clamp = 0,
    Repeat = 1,
}

public enum Blend2D
{
    Alpha = 0,
    Additive = 1,
    Opaque = 2,
}

public enum Stage2D
{
    Scene = 0,
    Overlay = 1,
}

/// <summary>
///     Normalized UV sub-rect of a texture plus its pixel size. V0 is the TOP of the sub-rect in
///     image space (texture row 0 = top).
/// </summary>
public readonly record struct SpriteFrame(
    float U0,
    float V0,
    float U1,
    float V1,
    int PixelWidth,
    int PixelHeight)
{
    public static readonly SpriteFrame Full = new(
        0f,
        0f,
        1f,
        1f,
        0,
        0
    );
}
