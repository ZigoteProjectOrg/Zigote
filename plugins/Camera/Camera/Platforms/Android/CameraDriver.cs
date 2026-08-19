using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
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
        Action<string> onError) =>
        new Camera2Session(
            deviceId: deviceId,
            maxHeight: maxHeight,
            minimalProcessing: minimalProcessing,
            frames: frames,
            onError: onError
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
        private readonly HandlerThread _thread;
        private readonly Handler _handler;
        private readonly object _gate = new();

        private readonly bool _minimalProcessing;
        private CameraDevice? _device;
        private CameraCaptureSession? _session;
        private ImageReader? _reader;
        private int _rotation;
        private bool _disposed;

        public Camera2Session(
            string? deviceId,
            int maxHeight,
            bool minimalProcessing,
            FrameMailbox frames,
            Action<string> onError)
        {
            _frames = frames;
            _onError = onError;
            _minimalProcessing = minimalProcessing;
            _thread = new HandlerThread("zigote-camera");
            _thread.Start();
            _handler = new Handler(_thread.Looper!);

            var manager = Manager();
            string id = deviceId ?? DefaultDeviceId(manager)
                ?? throw new InvalidOperationException("No camera found.");

            var characteristics = manager.GetCameraCharacteristics(id);
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
                device.CreateCaptureSession(
                    outputs: [_reader!.Surface!],
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

            try
            {
                var request = _device!.CreateCaptureRequest(CameraTemplate.Preview);
                request.AddTarget(_reader!.Surface!);
                if (_minimalProcessing)
                {
                    // The DSLR-flat option: strip the vendor look as far as camera2 allows so the
                    // frames are the sensor pipeline's neutral output — the app's own GPU grade is
                    // the look. AE/AWB/AF stay on, like a DSLR in P mode; manual exposure is a
                    // bigger surface for when it is asked for.
                    request.Set(key: CaptureRequest.NoiseReductionMode!, value: (int)NoiseReductionMode.Off);
                    request.Set(key: CaptureRequest.EdgeMode!, value: (int)EdgeMode.Off);
                    request.Set(
                        key: CaptureRequest.ControlVideoStabilizationMode!,
                        value: (int)ControlVideoStabilizationMode.Off
                    );
                }

                session.SetRepeatingRequest(request: request.Build(), listener: null, handler: _handler);
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
        }

        /// <summary>On the handler thread: latest image → RGBA (rotated) → mailbox.</summary>
        private void OnFrame(ImageReader reader)
        {
            using var image = reader.AcquireLatestImage();
            if (image is null) return;

            int w = image.Width, h = image.Height;
            bool swap = _rotation is 90 or 270;
            (int outW, int outH) = swap ? (h, w) : (w, h);

            byte[] rgba = _frames.Rent(outW * outH * 4);
            ConvertYuvToRgba(image: image, rotation: _rotation, rgba: rgba);
            _frames.Publish(buffer: rgba, width: outW, height: outH);
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
                camera.Close();
                owner.Fail("Camera disconnected.");
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
