using Zigote.Core.Engine;

namespace Zigote.UI.Material;

/// <summary>
///     The browser's preview pane: an aspect-fit render of the single selected image plus its
///     metadata (dimensions, size, modified). Decodes through the engine's image loader, so it
///     previews everything the engine can ingest — including .hdr and .tga, which OS dialogs
///     can't show. Textures are decoded scaled-down once per path and kept in a small cache
///     (engine texture handles have process lifetime; the cache stops re-uploads, not leaks).
/// </summary>
internal sealed class FileBrowserPreview : Widget
{
    private const uint MaxDecodeDim = 512;
    private const int CacheCap = 48;

    private static readonly Dictionary<string, (ulong Handle, uint W, uint H)> Cache =
        new(StringComparer.Ordinal);

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) {
        "png", "jpg", "jpeg", "webp", "gif", "bmp", "tga", "hdr",
    };

    private readonly FileBrowserEntry _entry;
    private ulong _handle;
    private Size _size;
    private uint _texH;
    private uint _texW;
    private bool _triedLoad;
    private ThemeData _theme = ThemeData.Dark;

    public FileBrowserPreview(FileBrowserEntry entry)
    {
        _entry = entry;
    }

    public static bool CanPreview(string name)
    {
        return ImageExts.Contains(Path.GetExtension(name).TrimStart('.'));
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(new Size(
            float.IsFinite(c.MaxWidth) ? c.MaxWidth : 220f,
            float.IsFinite(c.MaxHeight) ? c.MaxHeight : 300f
        ));
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

    private void EnsureLoaded()
    {
        if (_triedLoad) return;
        _triedLoad = true;
        if (Cache.TryGetValue(_entry.FullPath, out var cached))
        {
            (_handle, _texW, _texH) = cached;
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(_entry.FullPath);
            _handle = ZigoteEngine.LoadTextureFromMemoryScaled(
                bytes,
                MaxDecodeDim,
                out _texW,
                out _texH
            );
        }
        catch
        {
            _handle = 0;
        }

        if (_handle == 0) return;
        if (Cache.Count >= CacheCap) Cache.Clear(); // crude but bounded; re-decode is cheap
        Cache[_entry.FullPath] = (_handle, _texW, _texH);
    }

    public override void Paint(PaintList paint)
    {
        EnsureLoaded();
        var pad = 12f;
        var fs = _theme.FontSizeCaption;
        var imageBox = new Rect(
            Bounds.X + pad,
            Bounds.Y + pad,
            _size.Width - pad * 2f,
            MathF.Max(60f, MathF.Min(_size.Width - pad * 2f, _size.Height * 0.55f))
        );

        if (_handle != 0 && _texW > 0 && _texH > 0)
        {
            var scale = MathF.Min(imageBox.Width / _texW, imageBox.Height / _texH);
            var w = _texW * scale;
            var h = _texH * scale;
            var fit = new Rect(
                imageBox.X + (imageBox.Width - w) / 2f,
                imageBox.Y + (imageBox.Height - h) / 2f,
                w,
                h
            );
            paint.AddRect(imageBox, _theme.PanelSunken, Radii.Sm);
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
            paint.AddRect(imageBox, _theme.PanelSunken, Radii.Sm);
            paint.AddText(
                "No preview",
                imageBox.X + 12f,
                imageBox.Y + imageBox.Height / 2f,
                _theme.TextMuted,
                fs
            );
        }

        var textY = imageBox.Bottom + 18f;
        paint.AddClipStart(Bounds);
        paint.AddText(
            _entry.Name,
            Bounds.X + pad,
            textY,
            _theme.OnSurface,
            fs
        );
        textY += fs + 8f;
        if (_texW > 0)
        {
            paint.AddText(
                $"{_texW} × {_texH}",
                Bounds.X + pad,
                textY,
                _theme.TextSecondary,
                fs
            );
            textY += fs + 6f;
        }

        paint.AddText(
            FileBrowserList.FormatSize(_entry.Size),
            Bounds.X + pad,
            textY,
            _theme.TextSecondary,
            fs
        );
        textY += fs + 6f;
        paint.AddText(
            FileBrowserList.FormatDate(_entry.Modified, DateTime.Now),
            Bounds.X + pad,
            textY,
            _theme.TextMuted,
            fs
        );
        paint.AddClipEnd();
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(_entry.FullPath, _handle);
    }
}
