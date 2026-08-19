using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.Svg;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     Draws an SVG, rasterized by resvg at exactly the pixels it occupies — flutter_svg's
///     <c>SvgPicture</c>. Resizing the widget (or moving the window to a different-density display)
///     re-rasterizes, so the result is sharp at any scale, unlike an <see cref="Image" /> of a PNG.
/// </summary>
/// <remarks>
///     <para>
///         <b>Dispose it</b> — like <see cref="Image" />, the texture it holds is freed by nothing
///         else. <see cref="Dispose" /> also disposes the document when this widget parsed it
///         itself (the <c>FromX</c> factories); an <see cref="SvgAsset" /> handed in through the
///         constructor stays the caller's, so one parsed icon can back a hundred rows.
///     </para>
///     <para>
///         Rasterizing happens on the UI thread during layout. An icon is tens of microseconds; a
///         full-page illustration at 4K is not — parse it into an <see cref="SvgAsset" /> and
///         rasterize on a worker thread, then <see cref="Image.SetTexture" /> it.
///     </para>
/// </remarks>
public sealed class SvgPicture : Widget, IDisposable
{
    private readonly SvgAsset _asset;
    private readonly bool _ownsAsset;

    private bool _disposed;
    private Size _size;
    private uint _texHeight;
    private uint _texWidth;
    private ulong _textureHandle;

    /// <summary>Draw an asset owned by the caller — the form to use for a shared icon.</summary>
    public SvgPicture(SvgAsset asset)
    {
        _asset = asset;
        _ownsAsset = false;
    }

    private SvgPicture(SvgAsset asset, bool owns)
    {
        _asset = asset;
        _ownsAsset = owns;
    }

    /// <summary>
    ///     Logical width to draw at. With only one of <see cref="Width" />/<see cref="Height" /> set
    ///     the other follows the document's aspect ratio; with neither, the document's own size is
    ///     the preferred size (still subject to the incoming constraints).
    /// </summary>
    public float? Width { get; init; }

    /// <inheritdoc cref="Width" />
    public float? Height { get; init; }

    /// <summary>
    ///     Tint multiplied over every pixel — flutter_svg's <c>colorFilter</c>, the usual way to
    ///     recolor a monochrome icon. Null paints the document's own colors.
    /// </summary>
    public Color? ColorFilter { get; init; }

    /// <summary>
    ///     Accessible description. Null (the default) marks the picture decorative and hides it
    ///     from assistive tech — right for an icon that sits next to its own label.
    /// </summary>
    public string? AltText { get; init; }

    public override bool ExcludeSemantics => AltText is null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_textureHandle != 0)
        {
            ZigoteEngine.ReleaseTexture(_textureHandle);
            _textureHandle = 0;
        }

        if (_ownsAsset) _asset.Dispose();
    }

    public static SvgPicture FromString(string svg) =>
        new(asset: SvgAsset.FromString(svg), owns: true);

    public static SvgPicture FromBytes(ReadOnlySpan<byte> svg) =>
        new(asset: SvgAsset.FromBytes(svg), owns: true);

    public static SvgPicture FromFile(string path) =>
        new(asset: SvgAsset.FromFile(path), owns: true);

    /// <summary>
    ///     An SVG from the app's deployed <c>Assets/</c> tree — <c>SvgPicture.FromAsset("icons/logo.svg")</c>.
    /// </summary>
    public static SvgPicture FromAsset(string relativePath) =>
        new(asset: SvgAsset.FromAsset(relativePath), owns: true);

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Image;
        config.Label = AltText;
    }

    public override Size Measure(Constraints c)
    {
        Size natural = _asset.IntrinsicSize;
        float aspect = natural.Height > 0 ? natural.Width / natural.Height : 1f;

        float w = Width ?? (Height is { } h0 ? h0 * aspect : natural.Width);
        float h = Height ?? (Width is { } w0 ? w0 / aspect : natural.Height);

        // Same shrink-to-fit as Image: overflow the constraints on either axis and the whole
        // picture scales down, rather than being cropped.
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

        _size = c.Constrain(new Size(width: w, height: h));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        Rasterize();
    }

    public override void Paint(PaintList paint)
    {
        if (_textureHandle == 0) return;
        paint.AddImage(
            bounds: Bounds,
            pixelWidth: (int)_texWidth,
            pixelHeight: (int)_texHeight,
            pixels: null,
            cacheKey: _textureHandle,
            tint: ColorFilter
        );
    }

    /// <summary>
    ///     Rasterize at the physical pixel size the widget now occupies, reusing the existing
    ///     texture when that size did not change — which is every frame that is not a resize.
    /// </summary>
    private void Rasterize()
    {
        if (_disposed) return;

        float scale = App.Active?.HostScale ?? 1f;
        uint w = (uint)MathF.Round(_size.Width * scale);
        uint h = (uint)MathF.Round(_size.Height * scale);
        if (w == 0 || h == 0) return;
        if (w == _texWidth && h == _texHeight) return;

        // ponytail: a size that changes every frame (an animated or dragged picture) therefore
        // creates and releases a texture every frame, which the engine calls out as churn worth
        // avoiding — see VideoPlayer, which overwrites a fixed-size texture instead. The exact
        // path is the right default (a picture at rest is exact to the pixel); if an app animates
        // one continuously and it shows, quantize the size to a step and let the quad scale
        // between steps, or share a raster cache across pictures of the same document.

        ulong handle = _asset.CreateTexture(width: (int)w, height: (int)h);
        if (handle == 0) return;

        if (_textureHandle != 0) ZigoteEngine.ReleaseTexture(_textureHandle);
        _textureHandle = handle;
        _texWidth = w;
        _texHeight = h;
    }
}
