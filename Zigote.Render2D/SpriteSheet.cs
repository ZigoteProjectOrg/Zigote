namespace Zigote.Render2D;

/// <summary>A texture sliced into indexed frames (grid-based sprite sheets, flipbooks).</summary>
public sealed class SpriteSheet
{
    private readonly SpriteFrame[] _frames;

    private SpriteSheet(SpriteTexture texture, SpriteFrame[] frames)
    {
        Texture = texture;
        _frames = frames;
    }

    public SpriteTexture Texture { get; }
    public IReadOnlyList<SpriteFrame> Frames => _frames;
    public int FrameCount => _frames.Length;

    public SpriteFrame Frame(int index)
    {
        if (_frames.Length == 0) return SpriteFrame.Full;
        return _frames[Math.Clamp(index, 0, _frames.Length - 1)];
    }

    public static SpriteSheet FromGrid(SpriteTexture texture, int cols, int rows,
        int marginX = 0, int marginY = 0, int spacingX = 0, int spacingY = 0)
    {
        return new SpriteSheet(
            texture,
            GridFrames(
                texture.Width,
                texture.Height,
                cols,
                rows,
                marginX,
                marginY,
                spacingX,
                spacingY
            )
        );
    }

    /// <summary>
    ///     Row-major grid frames as pure math (headless-testable; the scene-node path uses this
    ///     directly). Cell size = (tex - 2·margin - (n-1)·spacing) / n per axis; frame pixel size is
    ///     the computed cell size.
    /// </summary>
    public static SpriteFrame[] GridFrames(int texWidth, int texHeight, int cols, int rows,
        int marginX = 0, int marginY = 0, int spacingX = 0, int spacingY = 0)
    {
        if (texWidth <= 0 || texHeight <= 0 || cols <= 0 || rows <= 0) return [];

        var cellW = (texWidth - 2 * marginX - (cols - 1) * spacingX) / cols;
        var cellH = (texHeight - 2 * marginY - (rows - 1) * spacingY) / rows;
        var invW = 1f / texWidth;
        var invH = 1f / texHeight;

        var frames = new SpriteFrame[cols * rows];
        for (var row = 0; row < rows; row++)
        for (var col = 0; col < cols; col++)
        {
            var x = marginX + col * (cellW + spacingX);
            var y = marginY + row * (cellH + spacingY);
            frames[row * cols + col] = new SpriteFrame(
                x * invW,
                y * invH,
                (x + cellW) * invW,
                (y + cellH) * invH,
                cellW,
                cellH
            );
        }

        return frames;
    }
}
