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

    public bool CanHandle(string ext) => Array.IndexOf(array: Exts, value: ext) >= 0;

    public Widget BuildPreview(string path, ThemeData theme) =>
        new ImagePreviewWidget(path: path, theme: theme);

    public IEnumerable<(string Key, string Value)> ExtraMetadata(string path)
    {
        (uint w, uint h) = TryDimensions(path);
        if (w > 0 && h > 0)
            yield return ("Dimensions", $"{w} × {h}");
    }

    /// <summary>
    ///     The image's pixel size, for the metadata list.
    ///     <para>
    ///         There is no measure-without-decode call, so this uploads and immediately releases.
    ///         Texture handles are owned by whoever created them and nothing else frees them — keeping
    ///         this one would strand a full-size texture per image whose metadata was ever shown, for
    ///         the editor's lifetime.
    ///     </para>
    /// </summary>
    internal static (uint W, uint H) TryDimensions(string path)
    {
        ulong handle = 0UL;
        try
        {
            if (ZigoteEngine.Instance is null) return (0, 0);
            handle = ZigoteEngine.LoadTexture(path: path, outW: out uint w, outH: out uint h);
            return handle != 0 ? (w, h) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
        finally
        {
            if (handle != 0) ZigoteEngine.ReleaseTexture(handle);
        }
    }
}

/// <summary>
///     Leaf widget: alpha checkerboard background + fit-contained engine texture blit.
///     <para>
///         Owns the texture it loads, so it is <see cref="IDisposable" /> — the panel builds a new one
///         per selection, and without a release each browsed image left its full-size texture resident
///         forever. At 2000×3000 that is 24 MB a click.
///     </para>
/// </summary>
internal sealed class ImagePreviewWidget : Widget, IDisposable
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

    /// <summary>Free the texture. Idempotent, and safe to call mid-frame (the engine defers the free).</summary>
    public void Dispose()
    {
        if (_handle == 0) return;
        ZigoteEngine.ReleaseTexture(_handle);
        _handle = 0;
        _texW = _texH = 0;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (ZigoteEngine.Instance is not null)
                _handle = ZigoteEngine.LoadTexture(path: _path, outW: out _texW, outH: out _texH);
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
        float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 240f;
        float h = float.IsFinite(c.MaxHeight) ? MathF.Max(x: 120f, y: c.MaxHeight) : 220f;
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
    }

    public override void Paint(PaintList paint)
    {
        if (!paint.IsVisible(Bounds)) return;

        // Surface + checkerboard so transparent regions read as transparent.
        paint.AddRect(bounds: Bounds, color: _theme.SurfaceAlt, radius: 6f);
        paint.AddClipStart(Bounds);
        var dark = _theme.Background;
        var light = _theme.Surface.Lighten(0.04f);
        int cols = (int)MathF.Ceiling(Bounds.Width / CheckerSize);
        int rows = (int)MathF.Ceiling(Bounds.Height / CheckerSize);
        for (int ry = 0; ry < rows; ry++)
        for (int cx = 0; cx < cols; cx++)
        {
            var col = (cx + ry) % 2 == 0 ? dark : light;
            var cell = new Rect(
                x: Bounds.X + (cx * CheckerSize),
                y: Bounds.Y + (ry * CheckerSize),
                width: CheckerSize,
                height: CheckerSize
            );
            paint.AddRect(bounds: cell, color: col);
        }

        if (_handle != 0 && _texW > 0 && _texH > 0)
        {
            var fit = FitContain(box: Bounds, texW: _texW, texH: _texH);
            paint.AddImage(
                bounds: fit,
                pixelWidth: (int)_texW,
                pixelHeight: (int)_texH,
                pixels: null,
                cacheKey: _handle
            );
        }
        else
        {
            const string msg = "preview unavailable";
            float tx = Bounds.X + ((Bounds.Width - (msg.Length * 5.5f)) * 0.5f);
            float ty = Bounds.Y + (Bounds.Height * 0.5f);
            paint.AddText(
                text: msg,
                baselineX: tx,
                baselineY: ty,
                color: _theme.TextMuted,
                fontSize: _theme.FontSizeCaption
            );
        }

        paint.AddClipEnd();
        paint.AddBorder(bounds: Bounds, color: _theme.Separator, radius: 6f);
    }

    private static Rect FitContain(Rect box, uint texW, uint texH)
    {
        const float pad = 8f;
        float availW = MathF.Max(x: 1f, y: box.Width - (pad * 2f));
        float availH = MathF.Max(x: 1f, y: box.Height - (pad * 2f));
        float scale = MathF.Min(x: availW / texW, y: availH / texH);
        float w = texW * scale;
        float h = texH * scale;
        float x = box.X + ((box.Width - w) * 0.5f);
        float y = box.Y + ((box.Height - h) * 0.5f);
        return new Rect(
            x: x,
            y: y,
            width: w,
            height: h
        );
    }
}
