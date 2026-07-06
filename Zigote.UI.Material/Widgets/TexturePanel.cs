namespace Zigote.UI.Material;

/// <summary>
///     A widget that composites an offscreen 3D scene render into the 2D UI.
/// </summary>
public class TexturePanel(ulong textureId) : RenderWidget
{
    private Size _size;

    /// <summary>
    ///     The cache key / ID of the wgpu texture containing the 3D scene render.
    /// </summary>
    public ulong TextureId { get; set; } = textureId;

    public override Size Measure(Constraints c)
    {
        // Viewports typically expand to fill available space
        _size = c.Constrain(new Size(float.PositiveInfinity, float.PositiveInfinity));
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
        // Render the 3D scene texture as a full-size image widget.
        // We assume the native side will provide the texture contents via the TextureId.
        paint.AddImage(
            Bounds,
            (int)_size.Width,
            (int)_size.Height,
            null,
            TextureId
        );
    }
}