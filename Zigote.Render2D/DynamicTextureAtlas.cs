namespace Zigote.Render2D;

/// <summary>
///     A sprite's slot inside a <see cref="DynamicTextureAtlas" />. The atlas recomputes
///     <see cref="Frame" /> on repack/grow — holders keep the same object and stay valid.
/// </summary>
public sealed class AtlasSprite
{
    public SpriteFrame Frame { get; internal set; }
    public int PixelX { get; internal set; }
    public int PixelY { get; internal set; }
    public int PixelWidth { get; internal set; }
    public int PixelHeight { get; internal set; }
}

/// <summary>
///     Square RGBA atlas packed with shelf rows (max-height-in-row shelves, <c>padding</c> px
///     between entries). When an entry doesn't fit the atlas grows ×2 up to <c>maxSize</c> and
///     repacks every entry — source RGBA is retained per entry to make that possible (the
///     documented memory tradeoff). Packing is pure CPU state: pixel rects and frames are updated
///     on every (re)pack, before any <see cref="Commit" />; Commit only uploads to the GPU.
/// </summary>
public sealed class DynamicTextureAtlas : IDisposable
{
    private readonly ISpriteDevice _device;
    private readonly List<Entry> _entries = [];
    private readonly SpriteFilter _filter;
    private readonly int _maxSize;
    private readonly int _padding;
    private bool _dirty;

    // Running shelf cursor so the common TryAdd appends in O(1) without disturbing prior entries.
    private int _penX, _penY, _shelfH;
    private byte[] _pixels = [];

    public DynamicTextureAtlas(ISpriteDevice device, int initialSize = 512, int maxSize = 4096,
        int padding = 2, SpriteFilter filter = SpriteFilter.Linear)
    {
        _device = device;
        _maxSize = maxSize;
        _padding = padding;
        _filter = filter;
        Size = Math.Clamp(initialSize, 1, maxSize);
    }

    /// <summary>0 until the first <see cref="Commit" />.</summary>
    public uint TextureHandle { get; private set; }

    /// <summary>Current square size in pixels.</summary>
    public int Size { get; private set; }

    public void Dispose()
    {
        if (TextureHandle == 0) return;
        _device.DestroyTexture(TextureHandle);
        TextureHandle = 0;
    }

    /// <summary>Null if the entry can't fit even after growing to maxSize.</summary>
    public AtlasSprite? TryAdd(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > _maxSize || height > _maxSize) return null;
        if (rgba.Length < width * height * 4) return null;

        var entry = new Entry(rgba.ToArray(), width, height);

        // Fast path: append at the running cursor. This continues exactly the layout a full repack
        // would produce, so a same-size repack could never fit what the cursor can't — grow instead.
        if (TryPlace(
                entry,
                Size,
                ref _penX,
                ref _penY,
                ref _shelfH
            ))
        {
            Apply(entry, Size);
            _entries.Add(entry);
            _dirty = true;
            return entry.Sprite;
        }

        _entries.Add(entry);
        var size = Size;
        while (size < _maxSize)
        {
            size = Math.Min(size * 2, _maxSize);
            if (!TryRepack(size)) continue;
            Size = size;
            _dirty = true;
            return entry.Sprite;
        }

        // Doesn't fit even at maxSize: back the entry out and repack at the old size — the packer
        // is deterministic, so this restores every prior rect (and the cursor) exactly.
        _entries.RemoveAt(_entries.Count - 1);
        TryRepack(Size);
        return null;
    }

    /// <summary>
    ///     No-op unless dirty: rebuild the CPU atlas pixels, destroy the old GPU texture, create the
    ///     new one, and refresh every <see cref="AtlasSprite.Frame" />.
    /// </summary>
    public void Commit()
    {
        if (!_dirty) return;

        var size = Size;
        var stride = size * 4;
        var needed = size * stride;
        if (_pixels.Length < needed) _pixels = new byte[needed];
        else Array.Clear(_pixels, 0, needed);

        foreach (var e in _entries)
        {
            var rowBytes = e.W * 4;
            for (var row = 0; row < e.H; row++)
                Buffer.BlockCopy(
                    e.Rgba,
                    row * rowBytes,
                    _pixels,
                    (e.Y + row) * stride + e.X * 4,
                    rowBytes
                );
        }

        if (TextureHandle != 0) _device.DestroyTexture(TextureHandle);
        TextureHandle = _device.CreateTexture(
            new ReadOnlySpan<byte>(_pixels, 0, needed),
            size,
            size,
            _filter,
            true,
            SpriteWrap.Clamp
        );

        foreach (var e in _entries) Apply(e, size);
        _dirty = false;
    }

    private bool TryRepack(int size)
    {
        int penX = 0, penY = 0, shelfH = 0;
        for (var i = 0; i < _entries.Count; i++)
            if (!TryPlace(
                    _entries[i],
                    size,
                    ref penX,
                    ref penY,
                    ref shelfH
                ))
                return false;

        for (var i = 0; i < _entries.Count; i++) Apply(_entries[i], size);
        _penX = penX;
        _penY = penY;
        _shelfH = shelfH;
        return true;
    }

    private bool TryPlace(Entry e, int size, ref int penX, ref int penY, ref int shelfH)
    {
        if (e.W > size || e.H > size) return false;
        if (penX + e.W > size)
        {
            penY += shelfH + _padding;
            penX = 0;
            shelfH = 0;
        }

        if (penY + e.H > size) return false;

        e.X = penX;
        e.Y = penY;
        penX += e.W + _padding;
        if (e.H > shelfH) shelfH = e.H;
        return true;
    }

    private static void Apply(Entry e, int size)
    {
        var inv = 1f / size;
        e.Sprite.PixelX = e.X;
        e.Sprite.PixelY = e.Y;
        e.Sprite.Frame = new SpriteFrame(
            e.X * inv,
            e.Y * inv,
            (e.X + e.W) * inv,
            (e.Y + e.H) * inv,
            e.W,
            e.H
        );
    }

    private sealed class Entry
    {
        public readonly int H;
        public readonly byte[] Rgba;
        public readonly AtlasSprite Sprite;
        public readonly int W;
        public int X;
        public int Y;

        public Entry(byte[] rgba, int w, int h)
        {
            Rgba = rgba;
            W = w;
            H = h;
            Sprite = new AtlasSprite {
                PixelWidth = w,
                PixelHeight = h,
            };
        }
    }
}