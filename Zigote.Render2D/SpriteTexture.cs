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
        0f,
        0f,
        1f,
        1f,
        Width,
        Height
    );

    public static SpriteTexture? Load(ISpriteDevice device, string path,
        SpriteFilter filter = SpriteFilter.Linear, bool srgb = true,
        SpriteWrap wrap = SpriteWrap.Clamp)
    {
        var handle = device.CreateTextureFromFile(
            path,
            filter,
            srgb,
            wrap,
            out var width,
            out var height
        );
        return handle == 0
            ? null
            : new SpriteTexture(
                device,
                handle,
                width,
                height
            );
    }

    public static SpriteTexture? FromPixels(ISpriteDevice device, ReadOnlySpan<byte> rgba,
        int width, int height,
        SpriteFilter filter = SpriteFilter.Linear, bool srgb = true,
        SpriteWrap wrap = SpriteWrap.Clamp)
    {
        var handle = device.CreateTexture(
            rgba,
            width,
            height,
            filter,
            srgb,
            wrap
        );
        return handle == 0
            ? null
            : new SpriteTexture(
                device,
                handle,
                width,
                height
            );
    }

    public void Destroy()
    {
        if (Handle == 0) return;
        _device.DestroyTexture(Handle);
        Handle = 0;
    }
}