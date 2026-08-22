using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.Provider;
using Android.OS;
using Android.Runtime;

namespace Camera;

/// <summary>
///     Android capture (<c>net10.0-android</c>): camera2 through the base platform bindings — no
///     CameraX NuGet chain. The camera delivers YUV_420_888 into an <see cref="ImageReader" /> on
///     a dedicated handler thread, where one tight loop converts to RGBA with the sensor rotation
///     folded into the write index, and publishes into the mailbox.
///     <para>
///         The permission prompt has no activity-result plumbing of its own here: the request goes
///         through the SDL activity, and the answer is re-read when the app resumes — the prompt
///         is an overlay activity, so dismissing it always resumes us.
///     </para>
/// </summary>
internal static class CameraDriver
{
    private const int PermissionRequestCode = 0xCA;
    private static readonly List<TaskCompletionSource<bool>> PermissionWaiters = [];

    public static Task<bool> RequestPermissionAsync()
    {
        var context = Application.Context;
        if (context.CheckSelfPermission(global::Android.Manifest.Permission.Camera) == Permission.Granted)
            return Task.FromResult(true);

        var activity = CurrentActivity();
        if (activity is null) return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>();
        lock (PermissionWaiters)
        {
            PermissionWaiters.Add(tcs);
        }

        activity.RequestPermissions(
            permissions: [global::Android.Manifest.Permission.Camera],
            requestCode: PermissionRequestCode
        );
        return tcs.Task;
    }

    /// <summary>Resumed after the permission dialog (or anything else): settle the waiters.</summary>
    public static void OnAppLifecycle(bool paused)
    {
        if (paused) return;

        TaskCompletionSource<bool>[] waiters;
        lock (PermissionWaiters)
        {
            if (PermissionWaiters.Count == 0) return;
            waiters = PermissionWaiters.ToArray();
            PermissionWaiters.Clear();
        }

        bool granted = Application.Context.CheckSelfPermission(
            global::Android.Manifest.Permission.Camera
        ) == Permission.Granted;
        foreach (var tcs in waiters) tcs.TrySetResult(granted);
    }

    /// <summary>
    ///     What the device can afford. <c>isLowRamDevice</c> is the system's own answer to that
    ///     question, so it is used rather than a RAM threshold of our invention; above it, total
    ///     RAM separates the mid-range from a flagship.
    /// </summary>
    public static DeviceTier DeviceTier()
    {
        try
        {
            var am = (ActivityManager)Application.Context.GetSystemService(Context.ActivityService)!;
            if (am.IsLowRamDevice) return Camera.DeviceTier.Low;

            var info = new ActivityManager.MemoryInfo();
            am.GetMemoryInfo(info);
            long gigabytes = info.TotalMem / (1024L * 1024L * 1024L);
            return gigabytes >= 10 ? Camera.DeviceTier.High
                : gigabytes >= 5 ? Camera.DeviceTier.Mid
                : Camera.DeviceTier.Low;
        }
        catch (Exception)
        {
            // Unknown means assume the smaller budget: overshooting memory kills the process,
            // undershooting only costs preview resolution.
            return Camera.DeviceTier.Low;
        }
    }

    /// <summary>The platform's own thermal reading; polled rather than subscribed, so there is
    ///     nothing to unregister when the camera stops.</summary>
    public static ThermalState Thermal()
    {
        try
        {
            var pm = (PowerManager)Application.Context.GetSystemService(Context.PowerService)!;
            return (int)pm.CurrentThermalStatus switch {
                >= 4 => ThermalState.Critical, // CRITICAL, EMERGENCY, SHUTDOWN
                3 => ThermalState.Hot,         // SEVERE
                2 => ThermalState.Warm,        // MODERATE
                _ => ThermalState.Nominal,     // NONE, LIGHT
            };
        }
        catch (Exception)
        {
            return ThermalState.Nominal;
        }
    }

