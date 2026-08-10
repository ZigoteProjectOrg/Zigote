using System.Reflection;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Net;
using Zigote.UI.Semantics;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     Displays an image loaded from a file path, memory, or embedded asset. The image is decoded
///     natively by the engine, uploaded to the GPU on first paint, and kept there until this widget
///     is disposed.
/// </summary>
/// <remarks>
///     <para>
///         <b>Dispose it.</b> A texture is owned by the handle that created it — nothing else frees
///         one. A screen that shows a hundred photos and never disposes them holds a hundred
///         textures forever; at 2000×3000 that is 2.4 GB. <see cref="Dispose" /> is idempotent and
///         safe to call from anywhere, including during teardown.
///     </para>
///     <para>
///         <b>Bound the size.</b> A texture costs <c>width × height × 4</c> bytes on the GPU
///         regardless of how small it is drawn, so pass <c>maxDim</c> whenever the source image is
///         larger than its slot: a 4000 px photo in a 200 px thumbnail is 64 MB as loaded and
///         0.16 MB at <c>maxDim: 200</c>. The engine box-downsamples during decode, so the large
///         buffer never reaches the GPU at all.
///     </para>
///     <para>
///         <b>Load big images off the frame loop.</b> Decoding a multi-megapixel JPEG takes tens of
///         milliseconds — several dropped frames if done inline. <see cref="LoadAsync" /> fetches
///         and decodes on a worker thread and swaps the result in on the UI thread when it lands.
///         For a feed or grid tile, <c>Zigote.UI.Material.AsyncImage</c> builds on the same
///         mechanics and adds cover/contain fit, a placeholder fill and a fade-in.
///     </para>
/// </remarks>
public sealed class Image : Widget, IDisposable
{
    // Decoding is blocking native work. Without a gate, ten thousand LoadAsync calls would each
    // park a thread-pool thread inside the decoder; the pool only grows a thread or so per second
    // under starvation, so the queue would drain over minutes and every other pool user — the
    // fetches feeding it included — would stall behind it. One slot per core keeps the decoders
    // saturated and the pool free, and the wait is an await, so a queued load costs no thread.
    private static readonly SemaphoreSlim DecodeGate =
        new(Environment.ProcessorCount, Environment.ProcessorCount);

    private CancellationTokenSource? _loadCts;
    private bool _disposed;
    private Size _size;
    private uint _texHeight;
    private ulong _textureHandle;
    private uint _texWidth;

    /// <summary>An empty image — nothing is painted until a texture arrives (see <see cref="LoadAsync" />).</summary>
    public Image()
    {
    }

    /// <summary>
    ///     Load and decode <paramref name="path" /> synchronously. Blocks the calling thread for the
    ///     decode; prefer <see cref="LoadAsync" /> for anything larger than an icon.
    ///     <paramref name="maxDim" /> (0 = unbounded) caps the longest edge of the decoded texture.
    /// </summary>
    public Image(string path, uint maxDim = 0)
    {
        if (maxDim == 0)
        {
            _textureHandle = ZigoteEngine.LoadTexture(
                Path.GetFullPath(path),
                out _texWidth,
                out _texHeight
            );
            return;
        }

        // No scaled file entry point on the engine: read here and go through the memory path,
        // which does the downsample during decode.
        _textureHandle = ZigoteEngine.LoadTextureFromMemoryScaled(
            File.ReadAllBytes(Path.GetFullPath(path)),
            maxDim,
            out _texWidth,
            out _texHeight
        );
    }

    private Image(ulong handle, uint width, uint height)
    {
        _textureHandle = handle;
        _texWidth = width;
        _texHeight = height;
    }

    /// <summary>
    ///     Accessible description of the image. <c>null</c> (the default) marks the image as decorative,
    ///     omitting it from the accessibility tree; set it to announce the image to assistive tech.
    /// </summary>
    public string? AltText { get; set; }

    /// <summary>
    ///     Size to occupy while no texture is loaded. Without it an empty image measures to zero and
    ///     the surrounding layout jumps when the load completes — set it to the slot's expected size
    ///     (or the source's aspect at the target width) to keep scroll position stable.
    /// </summary>
    public Size? PlaceholderSize { get; set; }

    /// <summary>True once a texture is loaded and paintable.</summary>
    public bool HasTexture => _textureHandle != 0;

    /// <summary>
    ///     Runs on the UI thread once a <see cref="LoadAsync" /> texture is in place — the hook a
    ///     placeholder or a fade-in hangs off. Set it before starting the load.
    /// </summary>
    public Action? OnLoaded { get; set; }

