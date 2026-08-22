using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.Core.State;

namespace Camera;

/// <summary>
///     A captured frame as raw pixels, tightly packed RGBA. What a caller wants when it intends
///     to encode the picture itself.
/// </summary>
public readonly record struct CapturedPixels(byte[] Rgba, int Width, int Height);

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

    /// <summary>
    ///     The system took the camera away — a call, an alarm, Split View, another app. Distinct
    ///     from <see cref="Failed" /> because it is expected, temporary, and resolves on its own:
    ///     a UI should freeze the last frame and say why, not raise an error the user must dismiss
    ///     while they are still trying to take a photograph.
    /// </summary>
    Interrupted,
}

/// <summary>
///     How hot the device is. The camera, the GPU and the display together are the hottest thing
///     a phone does, and a mid-range one throttles within minutes; an app that ignores this gets
///     a preview that quietly drops to 15 fps with no explanation.
/// </summary>
public enum ThermalState
{
    Nominal,
    Warm,
    Hot,
    Critical,
}

/// <summary>
///     How much the device can afford. Preview resolution and how many stills may be in flight
///     come off this — a 50 MP RAW is ~100 MB, and two in flight on a 3 GB phone is a kill.
/// </summary>
public enum DeviceTier
{
    /// <summary>Low-RAM device: 720p preview, one still at a time.</summary>
    Low,

    /// <summary>The common case: 1080p preview, two stills.</summary>
    Mid,

    /// <summary>Plenty of headroom.</summary>
    High,
}

/// <summary>
///     A running platform capture, whichever API is underneath. It writes frames into the
///     <see cref="FrameMailbox" /> it was given and stops when disposed. Disposal must be safe
///     from the app thread at any moment, including before the first frame.
///     <para>
///         The manual-control members default to "this platform offers none", so a driver that
///         cannot do manual capture — desktop v4l2/avfoundation/dshow — implements nothing and
///         still reports the truth to the UI above it.
///     </para>
/// </summary>
internal interface ICameraSession : IDisposable
{
    /// <summary>What this lens can do. Probed at open; constant for the session's lifetime.</summary>
    CameraCapabilities Capabilities => CameraCapabilities.None;

    /// <summary>
    ///     Capture a sensor-data still as a DNG. Only called when
    ///     <see cref="CameraCapabilities.Raw" /> is true, so a driver without a raw path does not
    ///     implement this at all.
    /// </summary>
    Task<RawPhoto> CaptureRawAsync() =>
        Task.FromException<RawPhoto>(new NotSupportedException("This camera cannot capture RAW."));

    /// <summary>
    ///     Apply every control at once. Called on the app thread whenever any control changes,
    ///     coalesced, and once when the session opens. Must not block: a driver that needs to
    ///     talk to a camera HAL does it on its own thread.
    /// </summary>
    void Apply(in ControlState controls) { }

