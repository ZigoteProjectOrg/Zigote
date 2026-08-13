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

    public bool CanHandle(string ext) => Array.IndexOf(array: Exts, value: ext) >= 0;

    public Widget BuildPreview(string path, ThemeData theme)
    {
        var lines = ReadLines(
            path: path,
            cap: MaxLines,
            lineCount: out _,
            charCount: out _
        );
        return new ReadOnlyTextView(lines: lines, theme: theme);
    }

    public IEnumerable<(string Key, string Value)> ExtraMetadata(string path)
    {
        ReadLines(
            path: path,
            cap: int.MaxValue,
            lineCount: out int lineCount,
            charCount: out int charCount
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
                    lines.Add(line.Replace(oldValue: "\t", newValue: "    "));
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

    private float ContentHeight => (_lines.Count * LineHeight) + (PadY * 2f);

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        float w = float.IsInfinity(c.MaxWidth) ? 320f : c.MaxWidth;
        float h = float.IsInfinity(c.MaxHeight) ? 320f : MathF.Min(x: c.MaxHeight, y: 360f);
        _size = c.Constrain(new Size(width: w, height: MathF.Max(x: h, y: c.MinHeight)));
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
        ClampScroll();
    }

    private void ClampScroll()
    {
        float max = MathF.Max(x: 0f, y: ContentHeight - Bounds.Height);
        _scrollY = Math.Clamp(value: _scrollY, min: 0f, max: max);
    }

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;

        paint.AddRect(bounds: Bounds, color: _theme.Background, radius: 6f);
        paint.AddClipStart(Bounds);

        // Gutter.
        var gutter = new Rect(
            x: Bounds.X,
            y: Bounds.Y,
            width: GutterW,
            height: Bounds.Height
        );
        paint.AddRect(bounds: gutter, color: _theme.SurfaceAlt);

        int first = Math.Max(val1: 0, val2: (int)((_scrollY - PadY) / LineHeight));
        int visibleRows = (int)(Bounds.Height / LineHeight) + 2;
        int last = Math.Min(val1: _lines.Count, val2: first + visibleRows);

        for (int i = first; i < last; i++)
        {
            float top = Bounds.Y + PadY + (i * LineHeight) - _scrollY;
            float baseline = top + (FontSize * 0.8f);

            string num = (i + 1).ToString();
            float numX = Bounds.X + GutterW - 6f - (num.Length * 7f);
            paint.AddText(
                text: num,
                baselineX: numX,
                baselineY: baseline,
                color: _theme.TextMuted,
                fontSize: FontSize,
                fontFamily: "code"
            );

            paint.AddText(
                text: _lines[i],
                baselineX: Bounds.X + GutterW + PadX,
                baselineY: baseline,
                color: _theme.OnSurface,
                fontSize: FontSize,
                fontFamily: "code"
            );
        }

        paint.AddClipEnd();
        paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: 6f);

        // Slim scroll indicator.
        float max = MathF.Max(x: 0f, y: ContentHeight - Bounds.Height);
        if (max > 0f)
        {
            float track = Bounds.Height;
            float thumbH = MathF.Max(x: 24f, y: track * (Bounds.Height / ContentHeight));
            float thumbY = Bounds.Y + ((track - thumbH) * (_scrollY / max));
            var thumb = new Rect(
                x: Bounds.Right - 5f,
                y: thumbY,
                width: 3f,
                height: thumbH
            );
            paint.AddRect(bounds: thumb, color: _theme.OnSurface.WithAlpha(0.25f), radius: 1.5f);
        }
    }

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? this : null;

    public override void OnScroll(float dx, float dy)
    {
        float max = MathF.Max(x: 0f, y: ContentHeight - Bounds.Height);
        if (max <= 0f) return;
        _scrollY = Math.Clamp(value: _scrollY - (dy * 40f), min: 0f, max: max);
        MarkNeedsPaint();
    }
}
