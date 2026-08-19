using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.Core.State;

namespace Camera;

/// <summary>Which way a camera looks. Desktop webcams report <see cref="External" />.</summary>
public enum CameraFacing
{
    Front,
    Back,
    External,
}

/// <summary>One camera the platform knows about.</summary>
/// <param name="Id">Stable identifier to pass to <see cref="CameraController.StartAsync" />.</param>
/// <param name="Name">Human-readable, for a picker.</param>
public sealed record CameraDeviceInfo(string Id, string Name, CameraFacing Facing);

public enum CameraState
{
    /// <summary>No session. Also after <see cref="CameraController.Stop" />.</summary>
    Idle,

    /// <summary>Opening the device; no frame yet.</summary>
    Starting,

    /// <summary>Frames are arriving.</summary>
    Streaming,

    /// <summary>The device could not be opened or the stream died — see <see cref="CameraController.Error" />.</summary>
    Failed,
}

/// <summary>
///     A running platform capture, whichever API is underneath. It writes frames into the
///     <see cref="FrameMailbox" /> it was given and stops when disposed. Disposal must be safe
///     from the app thread at any moment, including before the first frame.
/// </summary>
internal interface ICameraSession : IDisposable;

/// <summary>
///     The single-frame handoff between a capture thread and the app thread: latest frame wins,
///     older undelivered frames are recycled, and buffers cycle through a free list so steady-state
///     capture allocates nothing. The camera flavor of <c>VideoPlayer</c>'s frame queue — a preview
///     never wants a backlog, only the newest picture.
/// </summary>
internal sealed class FrameMailbox
{
    private readonly object _gate = new();
    private readonly Stack<byte[]> _free = new();
    private byte[]? _latest;
    private int _width, _height;

    /// <summary>Writer side: a buffer of at least <paramref name="bytes" />, recycled when possible.</summary>
    public byte[] Rent(int bytes)
    {
        lock (_gate)
        {
            while (_free.TryPop(out byte[]? buffer))
                if (buffer.Length >= bytes)
                    return buffer;
        }

        return new byte[bytes];
    }

    /// <summary>Writer side: make this frame the latest. An unread previous frame goes back to the pool.</summary>
    public void Publish(byte[] buffer, int width, int height)
    {
        lock (_gate)
        {
            if (_latest is not null) _free.Push(_latest);
            _latest = buffer;
            _width = width;
            _height = height;
        }
    }

    /// <summary>Reader side: the newest unread frame, or false. Pair with <see cref="Return" />.</summary>
    public bool TryTake(out byte[] buffer, out int width, out int height)
    {
        lock (_gate)
        {
            buffer = _latest!;
            width = _width;
            height = _height;
            _latest = null;
            return buffer is not null;
        }
    }

    /// <summary>Reader side: done with a taken buffer.</summary>
    public void Return(byte[] buffer)
    {
        lock (_gate)
        {
            _free.Push(buffer);
        }
    }
}

/// <summary>
///     A camera session: open a device, stream its frames into a GPU texture that
///     <see cref="CameraView" /> paints, snapshot the current frame as a JPEG.
///     <para>
///         Shaped like <c>VideoPlayer</c>: state is fine-grained signals, nothing runs on its own —
///         the host calls <see cref="Tick" /> once per frame (which <see cref="CameraView" /> does
///         while on screen), and that is when the newest captured frame is uploaded. Capture itself
///         runs on platform threads and hands over only the latest frame, so a slow UI frame drops
///         camera frames instead of queueing them.
///     </para>
///     <para>
///         On mobile the controller releases the camera when the app is backgrounded and reopens it
///         on resume — provided the consumer registered <see cref="CameraPlugin" />, which is what
///         wires the lifecycle through.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var camera = new CameraController();
/// if (await CameraPlugin.RequestPermissionAsync())
///     await camera.StartAsync();          // default device, 720p preview
/// // in the widget tree: new CameraView(camera)
/// byte[] jpeg = await camera.TakePhotoAsync();
/// </code>
/// </example>
public sealed class CameraController : IDisposable
{
    private readonly Signal<string?> _error = new(null);
    private readonly Signal<CameraState> _state = new(CameraState.Idle);
    private readonly FrameMailbox _frames = new();
    private readonly IDisposable _lifecycle;

    private ICameraSession? _session;
    private PhotoRequest? _photo;
    private byte[]? _lastFrame;
    private (int Width, int Height) _lastFrameSize;
    private string? _deviceId;
    private int _maxHeight;
    private bool _minimalProcessing;
    private bool _resumeOnForeground;
    private bool _disposed;

