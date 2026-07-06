using Zigote.UI.Host;

namespace Zigote.UI.Material.FilePicker;

public sealed class FileGrid : RenderWidget
{
    private const float TileWidth = 96f;
    private const float TileHeight = 80f;
    private const float Spacing = 6f;
    private const float FallbackWidth = 480f;
    private readonly Action<string> _onConfirmed;
    private readonly Action<string> _onSelected;
    private readonly Func<string?> _selectedFile;

    private readonly List<FileTile> _tiles = [];
    private Size _size;

    public FileGrid(
        Action<string> onSelected,
        Action<string> onConfirmed,
        Func<string?> selectedFile)
    {
        _onSelected = onSelected;
        _onConfirmed = onConfirmed;
        _selectedFile = selectedFile;
    }

    public void SetFiles(IEnumerable<string> files)
    {
        // Detach old tiles
        foreach (var tile in _tiles) tile.Detach();
        _tiles.Clear();

        foreach (var file in files)
        {
            var tile = new FileTile(
                file,
                Path.GetFileName(file),
                () => _onSelected(file),
                () => _onConfirmed(file),
                () => _selectedFile() == file
            );

            // Attach new tile if owner is already set
            if (Owner != null) tile.Attach(Owner, this);

            _tiles.Add(tile);
        }

        MarkNeedsLayout();
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        foreach (var tile in _tiles) tile.Attach(owner, this);
    }

    public override void Detach()
    {
        foreach (var tile in _tiles) tile.Detach();
        base.Detach();
    }

    public override Size Measure(Constraints c)
    {
        foreach (var tile in _tiles) tile.Measure(Constraints.Tight(TileWidth, TileHeight));

        var width = ResolveWidth(c.MaxWidth);
        var columns = CalculateColumnCount(width);

        var rows = _tiles.Count == 0
            ? 0
            : (int)MathF.Ceiling(_tiles.Count / (float)columns);

        var height = rows * (TileHeight + Spacing) + Spacing * 2f;

        _size = c.Constrain(new Size(width, height));
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

        var columns = CalculateColumnCount(_size.Width);

        for (var i = 0; i < _tiles.Count; i++)
        {
            var col = i % columns;
            var row = i / columns;

            var x = origin.X + Spacing + col * (TileWidth + Spacing);
            var y = origin.Y + Spacing + row * (TileHeight + Spacing);

            _tiles[i].Layout(new Offset(x, y));
        }
    }

    public override void Paint(PaintList paint)
    {
        foreach (var tile in _tiles) tile.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;

        for (var i = _tiles.Count - 1; i >= 0; i--)
        {
            var hit = _tiles[i].HitTest(point);
            if (hit is not null) return hit;
        }

        return null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return _tiles;
    }

    private static int CalculateColumnCount(float width)
    {
        var contentWidth = MathF.Max(0f, width - Spacing * 2f);
        return Math.Max(1, (int)MathF.Floor(contentWidth / (TileWidth + Spacing)));
    }

    private static float ResolveWidth(float maxWidth)
    {
        if (float.IsNaN(maxWidth)) return FallbackWidth;
        if (float.IsInfinity(maxWidth)) return FallbackWidth;

        return MathF.Max(0f, maxWidth);
    }
}