namespace Zigote.Render2D;

/// <summary>A GPU sprite texture handle with its pixel dimensions.</summary>
public sealed class SpriteTexture
{
    private readonly ISpriteDevice _device;

    private SpriteTexture(ISpriteDevice device, uint handle, int width, int height)
    {
        _device = device;
        Handle = handle;
        Width = width;
        Height = height;
    }

    public uint Handle { get; private set; }
    public int Width { get; }
    public int Height { get; }

    public SpriteFrame FullFrame => new(
        U0: 0f,
        V0: 0f,
        U1: 1f,
        V1: 1f,
        PixelWidth: Width,
        PixelHeight: Height
    );

    public static SpriteTexture? Load(ISpriteDevice device, string path,
        SpriteFilter filter = SpriteFilter.Linear, bool srgb = true,
        SpriteWrap wrap = SpriteWrap.Clamp)
    {
        uint handle = device.CreateTextureFromFile(
            path: path,
            filter: filter,
            srgb: srgb,
            wrap: wrap,
            width: out int width,
            height: out int height
        );
        return handle == 0
            ? null
            : new SpriteTexture(
                device: device,
                handle: handle,
                width: width,
                height: height
            );
    }

    public static SpriteTexture? FromPixels(ISpriteDevice device, ReadOnlySpan<byte> rgba,
        int width, int height,
        SpriteFilter filter = SpriteFilter.Linear, bool srgb = true,
        SpriteWrap wrap = SpriteWrap.Clamp)
    {
        uint handle = device.CreateTexture(
            rgba: rgba,
            width: width,
            height: height,
            filter: filter,
            srgb: srgb,
            wrap: wrap
        );
        return handle == 0
            ? null
            : new SpriteTexture(
                device: device,
                handle: handle,
                width: width,
                height: height
            );
    }

    public void Destroy()
    {
        if (Handle == 0) return;
        _device.DestroyTexture(Handle);
        Handle = 0;
    }
}