    /// <summary>
    ///     Runs on the UI thread when a <see cref="LoadAsync" /> could not produce a texture: the
    ///     fetch threw (no network, 404, timeout) or the bytes were not a decodable image. A load
    ///     that was merely superseded or disposed is not a failure and does not fire this.
    /// </summary>
    public Action<Exception>? OnFailed { get; set; }

    /// <summary>Pixel dimensions of the loaded texture (0×0 when empty). Post-downsample.</summary>
    public (uint Width, uint Height) TextureSize => (_texWidth, _texHeight);

    /// <summary>Bytes this image occupies on the GPU — <c>w × h × 4</c>. 0 when empty.</summary>
    public long TextureBytes => (long)_texWidth * _texHeight * 4;

    public override bool ExcludeSemantics => AltText is null;

    /// <summary>
    ///     Release the texture (CPU copy and GPU memory) and cancel any in-flight load. Idempotent;
    ///     the widget stays usable and simply paints nothing.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        ClearTexture();
    }

    public override void DescribeSemantics(SemanticsConfiguration config)
    {
        config.Role = SemanticsRole.Image;
        config.Label = AltText;
    }

    public static Image FromFile(string path, uint maxDim = 0)
    {
        return new Image(path, maxDim);
    }

    /// <summary>
    ///     An image from the app's deployed <c>Assets/</c> tree —
    ///     <c>Image.FromAsset("Sprites/hero.png")</c> — resolved by <see cref="AppAssets" /> so the
    ///     same path works in a dev build, a published bundle and a macOS .app.
    ///     <para>
    ///         Prefer this to composing the path yourself: a literal asset path is one the publish-time
    ///         asset shake can see, and an asset nothing names by literal is an asset it will drop.
    ///     </para>
    ///     <para>
    ///         Distinct from <see cref="FromResource" />, which loads a file compiled into an assembly
    ///         manifest. Kept as a separate name rather than an overload of it: C# prefers the
    ///         candidate with fewer omitted optional parameters, so an overload would have quietly
    ///         become the better match for a one-argument embedded-resource call and turned it into a
    ///         missing-file error.
    ///     </para>
    /// </summary>
    public static Image FromAsset(string relativePath, uint maxDim = 0)
    {
        return new Image(AppAssets.Path(relativePath), maxDim);
    }

    public static Image FromBytes(ReadOnlySpan<byte> data, uint maxDim = 0)
    {
        uint w, h;
        var handle = maxDim == 0
            ? ZigoteEngine.LoadTextureFromMemory(data, out w, out h)
            : ZigoteEngine.LoadTextureFromMemoryScaled(
                data,
                maxDim,
                out w,
                out h
            );
        return new Image(handle, w, h);
    }

    /// <summary>
    ///     An image from an <b>embedded resource</b> — a file compiled into an assembly's manifest,
    ///     named by its resource name (<c>MyApp.Images.logo.png</c>), not by any path on disk.
    ///     <paramref name="assemblyType" /> selects the assembly to read from; the caller's own is used
    ///     when it is null.
    ///     <para>
    ///         For a file the app ships in its deployed <c>Assets/</c> tree, use
    ///         <see cref="FromAsset" /> instead — different mechanism, different lookup.
    ///     </para>
    /// </summary>
    public static Image FromResource(string resourceName, Type? assemblyType = null, uint maxDim = 0)
    {
        var asm = assemblyType?.Assembly ?? Assembly.GetCallingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            var allNames = string.Join(", ", asm.GetManifestResourceNames());
            throw new FileNotFoundException(
                $"Resource '{resourceName}' not found. Available: {allNames}"
            );
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return FromBytes(ms.ToArray(), maxDim);
    }

    /// <summary>
    ///     Fetch and decode off the UI thread, then swap the result in on the next frame. Starting a
    ///     second load cancels the first, so an image recycled through a scrolling list never shows a
    ///     stale row's picture. The engine's loaders are thread-safe; the GPU upload still happens on
    ///     the render thread, at first paint.
    ///     <para>
    ///         Outcomes arrive through <see cref="OnLoaded" /> and <see cref="OnFailed" />, both on
    ///         the UI thread — set them before calling. The returned task completes when the load
    ///         has been dealt with either way and never faults, so a fire-and-forget call is safe.
    ///     </para>
    /// </summary>
    /// <param name="fetch">
    ///     Produces the encoded bytes (file read, archive entry, HTTP body — see
    ///     <see cref="NetworkCache.FetchAsync" /> for a cached, coalesced, rate-gated HTTP one).
    /// </param>
    /// <param name="maxDim">Caps the longest edge of the decoded texture; 0 = unbounded.</param>
    public Task LoadAsync(Func<CancellationToken, Task<byte[]>> fetch, uint maxDim = 0)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        if (_disposed) return Task.CompletedTask;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();
        var token = cts.Token;

        return Task.Run(
            async () =>
            {
                ulong handle;
                uint w, h;
                try
                {
                    var bytes = await fetch(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    await DecodeGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        // Re-checked after the queue: a tile that scrolled away while it waited is
                        // the common case in a fast-flung grid, and its decode is pure waste.
                        token.ThrowIfCancellationRequested();
                        handle = maxDim == 0
                            ? ZigoteEngine.LoadTextureFromMemory(bytes, out w, out h)
                            : ZigoteEngine.LoadTextureFromMemoryScaled(
                                bytes,
                                maxDim,
                                out w,
                                out h
                            );
                    }
                    finally
                    {
                        DecodeGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return; // superseded or disposed — the caller asked for this, not a failure
                }
                catch (Exception error)
                {
                    Fail(error, token);
                    return;
                }

                if (handle == 0)
                {
                    // The fetch succeeded but the engine could not decode the bytes — an HTML error
                    // page served with an image URL, a truncated file, a format not built in.
                    Fail(new InvalidDataException("The fetched bytes are not a decodable image."), token);
                    return;
                }

                // Superseded or disposed while decoding: the texture exists but nothing will ever
                // paint it, so drop it here rather than leaking a page-sized allocation.
                if (token.IsCancellationRequested || _disposed)
                {
                    ZigoteEngine.ReleaseTexture(handle);
                    return;
                }

                var app = App.Active;
                if (app is null)
                {
                    ZigoteEngine.ReleaseTexture(handle);
                    return;
                }

                app.Post(() =>
                    {
                        // Re-checked on the UI thread: cancellation can land between the check
                        // above and this callback running.
                        if (token.IsCancellationRequested || _disposed)
                        {
                            ZigoteEngine.ReleaseTexture(handle);
                            return;
                        }

                        SetTexture(handle, w, h);
                        OnLoaded?.Invoke();
                    }
                );
            },
            token
        );
    }

    /// <summary>Report a load failure on the UI thread, unless the load was cancelled meanwhile.</summary>
    private void Fail(Exception error, CancellationToken token)
    {
        if (token.IsCancellationRequested || _disposed || OnFailed is null) return;
        App.Active?.Post(() =>
            {
                if (token.IsCancellationRequested || _disposed) return;
                OnFailed?.Invoke(error);
            }
        );
    }

    /// <summary>
    ///     Adopt an already-loaded texture handle, releasing whatever this image held before. Takes
    ///     ownership: the handle is freed on the next <see cref="SetTexture" /> or
    ///     <see cref="Dispose" />. UI thread only.
    /// </summary>
    public void SetTexture(ulong textureHandle, uint width, uint height)
    {
        if (textureHandle == _textureHandle) return;
        if (_textureHandle != 0) ZigoteEngine.ReleaseTexture(_textureHandle);

        _textureHandle = textureHandle;
        _texWidth = width;
        _texHeight = height;
        MarkNeedsLayout();
    }

    /// <summary>
    ///     Stop painting the current texture <b>without</b> releasing it.
    ///     <para>
    ///         For callers that own the texture themselves and hand the same handle to several
    ///         images — a shared thumbnail cache, say. <see cref="ClearTexture" /> would free a
    ///         texture the other images are still drawing; this only forgets it here, leaving the
    ///         owner to release it exactly once.
    ///     </para>
    /// </summary>
    public void ForgetTexture()
    {
        if (_textureHandle == 0) return;
        _textureHandle = 0;
        _texWidth = _texHeight = 0;
        MarkNeedsLayout();
    }

    /// <summary>Release the current texture and paint nothing. UI thread only.</summary>
    public void ClearTexture()
    {
        if (_textureHandle == 0) return;
        ZigoteEngine.ReleaseTexture(_textureHandle);
        _textureHandle = 0;
        _texWidth = _texHeight = 0;
        MarkNeedsLayout();
    }

    public override Size Measure(Constraints c)
    {
        if (_textureHandle == 0 || _texWidth == 0 || _texHeight == 0)
        {
            _size = c.Constrain(PlaceholderSize ?? Size.Zero);
            return _size;
        }

        var aspect = (float)_texWidth / _texHeight;
        float w = _texWidth;
        float h = _texHeight;

        if (w > c.MaxWidth)
        {
            w = c.MaxWidth;
            h = w / aspect;
        }

        if (h > c.MaxHeight)
        {
            h = c.MaxHeight;
            w = h * aspect;
        }

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
        if (_textureHandle != 0)
            paint.AddImage(
                Bounds,
                (int)_texWidth,
                (int)_texHeight,
                null,
                _textureHandle
            );
    }
}