    public CameraController()
    {
        _lifecycle = CameraPlugin.OnLifecycle(OnLifecycleChanged);
    }

    /// <summary>Where the session is. A whole camera UI can bind to just this.</summary>
    public IReadableSignal<CameraState> State => _state;

    /// <summary>Why the last start or stream failed, or null. Cleared by the next start.</summary>
    public IReadableSignal<string?> Error => _error;

    /// <summary>
    ///     The current preview frame's GPU texture, or 0 before the first frame. Owned by the
    ///     controller — paint it, never release it. <see cref="CameraView" /> is the ordinary way
    ///     to show it.
    /// </summary>
    public ulong TextureHandle { get; private set; }

    /// <summary>Pixel size of the preview frames, 0×0 before the first one.</summary>
    public (uint Width, uint Height) FrameSize { get; private set; }

    /// <summary>
    ///     Frames presented so far. The repaint signal for views: the texture HANDLE is stable
    ///     across a session (frames are texel overwrites), so "did a new frame arrive" must be
    ///     asked of this counter, never of <see cref="TextureHandle" />.
    /// </summary>
    public long FramesPresented { get; private set; }

    /// <summary>The device id of the running (or starting) session, null when idle.</summary>
    public string? DeviceId => _session is null ? null : _deviceId;

    /// <summary>
    ///     Open a camera and start streaming. Replaces any running session, so switching cameras is
    ///     just calling this again with another id.
    /// </summary>
    /// <param name="deviceId">
    ///     A <see cref="CameraDeviceInfo.Id" /> from <see cref="CameraPlugin.GetDevicesAsync" />,
    ///     or null for the platform's default camera.
    /// </param>
    /// <param name="maxHeight">
    ///     Cap the preview height, preserving aspect (0 = the device's native size). Every frame is
    ///     copied and uploaded whole, so 720 is the right answer for a preview pane; pass 0 when
    ///     the pixels themselves are the point.
    /// </param>
    /// <param name="minimalProcessing">
    ///     Ask the platform for the most neutral frames it can deliver — the DSLR-flat option:
    ///     on Android, noise reduction, edge enhancement and video stabilization are switched
    ///     off; on desktop, v4l2/avfoundation/dshow capture is already the driver's untouched
    ///     output, so this is a no-op. Pair with a <see cref="CameraLut" /> when the look should
    ///     come from your grade, not the vendor's.
    /// </param>
    /// <remarks>
    ///     Returns once the device is opening; <see cref="State" /> moves to
    ///     <see cref="CameraState.Streaming" /> on the first frame, or
    ///     <see cref="CameraState.Failed" /> with <see cref="Error" /> set. Permission is not
    ///     requested here — call <see cref="CameraPlugin.RequestPermissionAsync" /> first, because
    ///     a permission prompt belongs to a user gesture, not to a widget appearing. The frames
    ///     are raw off the device — grading (LUTs) lives in the render pipeline, see
    ///     <see cref="CameraLut" />.
    /// </remarks>
    public Task StartAsync(string? deviceId = null, int maxHeight = 720, bool minimalProcessing = false)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);

        StopSession();
        _deviceId = deviceId;
        _maxHeight = Math.Max(val1: 0, val2: maxHeight);
        _minimalProcessing = minimalProcessing;
        _error.Value = null;
        _state.Value = CameraState.Starting;

        try
        {
            _session = CameraDriver.Open(
                deviceId: deviceId,
                maxHeight: _maxHeight,
                minimalProcessing: minimalProcessing,
                frames: _frames,
                onError: Fail
            );
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>Release the camera. Keeps the last frame's texture on screen until the next start.</summary>
    public void Stop()
    {
        if (_disposed) return;
        StopSession();
        CancelPhoto();
        _resumeOnForeground = false;
        if (_state.Value is not CameraState.Failed) _state.Value = CameraState.Idle;
    }

    /// <summary>
    ///     One frame of the host's loop: upload the newest captured frame, if any. Must run on the
    ///     thread that owns the GPU. <see cref="CameraView" /> calls it while mounted; a headless
    ///     host calls it from its own loop. Cheap when nothing new arrived.
    /// </summary>
    public void Tick()
    {
        if (_disposed) return;

        // A photo pass emitted last frame has been rendered by now (the render-texture prepass
        // runs inside the frame that carried the commands) — close the pipeline: read back,
        // encode off-thread, release the target.
        if (_photo is { Emitted: true } photo)
        {
            _photo = null;
            CompletePhoto(photo);
        }

        if (!_frames.TryTake(buffer: out byte[] frame, width: out int w, height: out int h))
            return;

        Upload(rgba: frame, width: w, height: h);
        FramesPresented++;

        // Keep the presented frame for TakePhotoAsync; recycle the one it replaces.
        if (_lastFrame is not null) _frames.Return(_lastFrame);
        _lastFrame = frame;
        _lastFrameSize = (w, h);

        if (_state.Value == CameraState.Starting) _state.Value = CameraState.Streaming;
    }

    /// <summary>
    ///     Capture the current frame as a JPEG. Without a LUT the raw frame bytes are encoded
    ///     as-is. With one, the photo goes through the GPU pipeline: the frame texture and the
    ///     LUT shader pass render into an offscreen target, the processed pixels are read back,
    ///     then encoded — the same grade the preview shows, computed the same way.
    /// </summary>
    /// <remarks>
    ///     The GPU path needs a painter driving it: a mounted <see cref="CameraView" /> does this
    ///     automatically, a custom painter calls <see cref="PaintPhotoPass" /> from its own Paint.
    ///     Call from the app thread; only the JPEG encode runs off it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No frame yet, or a capture is already in flight.</exception>
    // ponytail: the photo is the preview-stream frame (≤ maxHeight tall; pass maxHeight: 0 for
    // the device's native size). A separate sensor-resolution still path (camera2 STILL_CAPTURE /
    // AVCapturePhotoOutput) is the upgrade if full-size stills start to matter.
    public Task<byte[]> TakePhotoAsync(int quality = 90, CameraLut? lut = null, float lutStrength = 1f)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (_lastFrame is null)
            throw new InvalidOperationException("No camera frame to capture yet.");
        quality = Math.Clamp(value: quality, min: 1, max: 100);
        (int w, int h) = _lastFrameSize;

        if (lut is null || lutStrength <= 0f)
        {
            // Copied now, on the app thread: the encoder runs later, and by then the buffer may
            // have been recycled into the capture loop.
            byte[] rgba = _lastFrame.AsSpan(start: 0, length: w * h * 4).ToArray();
            return CameraDriver.EncodeJpegAsync(rgba: rgba, width: w, height: h, quality: quality);
        }

        if (_photo is not null)
            throw new InvalidOperationException("A photo capture is already in flight.");
        var engine = ZigoteEngine.Instance
                     ?? throw new InvalidOperationException("GPU photo capture needs a running engine.");

        ulong rt = engine.CreateRenderTexture(width: (uint)w, height: (uint)h);
        if (rt == 0) throw new InvalidOperationException("Could not create the photo render texture.");
        _photo = new PhotoRequest {
            Tcs = new TaskCompletionSource<byte[]>(),
            Lut = lut,
            Strength = lutStrength,
            Quality = quality,
            Width = w,
            Height = h,
            Rt = rt,
        };
        return _photo.Tcs.Task;
    }

    /// <summary>
    ///     Emit the pending photo's GPU pass into <paramref name="paint" /> — one render-texture
    ///     block: the frame texture, then the LUT grade over it. <see cref="CameraView" /> calls
    ///     this every paint; an app compositing the preview itself calls it from its own Paint.
    ///     No-op when nothing is pending.
    /// </summary>
    public void PaintPhotoPass(PaintList paint)
    {
        var photo = _photo;
        if (photo is null || photo.Emitted || TextureHandle == 0) return;

        // The RT renders at the UI scale, so logical bounds of size/scale fill it pixel-exact
        // (identity resample at integer scales, sub-texel at fractional ones).
        float scale = ZigoteEngine.Instance?.Scale ?? 1f;
        var full = new Rect(x: 0, y: 0, width: photo.Width / scale, height: photo.Height / scale);
        paint.PushRenderTexture(photo.Rt);
        paint.AddImage(
            bounds: full,
            pixelWidth: photo.Width,
            pixelHeight: photo.Height,
            pixels: null,
            cacheKey: TextureHandle
        );
        LutEffect.Paint(paint: paint, bounds: full, lut: photo.Lut, strength: photo.Strength);
        paint.PopRenderTexture();
        photo.Emitted = true;
    }

    /// <summary>A GPU photo pass is waiting for a painter — <see cref="CameraView" /> repaints on this.</summary>
    public bool PhotoPassPending => _photo is { Emitted: false };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifecycle.Dispose();
        StopSession();
        CancelPhoto();
        if (_lastFrame is not null) _frames.Return(_lastFrame);
        _lastFrame = null;
        if (TextureHandle != 0 && ZigoteEngine.Instance is not null)
            ZigoteEngine.ReleaseTexture(TextureHandle);
        TextureHandle = 0;
        FrameSize = (0, 0);
    }

    /// <summary>
    ///     Backgrounded: give the camera back to the OS — on mobile it is taken anyway, and holding
    ///     it is what gets an app flagged. Resumed: reopen if we were the ones streaming.
    /// </summary>
    private void OnLifecycleChanged(bool paused)
    {
        if (_disposed) return;
        if (paused)
        {
            if (_session is null) return;
            _resumeOnForeground = true;
            StopSession();
        }
        else if (_resumeOnForeground)
        {
            _resumeOnForeground = false;
            _ = StartAsync(deviceId: _deviceId, maxHeight: _maxHeight, minimalProcessing: _minimalProcessing);
        }
    }

    private void StopSession()
    {
        // Exchange, because Fail arrives from capture threads while the app thread may be
        // stopping: whoever wins disposes, the other sees null.
        var session = Interlocked.Exchange(location1: ref _session, value: null);
        session?.Dispose();
        // Drain a frame published between the capture stop and now, so a stale picture is not
        // presented as the first frame of the next session.
        if (_frames.TryTake(buffer: out byte[] frame, width: out _, height: out _))
            _frames.Return(frame);
    }

    /// <summary>Same steady-state texture strategy as <c>VideoPlayer</c>: overwrite, never re-create.</summary>
    private void Upload(byte[] rgba, int width, int height)
    {
        if (ZigoteEngine.Instance is null || width <= 0 || height <= 0) return;

        var pixels = rgba.AsSpan(start: 0, length: width * height * 4);

        if (TextureHandle != 0
            && FrameSize == ((uint)width, (uint)height)
            && ZigoteEngine.UpdateTextureRgba(
                textureHandle: TextureHandle,
                rgba: pixels,
                width: (uint)width,
                height: (uint)height
            ))
            return;

        ulong handle = ZigoteEngine.LoadTextureFromRgba(
            rgba: pixels,
            width: (uint)width,
            height: (uint)height
        );
        if (handle == 0) return;

        if (TextureHandle != 0) ZigoteEngine.ReleaseTexture(TextureHandle);
        TextureHandle = handle;
        FrameSize = ((uint)width, (uint)height);
    }

    /// <summary>Capture threads report errors here; signals are thread-safe, so no marshalling.</summary>
    private void Fail(string message)
    {
        StopSession();
        _error.Value = message;
        _state.Value = CameraState.Failed;
    }

    private static void CompletePhoto(PhotoRequest photo)
    {
        var engine = ZigoteEngine.Instance;
        byte[] rgba = new byte[photo.Width * photo.Height * 4];
        bool ok = engine is not null
                  && engine.ReadRenderTexturePixels(rtHandle: photo.Rt, rgba: rgba);
        engine?.DestroyRenderTexture(photo.Rt);
        if (!ok)
        {
            photo.Tcs.TrySetException(new InvalidOperationException("Photo readback failed."));
            return;
        }

        EncodePhoto(photo: photo, rgba: rgba);
    }

    private static async void EncodePhoto(PhotoRequest photo, byte[] rgba)
    {
        try
        {
            photo.Tcs.TrySetResult(await CameraDriver.EncodeJpegAsync(
                    rgba: rgba,
                    width: photo.Width,
                    height: photo.Height,
                    quality: photo.Quality
                )
            );
        }
        catch (Exception ex)
        {
            photo.Tcs.TrySetException(ex);
        }
    }

    private void CancelPhoto()
    {
        var photo = _photo;
        _photo = null;
        if (photo is null) return;
        if (ZigoteEngine.Instance is not null) ZigoteEngine.Instance.DestroyRenderTexture(photo.Rt);
        photo.Tcs.TrySetCanceled();
    }

    private sealed class PhotoRequest
    {
        public required TaskCompletionSource<byte[]> Tcs { get; init; }
        public required CameraLut Lut { get; init; }
        public required float Strength { get; init; }
        public required int Quality { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required ulong Rt { get; init; }
        public bool Emitted { get; set; }
    }
}