    /// <summary>
    ///     What the sensor did for the most recent frame, or null before the first result. Read
    ///     from the app thread each tick; the driver writes it from the capture thread, so it is
    ///     published as a whole record rather than mutated in place.
    /// </summary>
    CaptureMetadata? Metadata => null;
}

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
    /// <summary>Frames between thermal polls. ~2s at 60fps; the state moves far slower than that.</summary>
    private const int ThermalPollFrames = 120;

    private readonly Signal<string?> _error = new(null);
    private readonly Signal<CameraState> _state = new(CameraState.Idle);
    private readonly Signal<CameraCapabilities> _capabilities = new(CameraCapabilities.None);
    private readonly Signal<CaptureMetadata?> _metadata = new(null);
    private readonly Signal<ThermalState> _thermal = new(ThermalState.Nominal);
    private readonly Signal<string?> _interruption = new(null);
    private readonly FrameMailbox _frames = new();
    private readonly IDisposable _lifecycle;
    private readonly IDisposable _controlWatch;

    private ICameraSession? _session;
    private PhotoRequest? _photo;
    private byte[]? _lastFrame;
    private (int Width, int Height) _lastFrameSize;
    private string? _deviceId;
    private int _maxHeight;
    private bool _minimalProcessing;
    private bool _resumeOnForeground;
    private bool _clamping;
    private int _thermalTick;
    private bool _disposed;

    public CameraController()
    {
        _lifecycle = CameraPlugin.OnLifecycle(OnLifecycleChanged);
        // One coalesced subscription over every control: a capture request is rebuilt whole
        // anyway, so fourteen separate handlers would be fourteen ways to rebuild it twice.
        _controlWatch = ReactiveExtensions.ObserveAny(PushControls, Controls.All);
    }

    /// <summary>Where the session is. A whole camera UI can bind to just this.</summary>
    public IReadableSignal<CameraState> State => _state;

    /// <summary>Why the last start or stream failed, or null. Cleared by the next start.</summary>
    public IReadableSignal<string?> Error => _error;

    /// <summary>
    ///     The manual controls — ISO, shutter, white balance, focus, locks and metering regions.
    ///     Set them any time: they are remembered across sessions and re-applied (clamped to what
    ///     the new lens can do) when one opens, so switching cameras does not silently drop the
    ///     photographer's settings.
    /// </summary>
    public CameraControls Controls { get; } = new();

    /// <summary>
    ///     What the running lens can actually do. <see cref="CameraCapabilities.None" /> while
    ///     idle. Build the UI from this — a dial for a control this reports as unsupported has
    ///     nothing to drive.
    /// </summary>
    public IReadableSignal<CameraCapabilities> Capabilities => _capabilities;

    /// <summary>
    ///     What the sensor did for the last presented frame, or null before the first one.
    ///     Updated in <see cref="Tick" />, so a readout bound to it repaints with the preview.
    /// </summary>
    public IReadableSignal<CaptureMetadata?> Metadata => _metadata;

    /// <summary>
    ///     How hot the device is, polled by the platform. Bind the preview's cost to it — an app
    ///     that keeps every overlay running into a thermal throttle just gets throttled harder.
    /// </summary>
    public IReadableSignal<ThermalState> Thermal => _thermal;

    /// <summary>Why the camera was taken away, while <see cref="CameraState.Interrupted" />.</summary>
    public IReadableSignal<string?> InterruptionReason => _interruption;

    /// <summary>
    ///     What this device can afford. Read once at startup — RAM does not change, and neither
    ///     should the preview size halfway through a session.
    /// </summary>
    public static DeviceTier Tier { get; } = CameraDriver.DeviceTier();

    /// <summary>The preview height this tier can carry. The default for <see cref="StartAsync" />.</summary>
    public static int TierPreviewHeight => Tier switch {
        DeviceTier.Low => 720,
        DeviceTier.Mid => 1080,
        _ => 1440,
    };

    /// <summary>How many stills may be in flight at once. The burst depth, and the memory cap.</summary>
    public static int TierStillBudget => Tier switch {
        DeviceTier.Low => 1,
        DeviceTier.Mid => 2,
        _ => 3,
    };

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
                onError: Fail,
                onInterrupted: Interrupt
            );

            // The new lens may offer less than the old one: pull the carried-over dials inside
            // what it can do before the first request, so nothing is silently ignored.
            // A driver that cannot do manual capture (desktop) still has to be able to drive the
            // UI that will: ZIGOTE_CAMERA_FAKE_CAPS reports a tier-A phone's ranges so the dials,
            // the tab-as-mode logic and the readout can be built and tested without a handset.
            // Nothing is applied — Metadata stays whatever the driver really said.
            _capabilities.Value = FakeCapabilities() ?? _session.Capabilities;

            // One line per session, at info: on a device this is the only way to find out what
            // the lens actually offered and what budget the app is running under, and it is the
            // first thing anyone debugging a camera report will ask for.
            var caps = _capabilities.Value;
            Console.WriteLine(
                $"[camera] tier={Tier} thermal={CameraDriver.Thermal()} " +
                $"preview<={TierPreviewHeight} stills<={TierStillBudget} " +
                $"iso={caps.Iso.Min}-{caps.Iso.Max} raw={caps.Raw} mf={caps.ManualFocus} " +
                $"regions={caps.Regions} kelvin={caps.Kelvin.Min}-{caps.Kelvin.Max}"
            );
            PushControls(); // clamps to the new lens, then applies
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

        // Thermal is polled here rather than subscribed: it changes over tens of seconds, the
        // platform calls are cheap, and a poll has nothing to unregister when the camera stops.
        if (++_thermalTick >= ThermalPollFrames)
        {
            _thermalTick = 0;
            var reading = CameraDriver.Thermal();
            if (reading != _thermal.Value) _thermal.Value = reading;
        }

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

        // The readout belongs to the frame on screen, so it updates here rather than whenever a
        // capture result happens to land.
        if (_session?.Metadata is { } metadata) _metadata.Value = metadata;

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
    public Task<byte[]> TakePhotoAsync(
        int quality = 90,
        CameraLut? lut = null,
        float lutStrength = 1f,
        Action<PaintList, Rect>? grade = null)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (_lastFrame is null)
            throw new InvalidOperationException("No camera frame to capture yet.");
        quality = Math.Clamp(value: quality, min: 1, max: 100);
        (int w, int h) = _lastFrameSize;

        bool graded = grade is not null || (lut is not null && lutStrength > 0f);
        if (!graded)
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
            Grade = grade,
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
        // An app that owns a richer pipeline supplies it; otherwise the LUT is the whole grade.
        // Either way the still runs the same passes the preview just showed, which is the only
        // reason the file can be trusted to match the viewfinder.
        if (photo.Grade is { } emit) emit(paint, full);
        else if (photo.Lut is { } lut) LutEffect.Paint(paint: paint, bounds: full, lut: lut, strength: photo.Strength);
        paint.PopRenderTexture();
        photo.Emitted = true;
    }

    /// <summary>
    ///     Capture the current frame through <paramref name="grade" /> and hand back the finished
    ///     <em>pixels</em>, so the caller can encode them once into whatever format it wants. The
    ///     alternative — take a JPEG, decode it, re-encode it — throws away a generation of
    ///     quality for a format preference, which is exactly backwards for a photographer.
    /// </summary>
    /// <remarks>Same painter requirement as <see cref="TakePhotoAsync" />.</remarks>
    public Task<CapturedPixels> TakePixelsAsync(CameraLut? lut = null, float lutStrength = 1f,
        Action<PaintList, Rect>? grade = null)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (_lastFrame is null) throw new InvalidOperationException("No camera frame to capture yet.");
        if (_photo is not null) throw new InvalidOperationException("A photo capture is already in flight.");

        (int w, int h) = _lastFrameSize;
        var engine = ZigoteEngine.Instance
                     ?? throw new InvalidOperationException("GPU photo capture needs a running engine.");

        ulong rt = engine.CreateRenderTexture(width: (uint)w, height: (uint)h);
        if (rt == 0) throw new InvalidOperationException("Could not create the photo render texture.");

        var tcs = new TaskCompletionSource<CapturedPixels>();
        _photo = new PhotoRequest {
            Tcs = new TaskCompletionSource<byte[]>(),
            PixelsTcs = tcs,
            Lut = lut,
            Grade = grade,
            Strength = lutStrength,
            Quality = 100,
            Width = w,
            Height = h,
            Rt = rt,
        };
        return tcs.Task;
    }

    /// <summary>
    ///     Capture a DNG straight off the sensor. Unlike <see cref="TakePhotoAsync" /> this does
    ///     not go through the preview stream or the GPU at all: it is a separate, full-resolution
    ///     capture, and the grade has nothing to do with it — a raw file is raw, and the Look is
    ///     something a developer applies later.
    /// </summary>
    /// <exception cref="NotSupportedException">This lens has no raw path — check
    ///     <see cref="Capabilities" /> first, and do not offer the option when it says no.</exception>
    public Task<RawPhoto> TakeRawAsync()
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        var session = _session
                      ?? throw new InvalidOperationException("The camera is not running.");
        if (!_capabilities.Value.Raw)
            throw new NotSupportedException("This camera cannot capture RAW.");

        return session.CaptureRawAsync();
    }

    /// <summary>A GPU photo pass is waiting for a painter — <see cref="CameraView" /> repaints on this.</summary>
    public bool PhotoPassPending => _photo is { Emitted: false };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifecycle.Dispose();
        _controlWatch.Dispose();
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
        // An idle camera can do nothing, and reports nothing: leaving the last lens's
        // capabilities up would let a UI keep drawing dials that drive a dead session.
        _capabilities.Value = CameraCapabilities.None;
        _metadata.Value = null;
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
    /// <summary>
    ///     Hand the current controls to the running session. Cheap and idempotent: a driver
    ///     rebuilds its request from a whole snapshot, so calling this more often than needed
    ///     costs a comparison, not a capture.
    /// </summary>
    /// <summary>
    ///     Tier-A capabilities for UI development, behind an environment variable so it can never
    ///     be mistaken for what a real camera reported. Values are a typical flagship's.
    /// </summary>
    private static CameraCapabilities? FakeCapabilities() =>
        Environment.GetEnvironmentVariable("ZIGOTE_CAMERA_FAKE_CAPS") switch {
            "1" or "A" => new CameraCapabilities(
                Iso: new IsoRange(Min: 50, Max: 6400),
                Shutter: new ShutterRange(MinNs: 60_000, MaxNs: 500_000_000),
                EvStep: 1f / 3f,
                EvRange: (-9, 9),
                Kelvin: (WhiteBalance.MinKelvin, WhiteBalance.MaxKelvin),
                Tint: true,
                MinFocusDiopters: 10f,
                ManualFocus: true,
                OisToggle: true,
                Regions: true,
                Raw: true
            ),
            // Tier B: manual exposure, no RAW, no manual focus or white balance.
            "B" => new CameraCapabilities(
                Iso: new IsoRange(Min: 100, Max: 3200),
                Shutter: new ShutterRange(MinNs: 100_000, MaxNs: 250_000_000),
                EvStep: 1f / 3f,
                EvRange: (-6, 6),
                Kelvin: (0, 0),
                Tint: false,
                MinFocusDiopters: 0f,
                ManualFocus: false,
                OisToggle: false,
                Regions: true,
                Raw: false
            ),
            // Tier C: a budget device — auto and EV, nothing else.
            "C" => CameraCapabilities.None with { EvStep = 1f / 3f, EvRange = (-6, 6) },
            _ => null,
        };

    private void PushControls()
    {
        if (_disposed || _clamping) return;

        // Clamp on the way in, not just inside the driver. If the signal kept a value the sensor
        // will never honour, every readout bound to it would report a lie — "ISO 999999" over a
        // frame shot at 6400. The guard is for the re-entrancy this causes: writing the clamped
        // values back re-enters through the same coalesced subscription.
        _clamping = true;
        try
        {
            Controls.ClampTo(_capabilities.Value);
        }
        finally
        {
            _clamping = false;
        }

        _session?.Apply(Controls.Snapshot());
    }

    private void Fail(string message)
    {
        StopSession();
        _error.Value = message;
        _state.Value = CameraState.Failed;
    }

    /// <summary>
    ///     The system took the camera. Distinct from a failure: the session is released (the HAL
    ///     demands it) but the intent to be running is kept, so <see cref="Resume" /> puts it back
    ///     without the app having to remember what it was doing.
    /// </summary>
    internal void Interrupt(string reason)
    {
        if (_disposed) return;
        StopSession();
        _interruption.Value = reason;
        _resumeOnForeground = true;
        _state.Value = CameraState.Interrupted;
    }

    /// <summary>
    ///     Reopen after an interruption or an idle teardown, with the same device, size and
    ///     controls. The controls survive because they live on the controller, not the session.
    /// </summary>
    public void Resume()
    {
        if (_disposed || _session is not null) return;
        _interruption.Value = null;
        _ = StartAsync(
            deviceId: _deviceId,
            maxHeight: _maxHeight,
            minimalProcessing: _minimalProcessing
        );
    }

    /// <summary>
    ///     Release the camera but remember that it should be running — for an app going idle. The
    ///     camera is the most expensive thing on the device; holding it open behind a still
    ///     viewfinder nobody is watching is pure battery.
    /// </summary>
    public void Idle()
    {
        if (_disposed || _session is null) return;
        StopSession();
        _resumeOnForeground = true;
        _interruption.Value = "paused to save power";
        _state.Value = CameraState.Interrupted;
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
            var failure = new InvalidOperationException("Photo readback failed.");
            photo.Tcs.TrySetException(failure);
            photo.PixelsTcs?.TrySetException(failure);
            return;
        }

        if (photo.PixelsTcs is { } pixels)
        {
            pixels.TrySetResult(new CapturedPixels(
                Rgba: rgba,
                Width: photo.Width,
                Height: photo.Height
            ));
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

        /// <summary>
        ///     Set when the caller wants the graded pixels rather than an encoded file. It then
        ///     encodes once, into whatever format it likes — rather than us encoding a JPEG the
        ///     caller has to decode and re-encode, which would cost a generation for nothing.
        /// </summary>
        public TaskCompletionSource<CapturedPixels>? PixelsTcs { get; init; }
        public required CameraLut? Lut { get; init; }

        /// <summary>An app-supplied grade emitted over the frame, in place of the plain LUT pass.</summary>
        public Action<PaintList, Rect>? Grade { get; init; }
        public required float Strength { get; init; }
        public required int Quality { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required ulong Rt { get; init; }
        public bool Emitted { get; set; }
    }
}
