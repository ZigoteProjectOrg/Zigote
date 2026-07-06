namespace Zigote.Render2D;

/// <summary>
///     Sprite material: shader + secondary texture + blend/stage + a 16-float params UBO.
///     Batching compares materials by REFERENCE — share one instance across draws to batch them.
///     Mutating a shared instance mid-frame (between Begin and End) is not supported: the batcher
///     reads it once at End, so all draws referencing it get the final values.
/// </summary>
public sealed class Material2D
{
    public static readonly Material2D Default = new();
    public readonly float[] Params = new float[16];
    public Blend2D Blend = Blend2D.Alpha;

    public uint ShaderHandle; // 0 = built-in sprite shader
    public Stage2D Stage = Stage2D.Scene;
    public uint Texture2; // secondary texture for custom shaders (0 = white)
}