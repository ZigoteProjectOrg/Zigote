namespace Zigote.UI.Material;

/// <summary>
///     A widget that composites an offscreen 3D scene render into the 2D UI.
/// </summary>
public class TexturePanel(ulong textureId) : Widget
{
    private Size _size;

    /// <summary>
    ///     The cache key / ID of the wgpu texture containing the 3D scene render.
    /// </summary>
    public ulong TextureId { get; set; } = textureId;

    public override Size Measure(Constraints c)
    {
        // Viewports typically expand to fill available space
        _size = c.Constrain(
            new Size(width: float.PositiveInfinity, height: float.PositiveInfinity)
        );
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
    }

    public override void Paint(PaintList paint)
    {
        // Render the 3D scene texture as a full-size image widget.
        // We assume the native side will provide the texture contents via the TextureId.
        paint.AddImage(
            bounds: Bounds,
            pixelWidth: (int)_size.Width,
            pixelHeight: (int)_size.Height,
            pixels: null,
            cacheKey: TextureId
        );
    }
}
