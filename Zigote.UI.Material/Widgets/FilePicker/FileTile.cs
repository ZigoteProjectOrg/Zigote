namespace Zigote.UI.Material.FilePicker;

public sealed class FileTile : LeafWidget
{
    private const double DoubleClickMilliseconds = 300;
    private readonly Func<bool> _isSelected;
    private readonly string _name;
    private readonly Action _onConfirmed;
    private readonly Action _onSelected;

    private readonly string _relPath;

    private bool _hovered;
    private DateTime _lastClickTime = DateTime.MinValue;
    private Size _size;
    private ThemeData _theme = ThemeData.Dark;

    public FileTile(
        string relPath,
        string name,
        Action onSelected,
        Action onConfirmed,
        Func<bool> isSelected)
    {
        _relPath = relPath;
        _name = name;
        _onSelected = onSelected;
        _onConfirmed = onConfirmed;
        _isSelected = isSelected;
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
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
        var selected = _isSelected();

        var background = selected
            ? _theme.Primary.WithAlpha(0.2f)
            : _hovered
                ? _theme.SurfaceAlt
                : _theme.Surface;

        var border = selected
            ? _theme.Primary
            : _theme.OnSurface.WithAlpha(0.12f);

        paint.AddRect(Bounds, background, 4f);
        paint.AddBorder(
            Bounds,
            border,
            4f,
            selected ? 1.5f : 1f
        );

        PaintIcon(paint);
        PaintFileName(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

    public override void OnPointerEnter()
    {
        if (_hovered) return;

        _hovered = true;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        if (!_hovered) return;

        _hovered = false;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        var now = DateTime.UtcNow;
        var elapsedMs = (now - _lastClickTime).TotalMilliseconds;

        _lastClickTime = now;

        if (elapsedMs <= DoubleClickMilliseconds)
        {
            _onConfirmed();
            return;
        }

        _onSelected();
    }

    private void PaintIcon(PaintList paint)
    {
        var ext = Path.GetExtension(_relPath).ToLowerInvariant();

        var icon = GetIcon(ext);
        var color = GetIconColor(ext);
        var fontSize = _theme.FontSizeCaption + 1f;

        var centerX = Bounds.X + Bounds.Width / 2f;
        var y = Bounds.Y + 12f;

        var width = EstimateTextWidth(icon, fontSize);
        paint.AddText(
            icon,
            centerX - width / 2f,
            y,
            color,
            fontSize
        );
    }

    private void PaintFileName(PaintList paint)
    {
        var displayName = Ellipsize(_name, 10);
        var fontSize = _theme.FontSizeCaption - 1f;

        var centerX = Bounds.X + Bounds.Width / 2f;
        var y = Bounds.Y + 44f;

        var width = EstimateTextWidth(displayName, fontSize);
        paint.AddText(
            displayName,
            centerX - width / 2f,
            y,
            _theme.OnSurface,
            fontSize
        );
    }

    private static string GetIcon(string ext)
    {
        return ext switch {
            ".glb" or ".fbx" or ".obj" => "[3D]",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => "[TX]",
            ".cs" or ".fs" or ".zig" or ".lua" => "[CS]",
            ".wav" or ".ogg" or ".mp3" => "[AU]",
            _ => "[  ]",
        };
    }

    private static Color GetIconColor(string ext)
    {
        return ext switch {
            ".glb" or ".fbx" or ".obj" => new Color(0.4f, 0.75f, 1f),
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => new Color(0.4f, 0.9f, 0.5f),
            ".cs" or ".fs" or ".zig" or ".lua" => new Color(0.75f, 0.45f, 1f),
            ".wav" or ".ogg" or ".mp3" => new Color(1f, 0.88f, 0.3f),
            _ => new Color(0.6f, 0.6f, 0.6f),
        };
    }

    private static string Ellipsize(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        if (maxLength <= 3) return "...";

        return value[..(maxLength - 3)] + "...";
    }

    private static float EstimateTextWidth(string text, float fontSize)
    {
        return text.Length * fontSize * 0.55f;
    }
}