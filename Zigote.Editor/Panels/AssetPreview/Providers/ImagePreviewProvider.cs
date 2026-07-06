using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Panels.AssetPreview.Providers;

/// <summary>
///     Previews raster image assets by uploading them through the engine texture loader and drawing
///     them fit-contained over an alpha checkerboard.
/// </summary>
public sealed class ImagePreviewProvider : IAssetPreviewProvider
{
    private static readonly string[] Exts =
        [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".hdr", ".tga"];

    public bool CanHandle(string ext)
    {
        return Array.IndexOf(Exts, ext) >= 0;
    }

    public Widget BuildPreview(string path, ThemeData theme)
    {
        return new ImagePreviewWidget(path, theme);
    }

    public IEnumerable<(string Key, string Value)> ExtraMetadata(string path)
    {
        var (w, h) = TryDimensions(path);
        if (w > 0 && h > 0)
            yield return ("Dimensions", $"{w} × {h}");
    }

    internal static (uint W, uint H) TryDimensions(string path)
    {
        try
        {
            if (ZigoteEngine.Instance is null) return (0, 0);
            var handle = ZigoteEngine.LoadTexture(path, out var w, out var h);
            return handle != 0 ? (w, h) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }
}

/// <summary>Leaf widget: alpha checkerboard background + fit-contained engine texture blit.</summary>
internal sealed class ImagePreviewWidget : Widget
{
    private const float CheckerSize = 10f;
    private readonly string _path;
    private ulong _handle;

    private bool _loaded;
    private Size _size;
    private uint _texW, _texH;
    private ThemeData _theme;

    public ImagePreviewWidget(string path, ThemeData theme)
    {
        _path = path;
        _theme = theme;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (ZigoteEngine.Instance is not null)
                _handle = ZigoteEngine.LoadTexture(_path, out _texW, out _texH);
        }
        catch
        {
            _handle = 0;
        }
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        EnsureLoaded();
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f;
        var h = float.IsFinite(c.MaxHeight) ? MathF.Max(120f, c.MaxHeight) : 220f;
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
        if (!paint.IsVisible(Bounds)) return;

        // Surface + checkerboard so transparent regions read as transparent.
        paint.AddRect(Bounds, _theme.SurfaceAlt, 6f);
        paint.AddClipStart(Bounds);
        var dark = _theme.Background;
        var light = _theme.Surface.Lighten(0.04f);
        var cols = (int)MathF.Ceiling(Bounds.Width / CheckerSize);
        var rows = (int)MathF.Ceiling(Bounds.Height / CheckerSize);
        for (var ry = 0; ry < rows; ry++)
        for (var cx = 0; cx < cols; cx++)
        {
            var col = (cx + ry) % 2 == 0 ? dark : light;
            var cell = new Rect(
                Bounds.X + cx * CheckerSize,
                Bounds.Y + ry * CheckerSize,
                CheckerSize,
                CheckerSize
            );
            paint.AddRect(cell, col);
        }

        if (_handle != 0 && _texW > 0 && _texH > 0)
        {
            var fit = FitContain(Bounds, _texW, _texH);
            paint.AddImage(
                fit,
                (int)_texW,
                (int)_texH,
                null,
                _handle
            );
        }
        else
        {
            const string msg = "preview unavailable";
            var tx = Bounds.X + (Bounds.Width - msg.Length * 5.5f) * 0.5f;
            var ty = Bounds.Y + Bounds.Height * 0.5f;
            paint.AddText(
                msg,
                tx,
                ty,
                _theme.TextMuted,
                _theme.FontSizeCaption
            );
        }

        paint.AddClipEnd();
        paint.AddBorder(Bounds, _theme.Separator, 6f);
    }

    private static Rect FitContain(Rect box, uint texW, uint texH)
    {
        const float pad = 8f;
        var availW = MathF.Max(1f, box.Width - pad * 2f);
        var availH = MathF.Max(1f, box.Height - pad * 2f);
        var scale = MathF.Min(availW / texW, availH / texH);
        var w = texW * scale;
        var h = texH * scale;
        var x = box.X + (box.Width - w) * 0.5f;
        var y = box.Y + (box.Height - h) * 0.5f;
        return new Rect(
            x,
            y,
            w,
            h
        );
    }
}