    /// <summary>
    ///     Insert into MediaStore so the photo appears in the system gallery. Written while
    ///     <c>IS_PENDING</c> is set and cleared only once the bytes are down, so a half-written
    ///     file is never visible to anything — the same reason the app writes ahead to its own
    ///     folder first. Needs no storage permission on any supported API.
    /// </summary>
    public static Task<string> PublishPhotoAsync(byte[] bytes, string fileName, string album) =>
        Task.Run(() =>
            {
                var resolver = Application.Context.ContentResolver
                               ?? throw new InvalidOperationException("No content resolver.");

                var values = new ContentValues();
                values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
                values.Put(MediaStore.IMediaColumns.MimeType, MimeFor(fileName));
                values.Put(
                    MediaStore.IMediaColumns.RelativePath,
                    System.IO.Path.Combine(
                        path1: global::Android.OS.Environment.DirectoryPictures!,
                        path2: album
                    )
                );
                values.Put(MediaStore.IMediaColumns.IsPending, 1);

                var collection = MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternalPrimary)
                                 ?? throw new InvalidOperationException("No media collection.");
                var uri = resolver.Insert(url: collection, values: values)
                          ?? throw new InvalidOperationException("MediaStore rejected the insert.");

                try
                {
                    using (var stream = resolver.OpenOutputStream(uri)
                                        ?? throw new InvalidOperationException("Could not open the entry."))
                    {
                        stream.Write(buffer: bytes, offset: 0, count: bytes.Length);
                        stream.Flush();
                    }

                    // Only now is it a photograph rather than a partial file.
                    var done = new ContentValues();
                    done.Put(MediaStore.IMediaColumns.IsPending, 0);
                    resolver.Update(uri: uri, values: done, where: null, selectionArgs: null);
                    return uri.ToString() ?? fileName;
                }
                catch (Exception)
                {
                    // A failed write leaves a pending row nothing can see; drop it rather than
                    // leaving a permanent invisible stub in the user's library.
                    try
                    {
                        resolver.Delete(url: uri, where: null, selectionArgs: null);
                    }
                    catch (Exception)
                    {
                        // Nothing further to do; the row stays pending and invisible.
                    }

                    throw;
                }
            }
        );

    private static string MimeFor(string fileName) =>
        System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch {
        ".png" => "image/png",
        ".jxl" => "image/jxl",
        ".dng" => "image/x-adobe-dng",
        _ => "image/jpeg",
    };

    public static Task<CameraDeviceInfo[]> GetDevicesAsync()
    {
        var manager = Manager();
        var devices = new List<CameraDeviceInfo>();
        foreach (string id in manager.GetCameraIdList())
        {
            var facing = Facing(manager.GetCameraCharacteristics(id));
            devices.Add(new CameraDeviceInfo(
                Id: id,
                Name: facing switch {
                    CameraFacing.Front => "Front camera",
                    CameraFacing.Back => "Back camera",
                    _ => $"Camera {id}",
                },
                Facing: facing
            ));
        }

        return Task.FromResult(devices.ToArray());
    }

    public static ICameraSession Open(
        string? deviceId,
        int maxHeight,
        bool minimalProcessing,
        FrameMailbox frames,
        Action<string> onError,
        Action<string>? onInterrupted = null) =>
        new Camera2Session(
            deviceId: deviceId,
            maxHeight: maxHeight,
            minimalProcessing: minimalProcessing,
            frames: frames,
            onError: onError,
            onInterrupted: onInterrupted
        );

    /// <summary>
    ///     Android's own encoders. JPEG XL needs libjxl, which the platform does not provide and
    ///     this plugin does not bundle, so it is reported unavailable rather than silently
    ///     substituted.
    /// </summary>
    public static bool SupportsFormat(StillFormat format) =>
        format is StillFormat.Jpeg or StillFormat.Png;

    public static Task<byte[]> EncodeAsync(
        byte[] rgba,
        int width,
        int height,
        StillFormat format,
        int quality) =>
        Task.Run(() =>
            {
                using var bitmap = Bitmap.CreateBitmap(
                    width: width,
                    height: height,
                    config: Bitmap.Config.Argb8888!
                );
                bitmap.CopyPixelsFromBuffer(Java.Nio.ByteBuffer.Wrap(rgba));
                using var stream = new MemoryStream();
                var codec = format == StillFormat.Png
                    ? Bitmap.CompressFormat.Png!
                    : Bitmap.CompressFormat.Jpeg!;
                if (!bitmap.Compress(format: codec, quality: quality, stream: stream))
                    throw new InvalidOperationException($"{format} encode failed.");
                return stream.ToArray();
            }
        );

    /// <summary>ARGB_8888's in-memory layout IS the RGBA byte order the frames already have.</summary>
    public static Task<byte[]> EncodeJpegAsync(byte[] rgba, int width, int height, int quality) =>
        Task.Run(() =>
            {
                using var bitmap = Bitmap.CreateBitmap(
                    width: width,
                    height: height,
                    config: Bitmap.Config.Argb8888!
                );
                bitmap.CopyPixelsFromBuffer(Java.Nio.ByteBuffer.Wrap(rgba));
                using var stream = new MemoryStream();
                if (!bitmap.Compress(format: Bitmap.CompressFormat.Jpeg!, quality: quality, stream: stream))
                    throw new InvalidOperationException("JPEG encode failed.");
                return stream.ToArray();
            }
        );

    private static CameraManager Manager() =>
        (CameraManager)Application.Context.GetSystemService(Context.CameraService)!;

    private static CameraFacing Facing(CameraCharacteristics characteristics) =>
        (LensFacing?)IntOf(characteristics.Get(CameraCharacteristics.LensFacing!)) switch {
            LensFacing.Front => CameraFacing.Front,
            LensFacing.Back => CameraFacing.Back,
            _ => CameraFacing.External,
        };

    private static int IntOf(Java.Lang.Object? value) =>
        (value as Java.Lang.Integer)?.IntValue() ?? -1;

    private static long LongOf(Java.Lang.Object? value) =>
        (value as Java.Lang.Long)?.LongValue() ?? -1L;

    private static float FloatOf(Java.Lang.Object? value) =>
        (value as Java.Lang.Float)?.FloatValue() ?? float.NaN;

    /// <summary>
    ///     Read an <c>android.util.Range&lt;T&gt;</c> characteristic. The generic binding hands
    ///     back <see cref="Java.Lang.Object" /> bounds, so each caller narrows with IntOf/LongOf.
    /// </summary>
    private static (Java.Lang.Object? Lower, Java.Lang.Object? Upper) RangeOf(Java.Lang.Object? value)
    {
        if (value is not global::Android.Util.Range range) return (null, null);
        return (range.Lower, range.Upper);
    }

    /// <summary>
    ///     What one camera id can do, straight from its characteristics. Everything here is a
    ///     capability query, never a guess: a device that does not advertise MANUAL_SENSOR gets
    ///     no exposure dials at all, which is the whole point of publishing this record.
    /// </summary>
    private static CameraCapabilities ProbeCapabilities(CameraCharacteristics c)
    {
        int[] caps = (int[]?)(c.Get(CameraCharacteristics.RequestAvailableCapabilities!)
                              as Java.Lang.Object)?.ToArray<int>() ?? [];
        bool manualSensor = Array.IndexOf(array: caps, value: 1) >= 0; // MANUAL_SENSOR
        bool raw = Array.IndexOf(array: caps, value: 3) >= 0;          // RAW

        // Sensitivity and exposure time are only meaningful with MANUAL_SENSOR; a LIMITED device
        // reports ranges it will ignore, and a dial that does nothing is worse than no dial.
        var iso = default(IsoRange);
        var shutter = default(ShutterRange);
        if (manualSensor)
        {
            (var isoLo, var isoHi) = RangeOf(c.Get(CameraCharacteristics.SensorInfoSensitivityRange!));
            if (isoLo is not null) iso = new IsoRange(Min: IntOf(isoLo), Max: IntOf(isoHi));

            (var expLo, var expHi) = RangeOf(c.Get(CameraCharacteristics.SensorInfoExposureTimeRange!));
            if (expLo is not null) shutter = new ShutterRange(MinNs: LongOf(expLo), MaxNs: LongOf(expHi));
        }

        (var evLo, var evHi) = RangeOf(c.Get(CameraCharacteristics.ControlAeCompensationRange!));
        (int evMin, int evMax) = evLo is null ? (0, 0) : (IntOf(evLo), IntOf(evHi));

        float evStep = 0f;
        if (c.Get(CameraCharacteristics.ControlAeCompensationStep!) is global::Android.Util.Rational step
            && step.Denominator != 0)
            evStep = (float)step.Numerator / step.Denominator;

        // Manual focus is diopters here: LENS_INFO_MINIMUM_FOCUS_DISTANCE of 0 means the lens is
        // fixed-focus, which is the norm for ultra-wides even on flagship phones.
        float minFocus = FloatOf(c.Get(CameraCharacteristics.LensInfoMinimumFocusDistance!));
        bool manualFocus = minFocus > 0f;

        int[] ois = (int[]?)(c.Get(CameraCharacteristics.LensInfoAvailableOpticalStabilization!)
                             as Java.Lang.Object)?.ToArray<int>() ?? [];
        bool oisToggle = ois.Length > 1; // more than one mode means OFF is one of them

        bool regions = IntOf(c.Get(CameraCharacteristics.ControlMaxRegionsAe!)) > 0
                       || IntOf(c.Get(CameraCharacteristics.ControlMaxRegionsAf!)) > 0;

        // Camera2 has no Kelvin API — COLOR_CORRECTION_GAINS is the mechanism, and WhiteBalance
        // supplies the conversion. So manual WB is available exactly when manual colour
        // correction is, which MANUAL_POST_PROCESSING (capability 2) advertises.
        bool manualPost = Array.IndexOf(array: caps, value: 2) >= 0;
        (int, int) kelvin = manualPost ? (WhiteBalance.MinKelvin, WhiteBalance.MaxKelvin) : (0, 0);

        return new CameraCapabilities(
            Iso: iso,
            Shutter: shutter,
            EvStep: evStep,
            EvRange: (evMin, evMax),
            Kelvin: kelvin,
            Tint: manualPost,
            MinFocusDiopters: minFocus,
            ManualFocus: manualFocus,
            OisToggle: oisToggle,
            Regions: regions,
            Raw: raw
        );
    }

    /// <summary>The SDL activity that owns the process's window, via its own static singleton.</summary>
    private static Activity? CurrentActivity()
    {
        try
        {
            IntPtr cls = JNIEnv.FindClass("org/libsdl/app/SDLActivity");
            IntPtr field = JNIEnv.GetStaticFieldID(
                jclass: cls,
                name: "mSingleton",
                sig: "Lorg/libsdl/app/SDLActivity;"
            );
            IntPtr handle = JNIEnv.GetStaticObjectField(jclass: cls, jfieldID: field);
            return handle == IntPtr.Zero
                ? null
                : Java.Lang.Object.GetObject<Activity>(handle: handle, transfer: JniHandleOwnership.TransferLocalRef);
        }
        catch (Exception)
        {
            // A head not built on the SDL activity: permission checks still work, prompts don't.
            return null;
        }
    }

    private sealed class Camera2Session : ICameraSession
    {
        private readonly FrameMailbox _frames;
        private readonly Action<string> _onError;
        private readonly Action<string>? _onInterrupted;
        private readonly HandlerThread _thread;
        private readonly Handler _handler;
        private readonly object _gate = new();

        private readonly bool _minimalProcessing;
        private readonly global::Android.Graphics.Rect? _activeArray;
        private readonly CameraCharacteristics _characteristics;
        private ImageReader? _rawReader;
        private TaskCompletionSource<RawPhoto>? _rawPending;
        private CameraDevice? _device;
        private CameraCaptureSession? _session;
        private ImageReader? _reader;
        private int _rotation;
        private bool _disposed;

        /// <summary>Last controls asked for. Written by the app thread, read by the handler thread.</summary>
        private ControlState _controls;

        /// <summary>Last capture result, published whole so a reader never sees half a record.</summary>
        private volatile CaptureMetadata? _metadata;

        public CameraCapabilities Capabilities { get; private set; } = CameraCapabilities.None;

        public CaptureMetadata? Metadata => _metadata;

        /// <summary>
        ///     Take the new controls and rebuild the repeating request. Called on the app thread,
        ///     so the camera work is posted to the handler thread rather than done here.
        /// </summary>
        public void Apply(in ControlState controls)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _controls = controls;
                if (_session is null) return; // picked up by OnConfigured when the session lands
            }

            _handler.Post(RestartRepeating);
        }

        public Camera2Session(
            string? deviceId,
            int maxHeight,
            bool minimalProcessing,
            FrameMailbox frames,
            Action<string> onError,
            Action<string>? onInterrupted = null)
        {
            _frames = frames;
            _onError = onError;
            _onInterrupted = onInterrupted;
            _minimalProcessing = minimalProcessing;
            _thread = new HandlerThread("zigote-camera");
            _thread.Start();
            _handler = new Handler(_thread.Looper!);

            var manager = Manager();
            string id = deviceId ?? DefaultDeviceId(manager)
                ?? throw new InvalidOperationException("No camera found.");

            var characteristics = manager.GetCameraCharacteristics(id);
            _characteristics = characteristics;
            Capabilities = ProbeCapabilities(characteristics);
            // Metering rectangles are in sensor pixels, so the normalized regions the app sets
            // need this to be mapped; null means the device does not report it and regions are
            // skipped rather than sent as garbage coordinates.
            _activeArray = characteristics.Get(CameraCharacteristics.SensorInfoActiveArraySize!)
                as global::Android.Graphics.Rect;
            _rotation = IntOf(characteristics.Get(CameraCharacteristics.SensorOrientation!)) is var r and >= 0
                ? r
                : 0;
            (int w, int h) = PreviewSize(characteristics: characteristics, maxHeight: maxHeight);

            _reader = ImageReader.NewInstance(
                width: w,
                height: h,
                format: ImageFormatType.Yuv420888,
                maxImages: 3
            );
            _reader.SetOnImageAvailableListener(listener: new FrameListener(this), handler: _handler);

            // A second target, at the sensor's full raw size. maxImages is 1 deliberately: a
            // 50 MP raw frame is ~100 MB, and queueing two is how a camera app gets killed.
            if (Capabilities.Raw && LargestRawSize(characteristics) is { } rawSize)
            {
                _rawReader = ImageReader.NewInstance(
                    width: rawSize.Width,
                    height: rawSize.Height,
                    format: ImageFormatType.RawSensor,
                    maxImages: 1
                );
            }

            manager.OpenCamera(cameraId: id, callback: new DeviceCallback(this), handler: _handler);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _session?.Close();
                _session = null;
                _device?.Close();
                _device = null;
                _reader?.Close();
                _reader = null;
                _rawReader?.Close();
                _rawReader = null;
                _rawPending?.TrySetCanceled();
                _rawPending = null;
            }

            _thread.QuitSafely();
        }

        /// <summary>Back camera when there is one; whatever exists otherwise.</summary>
        private static string? DefaultDeviceId(CameraManager manager)
        {
            string[] ids = manager.GetCameraIdList();
            foreach (string id in ids)
            {
                if (Facing(manager.GetCameraCharacteristics(id)) == CameraFacing.Back) return id;
            }

            return ids.FirstOrDefault();
        }

        /// <summary>The sensor's biggest RAW_SENSOR output, or null if it advertises none.</summary>
        private static global::Android.Util.Size? LargestRawSize(CameraCharacteristics characteristics)
        {
            var map = (StreamConfigurationMap?)characteristics.Get(
                CameraCharacteristics.ScalerStreamConfigurationMap!
            );
            var sizes = map?.GetOutputSizes((int)ImageFormatType.RawSensor);
            if (sizes is null || sizes.Length == 0) return null;

            global::Android.Util.Size? best = null;
            foreach (var size in sizes)
                if (best is null || (long)size.Width * size.Height > (long)best.Width * best.Height)
                    best = size;

            return best;
        }

        /// <summary>
        ///     One still capture into the raw reader. The DNG needs both the pixels and the
        ///     result that describes them — colour matrices, black levels, the lens's opcode
        ///     lists — so the image is held until its result arrives and written by the platform's
        ///     own <see cref="DngCreator" />, which knows all of that better than we could.
        /// </summary>
        public Task<RawPhoto> CaptureRawAsync()
        {
            lock (_gate)
            {
                if (_disposed || _rawReader is null || _session is null || _device is null)
                    return Task.FromException<RawPhoto>(
                        new InvalidOperationException("The camera has no RAW path running.")
                    );

                if (_rawPending is not null)
                    return Task.FromException<RawPhoto>(
                        new InvalidOperationException("A RAW capture is already in flight.")
                    );

                _rawPending = new TaskCompletionSource<RawPhoto>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
            }

            _handler.Post(SubmitRawCapture);
            return _rawPending.Task;
        }

        private void SubmitRawCapture()
        {
            CameraCaptureSession? session;
            CameraDevice? device;
            ControlState controls;
            lock (_gate)
            {
                if (_disposed) return;
                session = _session;
                device = _device;
                controls = _controls;
            }

            if (session is null || device is null || _rawReader is null) return;

            try
            {
                // STILL_CAPTURE, not PREVIEW: it is the template that asks the HAL for its best
                // quality rather than its lowest latency.
                var request = device.CreateCaptureRequest(CameraTemplate.StillCapture);
                request.AddTarget(_rawReader.Surface!);

                // A raw file is raw: no noise reduction, no sharpening, nothing that would bake a
                // decision into data whose whole point is that no decisions have been made yet.
                request.Set(key: CaptureRequest.NoiseReductionMode!, value: (int)NoiseReductionMode.Off);
                request.Set(key: CaptureRequest.EdgeMode!, value: (int)EdgeMode.Off);

                // The exposure the photographer set still applies — that is a decision about
                // light, not about processing.
                ApplyExposure(request: request, controls: controls);
                ApplyFocus(request: request, controls: controls);

                session.Capture(
                    request: request.Build(),
                    listener: new RawCaptureListener(this),
                    handler: _handler
                );
            }
            catch (Exception ex)
            {
                CompleteRaw(result: null, error: ex);
            }
        }

        /// <summary>On the handler thread: pixels plus their result become one DNG.</summary>
        private void OnRawResult(TotalCaptureResult result)
        {
            Image? image = null;
            try
            {
                image = _rawReader?.AcquireNextImage();
                if (image is null)
                {
                    CompleteRaw(
                        result: null,
                        error: new InvalidOperationException("The RAW capture produced no image.")
                    );
                    return;
                }

                using var dng = new DngCreator(_characteristics, result);
                if (_rotation != 0) dng.SetOrientation(OrientationOf(_rotation));

                using var stream = new MemoryStream();
                dng.WriteImage(dngOutput: stream, pixels: image);

                CompleteRaw(
                    result: new RawPhoto(Dng: stream.ToArray(), Metadata: MetadataOf(result)),
                    error: null
                );
            }
            catch (Exception ex)
            {
                CompleteRaw(result: null, error: ex);
            }
            finally
            {
                image?.Close();
            }
        }

        private void CompleteRaw(RawPhoto? result, Exception? error)
        {
            TaskCompletionSource<RawPhoto>? pending;
            lock (_gate)
            {
                pending = _rawPending;
                _rawPending = null;
            }

            if (pending is null) return;
            if (result is not null) pending.TrySetResult(result);
            else pending.TrySetException(error ?? new InvalidOperationException("RAW capture failed."));
        }

        /// <summary>Sensor rotation as the EXIF orientation a DNG records.</summary>
        private static global::Android.Media.Orientation OrientationOf(int degrees) => degrees switch {
            90 => global::Android.Media.Orientation.Rotate90,
            180 => global::Android.Media.Orientation.Rotate180,
            270 => global::Android.Media.Orientation.Rotate270,
            _ => global::Android.Media.Orientation.Normal,
        };

        /// <summary>
        ///     Largest YUV output whose short side fits the cap (sensor sizes are landscape, so
        ///     the cap applies to the height after rotation — the short side either way).
        /// </summary>
        private static (int Width, int Height) PreviewSize(
            CameraCharacteristics characteristics,
            int maxHeight)
        {
            var map = (StreamConfigurationMap)characteristics.Get(
                CameraCharacteristics.ScalerStreamConfigurationMap!
            )!;
            var sizes = map.GetOutputSizes((int)ImageFormatType.Yuv420888)
                        ?? throw new InvalidOperationException("Camera reports no YUV output sizes.");

            global::Android.Util.Size? best = null;
            global::Android.Util.Size? smallest = null;
            foreach (var size in sizes)
            {
                long area = (long)size.Width * size.Height;
                if (smallest is null || area < (long)smallest.Width * smallest.Height) smallest = size;
                if (maxHeight > 0 && Math.Min(size.Width, size.Height) > maxHeight) continue;
                if (best is null || area > (long)best.Width * best.Height) best = size;
            }

            var chosen = best ?? smallest!;
            return (chosen.Width, chosen.Height);
        }

        /// <summary>The system took the camera. Recoverable; the app reopens when it can.</summary>
        private void Interrupted(string reason)
        {
            bool report;
            lock (_gate)
            {
                report = !_disposed;
            }

            if (report) (_onInterrupted ?? _onError)(reason);
        }

        private void Fail(string message)
        {
            bool report;
            lock (_gate)
            {
                report = !_disposed;
            }

            if (report) _onError(message);
        }

        private void OnOpened(CameraDevice device)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    device.Close();
                    return;
                }

                _device = device;
            }

            try
            {
                // Both targets are declared up front: camera2 fixes a session's outputs at
                // configuration, so a RAW surface added later would need the whole session torn
                // down and rebuilt — a visible stall in the middle of taking a photograph.
                var outputs = _rawReader is null
                    ? new List<global::Android.Views.Surface> { _reader!.Surface! }
                    : [_reader!.Surface!, _rawReader.Surface!];

                device.CreateCaptureSession(
                    outputs: outputs,
                    callback: new SessionCallback(this),
                    handler: _handler
                );
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
        }

        private void OnConfigured(CameraCaptureSession session)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    session.Close();
                    return;
                }

                _session = session;
            }

            RestartRepeating();
        }

        /// <summary>
        ///     Build the repeating request from the current controls and submit it. Runs on the
        ///     handler thread — both from <see cref="OnConfigured" /> and from every
        ///     <see cref="Apply" />. Camera2 has no per-key setter on a running session: changing
        ///     one control means submitting a whole new request, which is why the controls arrive
        ///     as one snapshot.
        /// </summary>
        private void RestartRepeating()
        {
            CameraCaptureSession? session;
            CameraDevice? device;
            ControlState controls;
            lock (_gate)
            {
                if (_disposed) return;
                session = _session;
                device = _device;
                controls = _controls;
            }

            if (session is null || device is null) return;

            try
            {
                var request = device.CreateCaptureRequest(CameraTemplate.Preview);
                request.AddTarget(_reader!.Surface!);
                if (_minimalProcessing)
                {
                    // The DSLR-flat option: strip the vendor look as far as camera2 allows so the
                    // frames are the sensor pipeline's neutral output — the app's own GPU grade is
                    // the look.
                    request.Set(key: CaptureRequest.NoiseReductionMode!, value: (int)NoiseReductionMode.Off);
                    request.Set(key: CaptureRequest.EdgeMode!, value: (int)EdgeMode.Off);
                    request.Set(
                        key: CaptureRequest.ControlVideoStabilizationMode!,
                        value: (int)ControlVideoStabilizationMode.Off
                    );
                }

                ApplyExposure(request: request, controls: controls);
                ApplyWhiteBalance(request: request, controls: controls);
                ApplyFocus(request: request, controls: controls);
                ApplyRegionsAndLocks(request: request, controls: controls);

                if (Capabilities.OisToggle)
                    request.Set(
                        key: CaptureRequest.LensOpticalStabilizationMode!,
                        value: (int)(controls.Ois
                            ? LensOpticalStabilizationMode.On
                            : LensOpticalStabilizationMode.Off)
                    );

                session.SetRepeatingRequest(
                    request: request.Build(),
                    listener: new ResultListener(this),
                    handler: _handler
                );
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
        }

        /// <summary>
        ///     ISO and shutter. Camera2 has no priority modes: AE is either on or off, so
        ///     "ISO priority" is AE on with a sensitivity pinned, and only both-manual turns AE
        ///     off outright. An auto-ISO ceiling has no key at all — it is expressed by capping
        ///     the sensitivity the device may choose, which only bites once AE is off, so it is
        ///     honoured on the manual path and reported through metadata otherwise.
        /// </summary>
        private void ApplyExposure(CaptureRequest.Builder request, in ControlState controls)
        {
            bool iso = controls.IsoManual && Capabilities.Iso.Supported;
            bool shutter = controls.ShutterManual && Capabilities.Shutter.Supported;

            if (iso && shutter)
            {
                request.Set(key: CaptureRequest.ControlAeMode!, value: (int)ControlAEMode.Off);
                request.Set(
                    key: CaptureRequest.SensorSensitivity!,
                    value: Capabilities.Iso.Clamp(controls.Iso)
                );
                request.Set(
                    key: CaptureRequest.SensorExposureTime!,
                    value: Capabilities.Shutter.Clamp(controls.ShutterNs)
                );
                return;
            }

            request.Set(key: CaptureRequest.ControlAeMode!, value: (int)ControlAEMode.On);

            // One half fixed, the other metered. AE would override a bare sensitivity, so pin the
            // fixed half and let the device solve for the rest.
            if (iso) request.Set(key: CaptureRequest.SensorSensitivity!, value: Capabilities.Iso.Clamp(controls.Iso));
            if (shutter)
                request.Set(
                    key: CaptureRequest.SensorExposureTime!,
                    value: Capabilities.Shutter.Clamp(controls.ShutterNs)
                );

            if (Capabilities.EvStep > 0f && controls.EvCompensation != 0f)
            {
                int steps = (int)Math.Round(controls.EvCompensation / Capabilities.EvStep);
                request.Set(
                    key: CaptureRequest.ControlAeExposureCompensation!,
                    value: Math.Clamp(
                        value: steps,
                        min: Capabilities.EvRange.Min,
                        max: Capabilities.EvRange.Max
                    )
                );
            }

            // A shutter floor for the device's own choices: the AE target fps range is the only
            // lever camera2 gives, since a frame cannot be longer than its interval.
            if (controls.MinAutoShutterNs > 0)
            {
                int maxFps = (int)Math.Clamp(
                    value: 1_000_000_000L / controls.MinAutoShutterNs,
                    min: 1L,
                    max: 60L
                );
                request.Set(
                    key: CaptureRequest.ControlAeTargetFpsRange!,
                    value: new global::Android.Util.Range(
                        Java.Lang.Integer.ValueOf(Math.Min(val1: 15, val2: maxFps)),
                        Java.Lang.Integer.ValueOf(maxFps)
                    )
                );
            }
        }

        /// <summary>
        ///     White balance as channel gains, because camera2 has no temperature key. Turning AWB
        ///     off without supplying a transform leaves the colour matrix undefined on some HALs,
        ///     so the identity transform goes with it.
        /// </summary>
        private void ApplyWhiteBalance(CaptureRequest.Builder request, in ControlState controls)
        {
            if (!controls.WhiteBalanceManual || Capabilities.Kelvin.Max <= 0)
            {
                request.Set(key: CaptureRequest.ControlAwbMode!, value: (int)ControlAwbMode.Auto);
                return;
            }

            (float r, float g, float b) = WhiteBalance.GainsFor(
                kelvin: controls.WhiteBalanceKelvin,
                tint: controls.WhiteBalanceTint
            );

            request.Set(key: CaptureRequest.ControlAwbMode!, value: (int)ControlAwbMode.Off);
            request.Set(
                key: CaptureRequest.ColorCorrectionMode!,
                value: (int)ColorCorrectionMode.TransformMatrix
            );
            request.Set(
                key: CaptureRequest.ColorCorrectionGains!,
                value: new RggbChannelVector(red: r, greenEven: g, greenOdd: g, blue: b)
            );
        }

        private void ApplyFocus(CaptureRequest.Builder request, in ControlState controls)
        {
            if (controls.FocusManual && Capabilities.ManualFocus)
            {
                request.Set(key: CaptureRequest.ControlAfMode!, value: (int)ControlAFMode.Off);
                request.Set(
                    key: CaptureRequest.LensFocusDistance!,
                    value: Math.Clamp(
                        value: controls.FocusDiopters,
                        min: 0f,
                        max: Capabilities.MinFocusDiopters
                    )
                );
                return;
            }

            // AF_MODE_AUTO would focus once and stop; a viewfinder wants the continuous mode, and
            // an AF lock is expressed by simply not asking for a new trigger.
            request.Set(
                key: CaptureRequest.ControlAfMode!,
                value: (int)(controls.AfLock ? ControlAFMode.Auto : ControlAFMode.ContinuousPicture)
            );
        }

        private void ApplyRegionsAndLocks(CaptureRequest.Builder request, in ControlState controls)
        {
            request.Set(key: CaptureRequest.ControlAeLock!, value: controls.AeLock);
            request.Set(key: CaptureRequest.ControlAwbLock!, value: controls.AwbLock);

            if (!Capabilities.Regions || _activeArray is null) return;

            if (ToMeteringRectangle(controls.AeRegion) is { } ae)
                request.Set(key: CaptureRequest.ControlAeRegions!, value: new[] { ae });

            // A null AF region follows the AE one: that is the merged reticle, which is what a
            // single tap on the viewfinder means.
            if (ToMeteringRectangle(controls.AfRegion ?? controls.AeRegion) is { } af)
                request.Set(key: CaptureRequest.ControlAfRegions!, value: new[] { af });
        }

        /// <summary>Normalized frame rectangle → sensor pixels, clipped to the active array.</summary>
        private MeteringRectangle? ToMeteringRectangle(Zigote.Core.Rect? region)
        {
            if (region is not { } r || _activeArray is null) return null;

            int aw = _activeArray.Width(), ah = _activeArray.Height();
            int x = _activeArray.Left + (int)(Math.Clamp(value: r.X, min: 0f, max: 1f) * aw);
            int y = _activeArray.Top + (int)(Math.Clamp(value: r.Y, min: 0f, max: 1f) * ah);
            int w = Math.Max(val1: 1, val2: (int)(Math.Clamp(value: r.Width, min: 0f, max: 1f) * aw));
            int h = Math.Max(val1: 1, val2: (int)(Math.Clamp(value: r.Height, min: 0f, max: 1f) * ah));

            // A rectangle running past the array is rejected outright by some HALs, taking the
            // whole request with it — clamp the far edge rather than the origin.
            w = Math.Min(val1: w, val2: _activeArray.Right - x);
            h = Math.Min(val1: h, val2: _activeArray.Bottom - y);
            if (w <= 0 || h <= 0) return null;

            return new MeteringRectangle(
                x: x,
                y: y,
                width: w,
                height: h,
                meteringWeight: MeteringRectangle.MeteringWeightMax
            );
        }

        /// <summary>
        ///     Turn a capture result into the readout record. Runs on the handler thread for every
        ///     frame, so it does no allocation beyond the record itself and never touches the HAL.
        /// </summary>
        private void OnResult(TotalCaptureResult result)
        {
            try
            {
                _metadata = MetadataOf(result);
            }
            catch (Exception)
            {
                // A HAL that omits a key must not kill the preview: the readout just goes stale.
            }
        }

        /// <summary>One capture result as the readout record. Shared by the preview and RAW.</summary>
        private static CaptureMetadata? MetadataOf(TotalCaptureResult result)
        {
            try
            {
                int aeState = IntOf(result.Get(CaptureResult.ControlAeState!));
                int afState = IntOf(result.Get(CaptureResult.ControlAfState!));

                return new CaptureMetadata(
                    Iso: IntOf(result.Get(CaptureResult.SensorSensitivity!)),
                    ShutterNs: LongOf(result.Get(CaptureResult.SensorExposureTime!)),
                    Aperture: FloatOf(result.Get(CaptureResult.LensAperture!)),
                    FocalLengthMm: FloatOf(result.Get(CaptureResult.LensFocalLength!)),
                    FocusDiopters: FloatOf(result.Get(CaptureResult.LensFocusDistance!)),
                    Kelvin: 0, // camera2 reports gains, not a temperature; the dial is the truth
                    // CONVERGED(2) / FLASH_REQUIRED(4) / LOCKED(3) all mean "settled".
                    AeConverged: aeState is 2 or 3 or 4,
                    // FOCUSED_LOCKED(4) / NOT_FOCUSED_LOCKED(5) — the lens has stopped hunting.
                    AfConverged: afState is 4 or 5
                );
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>On the handler thread: latest image → RGBA (rotated) → mailbox.</summary>
        private void OnFrame(ImageReader reader)
        {
            var image = reader.AcquireLatestImage();
            if (image is null) return;

            try
            {
                int w = image.Width, h = image.Height;
                bool swap = _rotation is 90 or 270;
                (int outW, int outH) = swap ? (h, w) : (w, h);

                byte[] rgba = _frames.Rent(outW * outH * 4);
                ConvertYuvToRgba(image: image, rotation: _rotation, rgba: rgba);

                _frames.Publish(buffer: rgba, width: outW, height: outH);
            }
            finally
            {
                // Close(), not Dispose(). Disposing the binding releases the JNI wrapper; only
                // Image.close() hands the buffer back to the ImageReader. Get this wrong and the
                // preview runs for exactly maxImages frames and then dies with "maxImages has
                // already been acquired" — which no desktop build can ever show you.
                image.Close();
                image.Dispose();
            }
        }

        /// <summary>
        ///     Full-range BT.601 YUV_420_888 → RGBA, reading the planes through their direct
        ///     buffers (camera2 guarantees direct) and writing rotated in the same pass — one loop,
        ///     no intermediate copies.
        /// </summary>
        private static unsafe void ConvertYuvToRgba(Image image, int rotation, byte[] rgba)
        {
            var planes = image.GetPlanes()!;
            byte* y = (byte*)JNIEnv.GetDirectBufferAddress(planes[0].Buffer!.Handle);
            byte* u = (byte*)JNIEnv.GetDirectBufferAddress(planes[1].Buffer!.Handle);
            byte* v = (byte*)JNIEnv.GetDirectBufferAddress(planes[2].Buffer!.Handle);
            int yRow = planes[0].RowStride;
            int uvRow = planes[1].RowStride;
            int uvPix = planes[1].PixelStride;

            int w = image.Width, h = image.Height;
            bool swap = rotation is 90 or 270;
            int outW = swap ? h : w;

            fixed (byte* dstBase = rgba)
            {
                for (int row = 0; row < h; row++)
                {
                    byte* yp = y + (row * yRow);
                    byte* up = u + ((row >> 1) * uvRow);
                    byte* vp = v + ((row >> 1) * uvRow);

                    // Destination as start + stride per source column, so rotation costs nothing
                    // inside the pixel loop.
                    (long start, long step) = rotation switch {
                        90 => ((long)(outW - 1 - row), outW),
                        180 => (((long)(h - 1 - row) * outW) + (w - 1), -1L),
                        270 => (((long)(w - 1) * outW) + row, -outW),
                        _ => ((long)row * outW, 1L),
                    };
                    uint* dst = (uint*)dstBase + start;

                    for (int col = 0; col < w; col++)
                    {
                        int yv = yp[col];
                        int uvIndex = (col >> 1) * uvPix;
                        int d = up[uvIndex] - 128;
                        int e = vp[uvIndex] - 128;

                        int r = yv + ((91881 * e) >> 16);
                        int g = yv - ((22554 * d + 46802 * e) >> 16);
                        int b = yv + ((116130 * d) >> 16);
                        if ((uint)r > 255) r = r < 0 ? 0 : 255;
                        if ((uint)g > 255) g = g < 0 ? 0 : 255;
                        if ((uint)b > 255) b = b < 0 ? 0 : 255;

                        // Little-endian RGBA: A in the top byte.
                        *dst = 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r;
                        dst += step;
                    }
                }
            }
        }

        private sealed class DeviceCallback(Camera2Session owner) : CameraDevice.StateCallback
        {
            public override void OnOpened(CameraDevice camera) => owner.OnOpened(camera);

            public override void OnDisconnected(CameraDevice camera)
            {
                // Another app took the camera, or a higher-priority use did. Expected and
                // temporary — reporting it as a failure would put an error in front of a
                // photographer who only has to wait.
                camera.Close();
                owner.Interrupted("Another app is using the camera.");
            }

            public override void OnError(CameraDevice camera, CameraError error)
            {
                camera.Close();
                owner.Fail($"Camera error: {error}.");
            }
        }

        private sealed class SessionCallback(Camera2Session owner) : CameraCaptureSession.StateCallback
        {
            public override void OnConfigured(CameraCaptureSession session) => owner.OnConfigured(session);

            public override void OnConfigureFailed(CameraCaptureSession session) =>
                owner.Fail("Camera capture session configuration failed.");
        }

        /// <summary>
        ///     Carries every completed capture's result back to the session, which is the only
        ///     source of truth for what the sensor actually did — in Auto it is the only source
        ///     at all.
        /// </summary>
        private sealed class ResultListener(Camera2Session owner) : CameraCaptureSession.CaptureCallback
        {
            public override void OnCaptureCompleted(
                CameraCaptureSession session,
                CaptureRequest request,
                TotalCaptureResult result) => owner.OnResult(result);
        }

        /// <summary>Carries the still capture's result to the DNG writer that needs it.</summary>
        private sealed class RawCaptureListener(Camera2Session owner) : CameraCaptureSession.CaptureCallback
        {
            public override void OnCaptureCompleted(
                CameraCaptureSession session,
                CaptureRequest request,
                TotalCaptureResult result) => owner.OnRawResult(result);

            public override void OnCaptureFailed(
                CameraCaptureSession session,
                CaptureRequest request,
                CaptureFailure failure) =>
                owner.CompleteRaw(
                    result: null,
                    error: new InvalidOperationException($"RAW capture failed (reason {failure.Reason}).")
                );
        }

        private sealed class FrameListener(Camera2Session owner)
            : Java.Lang.Object, ImageReader.IOnImageAvailableListener
        {
            public void OnImageAvailable(ImageReader? reader)
            {
                if (reader is null) return;
                try
                {
                    owner.OnFrame(reader);
                }
                catch (Exception ex)
                {
                    owner.Fail(ex.Message);
                }
            }
        }
    }
}
