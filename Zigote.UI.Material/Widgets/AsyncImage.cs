using Zigote.Core.Animation;
using Zigote.Core.Engine;
using Zigote.UI.Host;

namespace Zigote.UI.Material;

/// <summary>How an <see cref="AsyncImage" /> maps its image into the cell.</summary>
public enum ImageFit
{
    /// <summary>Fill the cell, cropping overflow (CSS <c>object-fit: cover</c>).</summary>
    Cover,

    /// <summary>Fit the whole image inside the cell, letterboxed (CSS <c>object-fit: contain</c>).</summary>
    Contain,
}

/// <summary>
///     An image that loads its bytes asynchronously (from a cache, disk, or the network), decodes
///     them off the UI thread, and fades in over a placeholder once the texture is ready. Fills its
///     cell and cover-crops the image (like CSS <c>object-fit: cover</c>). Pair it with
///     <c>AspectRatio</c> + <c>ResponsiveGrid</c> for a masonry of remote images.
/// </summary>
/// <remarks>
///     Both the fetch and the native decode run on a worker thread (the engine's texture loaders are
///     thread-safe; the GPU upload happens in the renderer at first paint). A per-frame ticker only
///     adopts the finished handle and drives the fade — decoding a multi-megapixel image on the frame
///     thread costs several dropped frames per tile.
///     <para>
///         The texture is released on <see cref="Detach" />, so a tile leaving the tree gives its GPU
///         memory back and reloads if it returns. Set <see cref="MaxDecodeSize" /> on anything that
///         renders smaller than its source: a texture costs <c>w × h × 4</c> bytes whatever size it
///         is drawn at.
///     </para>
/// </remarks>
public sealed class AsyncImage : RenderWidget, ITickerProvider
{
    private readonly Func<CancellationToken, Task<byte[]?>> _loader;

    private CancellationTokenSource? _cts;
    private bool _decoded;
    private float _fade;
    private bool _failed;
    private ulong _handle;
    private Size _size;
    private Task<(ulong Handle, uint Width, uint Height)>? _task;
    private uint _texH;
    private uint _texW;
    private Ticker? _ticker;

    public AsyncImage(Func<CancellationToken, Task<byte[]?>> loader)
    {
        _loader = loader;
    }

    /// <summary>Fill shown before/behind the image (e.g. the image's dominant colour).</summary>
    public Color Placeholder { get; init; } = new(
        1f,
        1f,
        1f,
        0.06f
    );

    public float FadeDuration { get; init; } = 0.45f;

    /// <summary>Whether to cover-crop (default) or contain (letterbox) the image within the cell.</summary>
    public ImageFit Fit { get; init; } = ImageFit.Cover;

    /// <summary>Corner radius of the placeholder fill (the decoded image itself is not corner-clipped).</summary>
    public float Radius { get; init; }

    /// <summary>
    ///     Invoked once, on the UI thread, with the decoded pixel dimensions (for aspect-driven
    ///     layout).
    /// </summary>
    public Action<int, int>? OnDecoded { get; init; }

    /// <summary>
    ///     Invoked once, on the UI thread, when the image will never appear — the loader returned
    ///     nothing, threw, or the bytes did not decode. The counterpart to <see cref="OnDecoded" />:
    ///     without it a host cannot tell "still loading" from "gave up", so a placeholder that never
    ///     resolves is indistinguishable from a broken screen.
    /// </summary>
    public Action? OnFailed { get; init; }

    /// <summary>
    ///     Downsample the decoded image so neither axis exceeds this (0 = full resolution). Essential for
    ///     image-heavy feeds: source images are often far larger than they render, and full-res GPU
    ///     textures
    ///     exhaust memory. A grid tile wants ~600–800; a detail view more.
    /// </summary>
    public int MaxDecodeSize { get; init; }

    public Ticker CreateTicker(Action<float> onTick)
    {
        _ticker?.Dispose();
        _ticker = new Ticker(onTick);
        return _ticker;
    }

