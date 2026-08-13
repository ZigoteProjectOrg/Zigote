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
        "png",
        "jpg",
        "jpeg",
        "webp",
        "gif",
        "bmp",
        "tga",
        "hdr",
    };

    private readonly FileBrowserEntry _entry;
    private ulong _handle;
    private Size _size;
    private uint _texH;
    private uint _texW;
    private ThemeData _theme = ThemeData.Dark;
    private bool _triedLoad;

    public FileBrowserPreview(FileBrowserEntry entry) => _entry = entry;

    public static bool CanPreview(string name) =>
        ImageExts.Contains(Path.GetExtension(name).TrimStart('.'));

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _size = c.Constrain(
            new Size(
                width: float.IsFinite(c.MaxWidth) ? c.MaxWidth : 220f,
                height: float.IsFinite(c.MaxHeight) ? c.MaxHeight : 300f
            )
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

    private void EnsureLoaded()
    {
        if (_triedLoad) return;
        _triedLoad = true;
        if (Cache.TryGetValue(key: _entry.FullPath, value: out var cached))
        {
            (_handle, _texW, _texH) = cached;
            return;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(_entry.FullPath);
            _handle = ZigoteEngine.LoadTextureFromMemoryScaled(
                data: bytes,
                maxDim: MaxDecodeDim,
                outW: out _texW,
                outH: out _texH
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
        float pad = 12f;
        float fs = _theme.FontSizeCaption;
        var imageBox = new Rect(
            x: Bounds.X + pad,
            y: Bounds.Y + pad,
            width: _size.Width - (pad * 2f),
            height: MathF.Max(
                x: 60f,
                y: MathF.Min(x: _size.Width - (pad * 2f), y: _size.Height * 0.55f)
            )
        );

        if (_handle != 0 && _texW > 0 && _texH > 0)
        {
            float scale = MathF.Min(x: imageBox.Width / _texW, y: imageBox.Height / _texH);
            float w = _texW * scale;
            float h = _texH * scale;
            var fit = new Rect(
                x: imageBox.X + ((imageBox.Width - w) / 2f),
                y: imageBox.Y + ((imageBox.Height - h) / 2f),
                width: w,
                height: h
            );
            paint.AddRect(bounds: imageBox, color: _theme.PanelSunken, radius: Radii.Sm);
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
            paint.AddRect(bounds: imageBox, color: _theme.PanelSunken, radius: Radii.Sm);
            paint.AddText(
                text: "No preview",
                baselineX: imageBox.X + 12f,
                baselineY: imageBox.Y + (imageBox.Height / 2f),
                color: _theme.TextMuted,
                fontSize: fs
            );
        }

        float textY = imageBox.Bottom + 18f;
        paint.AddClipStart(Bounds);
        paint.AddText(
            text: _entry.Name,
            baselineX: Bounds.X + pad,
            baselineY: textY,
            color: _theme.OnSurface,
            fontSize: fs
        );
        textY += fs + 8f;
        if (_texW > 0)
        {
            paint.AddText(
                text: $"{_texW} × {_texH}",
                baselineX: Bounds.X + pad,
                baselineY: textY,
                color: _theme.TextSecondary,
                fontSize: fs
            );
            textY += fs + 6f;
        }

        paint.AddText(
            text: FileBrowserList.FormatSize(_entry.Size),
            baselineX: Bounds.X + pad,
            baselineY: textY,
            color: _theme.TextSecondary,
            fontSize: fs
        );
        textY += fs + 6f;
        paint.AddText(
            text: FileBrowserList.FormatDate(modified: _entry.Modified, now: DateTime.Now),
            baselineX: Bounds.X + pad,
            baselineY: textY,
            color: _theme.TextMuted,
            fontSize: fs
        );
        paint.AddClipEnd();
    }

    public override int DebugStateHash() =>
        HashCode.Combine(value1: _entry.FullPath, value2: _handle);
}
