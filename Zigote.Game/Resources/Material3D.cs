using Zigote.Core.Math3D;

namespace Zigote.Game.Resources;

public enum AlphaMode
{
    Opaque,
    Mask,
    Blend,
}

public enum RenderEffect : uint
{
    Standard = 0,
    CrtTv = 1,
    Unlit = 2,
}

/// <summary>PBR metallic-roughness material. Textures are optional; factors are used as fallback.</summary>
public sealed class Material3D
{
    public string Name { get; set; } = "";

    public Vec4 BaseColorFactor { get; set; } = new(
        x: 1,
        y: 1,
        z: 1,
        w: 1
    );

    public float MetallicFactor { get; set; } = 0f;
    public float RoughnessFactor { get; set; } = 0.5f;
    public Vec3 EmissiveFactor { get; set; } = Vec3.Zero;
    public AlphaMode AlphaMode { get; set; } = AlphaMode.Opaque;
    public float AlphaCutoff { get; set; } = 0.5f;
    public bool DoubleSided { get; set; }
    public RenderEffect Effect { get; set; } = RenderEffect.Standard;

    // Optional base-color texture (RGBA8, owned).
    public byte[]? BaseColorPixels { get; set; }
    public uint BaseColorWidth { get; set; }
    public uint BaseColorHeight { get; set; }

    // Optional normal map texture (RGBA8, owned).
    public byte[]? NormalPixels { get; set; }
    public uint NormalWidth { get; set; }
    public uint NormalHeight { get; set; }

    // ── Factories ─────────────────────────────────────────────────────────────

    public static Material3D Flat(Vec4 color) => new() { BaseColorFactor = color };

    public static Material3D FromPixels(string name, byte[] pixels, uint width, uint height)
    {
        if (width == 0 || height == 0) throw new ArgumentException("Invalid image size.");
        if ((ulong)pixels.Length != (ulong)width * height * 4)
            throw new ArgumentException("Invalid pixel data length.");
        return new Material3D {
            Name = name,
            BaseColorFactor = new Vec4(
                x: 1,
                y: 1,
                z: 1,
                w: 1
            ),
            BaseColorPixels = (byte[])pixels.Clone(),
            BaseColorWidth = width,
            BaseColorHeight = height,
        };
    }
}