    public override void Attach(App owner, Widget? parent)
    {
        base.Attach(owner, parent);
        _ticker ??= CreateTicker(OnTick);
        _ticker.Start();
        if (!_decoded)
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _failed = false;
            var token = _cts.Token;
            // Task.Run so the loader's synchronous prologue (opening a file, hashing a cache key)
            // does not run on the frame thread either.
            _task = Task.Run(() => LoadAndDecodeAsync(token), token);
        }
    }

    public override void Detach()
    {
        base.Detach();
        _cts?.Cancel();
        _ticker?.Dispose();
        _ticker = null;

        // Give the GPU memory back — but a frame LATER, not here. Rebuild churn (an AdaptiveBuilder
        // breakpoint crossing, a Watch reconcile) detaches and reattaches a kept tile within the
        // same layout pass; releasing immediately forced a full reload and a placeholder flash on
        // every one. If the tile is still off the tree by the next frame, the release proceeds —
        // a tile scrolled out of a long feed still gives its texture back.
        if (_handle == 0) return;
        if (App.Active is { } app) app.Post(ReleaseIfStillDetached);
        else ReleaseNow();
    }

    private void ReleaseIfStillDetached()
    {
        if (Owner is null) ReleaseNow();
    }

    private void ReleaseNow()
    {
        if (_handle != 0)
        {
            ZigoteEngine.ReleaseTexture(_handle);
            _handle = 0;
            _texW = _texH = 0;
        }

        // Clearing _decoded is what lets Attach reload it if the tile comes back.
        _decoded = false;
        _fade = 0f;
    }

    /// <summary>Fetch then decode, both off the UI thread. Returns a zero handle if cancelled.</summary>
    private async Task<(ulong, uint, uint)> LoadAndDecodeAsync(CancellationToken token)
    {
        var bytes = await _loader(token).ConfigureAwait(false);
        if (bytes is not { Length: > 0 } || token.IsCancellationRequested) return default;

        uint w, h;
        var handle = MaxDecodeSize > 0
            ? ZigoteEngine.LoadTextureFromMemoryScaled(
                bytes,
                (uint)MaxDecodeSize,
                out w,
                out h
            )
            : ZigoteEngine.LoadTextureFromMemory(bytes, out w, out h);

        // Detached while decoding: nothing will ever paint this, so it must not outlive the tile.
        if (token.IsCancellationRequested && handle != 0)
        {
            ZigoteEngine.ReleaseTexture(handle);
            return default;
        }

        return (handle, w, h);
    }

    public override Size Measure(Constraints c)
    {
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 0f;
        var h = float.IsFinite(c.MaxHeight) ? c.MaxHeight : 0f;
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
        // Skip off-screen tiles entirely: this keeps the renderer's image cache to the visible working set
        // (an unbounded feed would otherwise upload a texture per off-screen tile every frame).
        if (!paint.IsVisible(Bounds)) return;

        if (Placeholder.A > 0f && (!_decoded || _fade < 1f))
            paint.AddRect(Bounds, Placeholder, Radius);

        if (!_decoded || _handle == 0 || _texW == 0 || _texH == 0) return;

        var cellAspect = Bounds.Height > 0f ? Bounds.Width / Bounds.Height : 1f;
        var imgAspect = (float)_texW / _texH;
        var tint = new Color(
            1f,
            1f,
            1f,
            Math.Clamp(_fade, 0f, 1f)
        );

        if (Fit == ImageFit.Contain)
        {
            // Letterbox: shrink the destination rect to fit the whole image, full UVs.
            float w = Bounds.Width, h = Bounds.Height;
            if (imgAspect > cellAspect) h = w / imgAspect;
            else w = h * imgAspect;
            var dest = new Rect(
                Bounds.X + (Bounds.Width - w) / 2f,
                Bounds.Y + (Bounds.Height - h) / 2f,
                w,
                h
            );
            paint.AddImage(
                dest,
                (int)_texW,
                (int)_texH,
                null,
                _handle,
                0f,
                0f,
                1f,
                1f,
                tint
            );
            return;
        }

        float u0 = 0f, v0 = 0f, u1 = 1f, v1 = 1f;
        if (imgAspect > cellAspect)
        {
            var vis = cellAspect / imgAspect; // crop left/right
            u0 = (1f - vis) / 2f;
            u1 = 1f - u0;
        }
        else
        {
            var vis = imgAspect / cellAspect; // crop top/bottom
            v0 = (1f - vis) / 2f;
            v1 = 1f - v0;
        }

        paint.AddImage(
            Bounds,
            (int)_texW,
            (int)_texH,
            null,
            _handle,
            u0,
            v0,
            u1,
            v1,
            tint
        );
    }

    private void OnTick(float dt)
    {
        if (_decoded)
        {
            if (_fade >= 1f)
            {
                _ticker?.Stop(); // settled — stop pumping frames for this tile
                return;
            }

            _fade = MathF.Min(1f, _fade + dt / MathF.Max(0.01f, FadeDuration));
            MarkNeedsPaint();
            return;
        }

        if (_failed)
        {
            _ticker?.Stop();
            return;
        }

        if (_task is null || !_task.IsCompleted) return;

        // Adopting the finished handle is all that happens on the UI thread now — the fetch and the
        // decode already ran on the worker.
        if (_task.IsCompletedSuccessfully)
        {
            var (handle, w, h) = _task.Result;
            if (handle != 0 && w > 0 && h > 0)
            {
                if (_handle != 0)
                    ZigoteEngine.ReleaseTexture(_handle); // re-decode replacing an old texture
                _handle = handle;
                _texW = w;
                _texH = h;
                _decoded = true;
                _fade = 0f;
                OnDecoded?.Invoke((int)w, (int)h);
                MarkNeedsPaint();
                return;
            }
        }

        _failed = true;
        OnFailed?.Invoke();
        MarkNeedsPaint();
    }
}