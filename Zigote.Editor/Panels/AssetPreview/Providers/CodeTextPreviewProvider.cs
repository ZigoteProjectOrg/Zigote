using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels.AssetPreview.Providers;

/// <summary>
///     Previews text/code assets in a read-only monospace view (Iosevka via fontFamily "code"),
///     capped to a sensible number of lines.
/// </summary>
public sealed class CodeTextPreviewProvider : IAssetPreviewProvider
{
    private const int MaxLines = 200;

    private static readonly string[] Exts = [
        ".cs", ".wgsl", ".zig", ".lua", ".json", ".scene", ".txt", ".md", ".glsl", ".hlsl", ".xml",
        ".yaml", ".yml",
        ".toml", ".ini", ".cfg",
    ];

    public bool CanHandle(string ext)
    {
        return Array.IndexOf(Exts, ext) >= 0;
    }

    public Widget BuildPreview(string path, ThemeData theme)
    {
        var lines = ReadLines(
            path,
            MaxLines,
            out _,
            out _
        );
        return new ReadOnlyTextView(lines, theme);
    }

    public IEnumerable<(string Key, string Value)> ExtraMetadata(string path)
    {
        ReadLines(
            path,
            int.MaxValue,
            out var lineCount,
            out var charCount
        );
        yield return ("Lines", lineCount.ToString());
        yield return ("Characters", charCount.ToString());
    }

    private static List<string> ReadLines(string path, int cap, out int lineCount,
        out int charCount)
    {
        var lines = new List<string>();
        lineCount = 0;
        charCount = 0;
        try
        {
            using var reader = new StreamReader(path);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineCount++;
                charCount += line.Length;
                if (lines.Count < cap)
                    // Expand tabs so AddText's monospace advance stays aligned.
                    lines.Add(line.Replace("\t", "    "));
            }
        }
        catch
        {
            if (lines.Count == 0) lines.Add("(unable to read file)");
        }

        return lines;
    }
}

/// <summary>
///     Leaf widget: a read-only, vertically-scrollable monospace text view with a gutter line count.
///     Wheel/trackpad scrolls; no editing, caret or selection.
/// </summary>
internal sealed class ReadOnlyTextView : Widget
{
    private const float LineHeight = 16f;
    private const float FontSize = 12f;
    private const float PadX = 8f;
    private const float PadY = 6f;
    private const float GutterW = 38f;

    private readonly List<string> _lines;
    private float _scrollY;
    private Size _size;
    private ThemeData _theme;

    public ReadOnlyTextView(List<string> lines, ThemeData theme)
    {
        _lines = lines;
        _theme = theme;
    }

    private float ContentHeight => _lines.Count * LineHeight + PadY * 2f;

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        var w = float.IsInfinity(c.MaxWidth) ? 320f : c.MaxWidth;
        var h = float.IsInfinity(c.MaxHeight) ? 320f : MathF.Min(c.MaxHeight, 360f);
        _size = c.Constrain(new Size(w, MathF.Max(h, c.MinHeight)));
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
        ClampScroll();
    }

    private void ClampScroll()
    {
        var max = MathF.Max(0f, ContentHeight - Bounds.Height);
        _scrollY = Math.Clamp(_scrollY, 0f, max);
    }

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;

        paint.AddRect(Bounds, _theme.Background, 6f);
        paint.AddClipStart(Bounds);

        // Gutter.
        var gutter = new Rect(
            Bounds.X,
            Bounds.Y,
            GutterW,
            Bounds.Height
        );
        paint.AddRect(gutter, _theme.SurfaceAlt);

        var first = Math.Max(0, (int)((_scrollY - PadY) / LineHeight));
        var visibleRows = (int)(Bounds.Height / LineHeight) + 2;
        var last = Math.Min(_lines.Count, first + visibleRows);

        for (var i = first; i < last; i++)
        {
            var top = Bounds.Y + PadY + i * LineHeight - _scrollY;
            var baseline = top + FontSize * 0.8f;

            var num = (i + 1).ToString();
            var numX = Bounds.X + GutterW - 6f - num.Length * 7f;
            paint.AddText(
                num,
                numX,
                baseline,
                _theme.TextMuted,
                FontSize,
                fontFamily: "code"
            );

            paint.AddText(
                _lines[i],
                Bounds.X + GutterW + PadX,
                baseline,
                _theme.OnSurface,
                FontSize,
                fontFamily: "code"
            );
        }

        paint.AddClipEnd();
        paint.AddBorder(Bounds, _theme.Separator, 6f);

        // Slim scroll indicator.
        var max = MathF.Max(0f, ContentHeight - Bounds.Height);
        if (max > 0f)
        {
            var track = Bounds.Height;
            var thumbH = MathF.Max(24f, track * (Bounds.Height / ContentHeight));
            var thumbY = Bounds.Y + (track - thumbH) * (_scrollY / max);
            var thumb = new Rect(
                Bounds.Right - 5f,
                thumbY,
                3f,
                thumbH
            );
            paint.AddRect(thumb, _theme.OnSurface.WithAlpha(0.25f), 1.5f);
        }
    }

    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

    public override void OnScroll(float dx, float dy)
    {
        var max = MathF.Max(0f, ContentHeight - Bounds.Height);
        if (max <= 0f) return;
        _scrollY = Math.Clamp(_scrollY - dy * 40f, 0f, max);
        MarkNeedsPaint();
    }
}
