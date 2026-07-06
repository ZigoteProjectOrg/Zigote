using System.Reflection;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.UI.Semantics;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     Displays an image loaded from a file path, memory, or embedded asset.
///     The image is decoded natively by the engine and cached.
/// </summary>
public sealed class Image : RenderWidget
{
    private readonly uint _texHeight;
    private readonly ulong _textureHandle;
    private readonly uint _texWidth;

    private Size _size;

    public Image(string path)
    {
        var absPath = Path.GetFullPath(path);
        _textureHandle = ZigoteEngine.LoadTexture(absPath, out _texWidth, out _texHeight);
    }

    private Image(ulong handle, uint width, uint height)
    {
        _textureHandle = handle;
        _texWidth = width;
        _texHeight = height;
    }

    /// <summary>
    ///     Accessible description of the image. <c>null</c> (the default) marks the image as decorative,
    ///     omitting it from the accessibility tree; set it to announce the image to assistive tech.
    /// </summary>
    public string? AltText { get; set; }

    public override bool ExcludeSemantics => AltText is null;

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Image;
        config.Label = AltText;
    }

    public static Image FromFile(string path)
    {
        return new Image(path);
    }

    public static Image FromBytes(ReadOnlySpan<byte> data)
    {
        var handle = ZigoteEngine.LoadTextureFromMemory(data, out var w, out var h);
        Console.WriteLine(
            $"Image.FromBytes: Loaded texture. handle={handle}, w={w}, h={h}, len={data.Length}"
        );
        return new Image(handle, w, h);
    }

    public static Image FromAsset(string resourceName, Type? assemblyType = null)
    {
        var asm = assemblyType?.Assembly ?? Assembly.GetCallingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            var allNames = string.Join(", ", asm.GetManifestResourceNames());
            throw new FileNotFoundException(
                $"Resource '{resourceName}' not found. Available: {allNames}"
            );
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return FromBytes(ms.ToArray());
    }

    public override Size Measure(Constraints c)
    {
        if (_textureHandle == 0) return c.Constrain(Size.Zero);

        var aspect = (float)_texWidth / _texHeight;
        float w = _texWidth;
        float h = _texHeight;

        if (w > c.MaxWidth)
        {
            w = c.MaxWidth;
            h = w / aspect;
        }

        if (h > c.MaxHeight)
        {
            h = c.MaxHeight;
            w = h * aspect;
        }

        _size = c.Constrain(new Size(w, h));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        if (_textureHandle != 0)
            paint.AddImage(
                Bounds,
                (int)_texWidth,
                (int)_texHeight,
                null,
                _textureHandle
            );
    }
}