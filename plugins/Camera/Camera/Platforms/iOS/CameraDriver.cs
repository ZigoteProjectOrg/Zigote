using System.Runtime.InteropServices;
using AVFoundation;
using CoreFoundation;
using CoreGraphics;
using CoreMedia;
using CoreVideo;
using UIKit;

namespace Camera;

/// <summary>
///     iOS capture (<c>net10.0-ios</c>): an <see cref="AVCaptureSession" /> delivering 32BGRA
///     into a sample-buffer delegate on its own dispatch queue, where one swizzle loop turns the
///     pixel buffer into RGBA for the mailbox. The connection is pinned to portrait so frames
///     arrive display-oriented — no rotation pass of our own.
/// </summary>
internal static class CameraDriver
{
    public static Task<bool> RequestPermissionAsync() =>
        AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video) == AVAuthorizationStatus.Authorized
            ? Task.FromResult(true)
            : AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video);

    public static void OnAppLifecycle(bool paused)
    {
    }

    /// <summary>
    ///     Every supported iPhone has at least a mid-range budget; the split is between the 3–4 GB
    ///     devices still on the supported list and everything since.
    /// </summary>
    public static DeviceTier DeviceTier()
    {
        try
        {
            ulong gigabytes = Foundation.NSProcessInfo.ProcessInfo.PhysicalMemory / (1024UL * 1024UL * 1024UL);
            return gigabytes >= 6 ? Camera.DeviceTier.High
                : gigabytes >= 4 ? Camera.DeviceTier.Mid
                : Camera.DeviceTier.Low;
        }
        catch (Exception)
        {
            return Camera.DeviceTier.Mid;
        }
    }

    public static ThermalState Thermal()
    {
        try
        {
            return Foundation.NSProcessInfo.ProcessInfo.ThermalState switch {
                Foundation.NSProcessInfoThermalState.Critical => ThermalState.Critical,
                Foundation.NSProcessInfoThermalState.Serious => ThermalState.Hot,
                Foundation.NSProcessInfoThermalState.Fair => ThermalState.Warm,
                _ => ThermalState.Nominal,
            };
        }
        catch (Exception)
        {
            return ThermalState.Nominal;
        }
    }

    /// <summary>
    ///     Add to the user's photo library through PhotoKit. Requires
    ///     <c>NSPhotoLibraryAddUsageDescription</c>, which the head declares.
    /// </summary>
    public static Task<string> PublishPhotoAsync(byte[] bytes, string fileName, string album)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        Photos.PHPhotoLibrary.RequestAuthorization(Photos.PHAccessLevel.AddOnly, status => {
            if (status != Photos.PHAuthorizationStatus.Authorized)
            {
                tcs.TrySetException(new InvalidOperationException("No permission to add to the photo library."));
                return;
            }

            Photos.PHPhotoLibrary.SharedPhotoLibrary.PerformChanges(
                () => {
                    var options = new Photos.PHAssetResourceCreationOptions {
                        OriginalFilename = fileName,
                    };
                    var request = Photos.PHAssetCreationRequest.CreationRequestForAsset();
                    request.AddResource(
                        type: Photos.PHAssetResourceType.Photo,
                        data: Foundation.NSData.FromArray(bytes),
                        options: options
                    );
                },
                (ok, error) => {
                    if (ok) tcs.TrySetResult(fileName);
                    else tcs.TrySetException(new InvalidOperationException(
                        error?.LocalizedDescription ?? "The photo library rejected the change."
                    ));
                }
            );
        });

        return tcs.Task;
    }

    public static Task<CameraDeviceInfo[]> GetDevicesAsync()
    {
        using var discovery = AVCaptureDeviceDiscoverySession.Create(
            deviceTypes: [AVCaptureDeviceType.BuiltInWideAngleCamera],
            mediaType: AVMediaTypes.Video,
            position: AVCaptureDevicePosition.Unspecified
        );
        var devices = discovery.Devices.Select(d => new CameraDeviceInfo(
                Id: d.UniqueID,
                Name: d.LocalizedName,
                Facing: d.Position switch {
                    AVCaptureDevicePosition.Front => CameraFacing.Front,
                    AVCaptureDevicePosition.Back => CameraFacing.Back,
                    _ => CameraFacing.External,
                }
            )
        ).ToArray();
        return Task.FromResult(devices);
    }

    public static ICameraSession Open(
        string? deviceId,
        int maxHeight,
        bool minimalProcessing,
        FrameMailbox frames,
        Action<string> onError,
        Action<string>? onInterrupted = null) =>
        new AvSession(
            deviceId: deviceId,
            maxHeight: maxHeight,
            minimalProcessing: minimalProcessing,
            frames: frames,
            onError: onError,
            onInterrupted: onInterrupted
        );

    /// <summary>
    ///     ImageIO writes JPEG and PNG on every supported iOS. JPEG XL encoding is not exposed by
    ///     the system, so it is reported unavailable rather than silently substituted.
    /// </summary>
    public static bool SupportsFormat(StillFormat format) =>
        format is StillFormat.Jpeg or StillFormat.Png;

    public static Task<byte[]> EncodeAsync(
        byte[] rgba,
        int width,
        int height,
        StillFormat format,
        int quality) =>
        format == StillFormat.Png
            ? Task.Run(() =>
                {
                    using var image = ImageFrom(rgba: rgba, width: width, height: height);
                    using var png = image.AsPNG()
                                    ?? throw new InvalidOperationException("PNG encode failed.");
                    return png.ToArray();
                }
            )
            : EncodeJpegAsync(rgba: rgba, width: width, height: height, quality: quality);

    /// <summary>The RGBA buffer as a UIImage, shared by every encoder here.</summary>
    private static UIImage ImageFrom(byte[] rgba, int width, int height)
    {
        using var provider = new CGDataProvider(rgba);
        using var colorSpace = CGColorSpace.CreateDeviceRGB();
        using var cgImage = new CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            colorSpace: colorSpace,
            bitmapFlags: CGBitmapFlags.ByteOrderDefault | CGBitmapFlags.Last,
            provider: provider,
            decode: null,
            shouldInterpolate: false,
            intent: CGColorRenderingIntent.Default
        );
        return new UIImage(cgImage);
    }

    public static Task<byte[]> EncodeJpegAsync(byte[] rgba, int width, int height, int quality) =>
        Task.Run(() =>
            {
                using var provider = new CGDataProvider(rgba);
                using var colorSpace = CGColorSpace.CreateDeviceRGB();
                using var cgImage = new CGImage(
                    width: width,
                    height: height,
                    bitsPerComponent: 8,
                    bitsPerPixel: 32,
                    bytesPerRow: width * 4,
                    colorSpace: colorSpace,
                    bitmapFlags: CGBitmapFlags.ByteOrderDefault | CGBitmapFlags.Last,
                    provider: provider,
                    decode: null,
                    shouldInterpolate: false,
                    intent: CGColorRenderingIntent.Default
                );
                using var image = new UIImage(cgImage);
                using var jpeg = image.AsJPEG(quality / 100f)
                                 ?? throw new InvalidOperationException("JPEG encode failed.");
                return jpeg.ToArray();
            }
        );

    private sealed class AvSession : ICameraSession
    {
        private readonly AVCaptureSession _session;
        private readonly AVCaptureDevice _device;
        private readonly FrameDelegate _delegate;
        private readonly DispatchQueue _queue;
        private readonly Action<string>? _onInterrupted;
        private readonly AVCapturePhotoOutput? _photoOutput;
        private Foundation.NSObject? _interruptionObserver;
        private bool _disposed;

        public CameraCapabilities Capabilities { get; }

        /// <summary>
        ///     AVFoundation exposes the live values as KVO properties on the device rather than
        ///     per-frame metadata, so the readout is a device read at tick time — no observers to
        ///     register, and no chance of a stale record from a frame that already went by.
        /// </summary>
        public CaptureMetadata? Metadata
        {
            get
            {
                if (_disposed) return null;
                try
                {
                    // ExposureDuration is a CMTime; nanoseconds keep it exact across platforms.
                    var duration = _device.ExposureDuration;
                    long ns = duration.TimeScale > 0
                        ? (long)(duration.Value * 1_000_000_000.0 / duration.TimeScale)
                        : 0L;

                    return new CaptureMetadata(
                        Iso: (int)Math.Round(_device.ISO),
                        ShutterNs: ns,
                        Aperture: _device.LensAperture,
                        FocalLengthMm: 0f, // not exposed; the DNG/EXIF path is where it comes from
                        // lensPosition is a normalized 0…1, not diopters: scale by the closest
                        // focus so both platforms speak the same unit to the app above.
                        FocusDiopters: Capabilities.ManualFocus
                            ? _device.LensPosition * Capabilities.MinFocusDiopters
                            : float.NaN,
                        Kelvin: KelvinOf(_device),
                        AeConverged: !_device.AdjustingExposure,
                        AfConverged: !_device.AdjustingFocus
                    );
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        ///     Apply every control in one <c>lockForConfiguration</c>. AVFoundation demands the
        ///     lock for any of these and throws if a value is out of the device's range, so each
        ///     step is clamped by the capabilities probed at open.
        /// </summary>
        public void Apply(in ControlState controls)
        {
            if (_disposed) return;

            // Copy out of the by-ref struct: the work runs on another thread, and locking the
            // device blocks — neither belongs on the app thread.
            ControlState c = controls;
            Task.Run(() =>
                {
                    try
                    {
                        if (_disposed || !_device.LockForConfiguration(out var error) || error is not null) return;
                        try
                        {
                            ApplyExposure(c);
                            ApplyWhiteBalance(c);
                            ApplyFocus(c);
                        }
                        finally
                        {
                            _device.UnlockForConfiguration();
                        }
                    }
                    catch (Exception)
                    {
                        // A control the device refused must not kill the preview; Metadata
                        // reports what it actually settled on.
                    }
                }
            );
        }

        private void ApplyExposure(in ControlState c)
        {
            bool iso = c.IsoManual && Capabilities.Iso.Supported;
            bool shutter = c.ShutterManual && Capabilities.Shutter.Supported;

            if (iso || shutter)
            {
                // Custom mode takes both at once. AVCaptureDevice.ISOCurrent and
                // ExposureDurationCurrent are the "leave this half to the device" sentinels,
                // which is exactly how a priority mode is expressed here.
                float isoValue = iso ? Capabilities.Iso.Clamp(c.Iso) : AVCaptureDevice.ISOCurrent;
                var duration = shutter
                    ? CMTime.FromSeconds(
                        seconds: Capabilities.Shutter.Clamp(c.ShutterNs) / 1_000_000_000.0,
                        preferredTimeScale: 1_000_000_000
                    )
                    : AVCaptureDevice.ExposureDurationCurrent;

                if (_device.IsExposureModeSupported(AVCaptureExposureMode.Custom))
                    _device.LockExposure(duration: duration, iso: isoValue, completionHandler: null);
                return;
            }

            if (_device.IsExposureModeSupported(AVCaptureExposureMode.ContinuousAutoExposure))
                _device.ExposureMode = c.AeLock && _device.IsExposureModeSupported(AVCaptureExposureMode.Locked)
                    ? AVCaptureExposureMode.Locked
                    : AVCaptureExposureMode.ContinuousAutoExposure;

            (float minEv, float maxEv) = Capabilities.EvStops;
            if (maxEv > minEv)
                _device.SetExposureTargetBias(
                    bias: Math.Clamp(value: c.EvCompensation, min: minEv, max: maxEv),
                    handler: null
                );

            if (c.AeRegion is { } ae && _device.ExposurePointOfInterestSupported)
                _device.ExposurePointOfInterest = new CGPoint(
                    x: Math.Clamp(value: ae.X + (ae.Width / 2f), min: 0f, max: 1f),
                    y: Math.Clamp(value: ae.Y + (ae.Height / 2f), min: 0f, max: 1f)
                );
        }

        private void ApplyWhiteBalance(in ControlState c)
        {
            if (!c.WhiteBalanceManual || Capabilities.Kelvin.Max <= 0)
            {
                if (_device.IsWhiteBalanceModeSupported(AVCaptureWhiteBalanceMode.ContinuousAutoWhiteBalance))
                    _device.WhiteBalanceMode = c.AwbLock
                                               && _device.IsWhiteBalanceModeSupported(AVCaptureWhiteBalanceMode.Locked)
                        ? AVCaptureWhiteBalanceMode.Locked
                        : AVCaptureWhiteBalanceMode.ContinuousAutoWhiteBalance;
                return;
            }

            if (!_device.IsWhiteBalanceModeSupported(AVCaptureWhiteBalanceMode.Locked)) return;

            // AVFoundation has a real temperature/tint API, so it is used rather than the shared
            // Kelvin→gains table — the device's own conversion is better calibrated than ours.
            // Its tint is in the ±150 range where ours is ±1.
            var temperature = new AVCaptureWhiteBalanceTemperatureAndTintValues(
                temperature: Math.Clamp(
                    value: c.WhiteBalanceKelvin,
                    min: Capabilities.Kelvin.Min,
                    max: Capabilities.Kelvin.Max
                ),
                tint: Math.Clamp(value: c.WhiteBalanceTint, min: -1f, max: 1f) * 150f
            );

            var gains = _device.GetDeviceWhiteBalanceGains(temperature);
            _device.SetWhiteBalanceModeLockedWithDeviceWhiteBalanceGains(
                gains: Clamp(gains: gains, device: _device),
                completionHandler: null
            );
        }

        private void ApplyFocus(in ControlState c)
        {
            if (c.FocusManual && Capabilities.ManualFocus
                              && _device.IsFocusModeSupported(AVCaptureFocusMode.Locked))
            {
                // Back to the normalized 0…1 the device wants, from the diopters the app speaks.
                float position = Capabilities.MinFocusDiopters > 0f
                    ? Math.Clamp(value: c.FocusDiopters / Capabilities.MinFocusDiopters, min: 0f, max: 1f)
                    : 0f;
                _device.SetFocusModeLocked(lensPosition: position, completionHandler: null);
                return;
            }

            if (c.AfLock && _device.IsFocusModeSupported(AVCaptureFocusMode.Locked))
            {
                _device.FocusMode = AVCaptureFocusMode.Locked;
                return;
            }

            if (_device.IsFocusModeSupported(AVCaptureFocusMode.ContinuousAutoFocus))
                _device.FocusMode = AVCaptureFocusMode.ContinuousAutoFocus;

            if ((c.AfRegion ?? c.AeRegion) is { } af && _device.FocusPointOfInterestSupported)
                _device.FocusPointOfInterest = new CGPoint(
                    x: Math.Clamp(value: af.X + (af.Width / 2f), min: 0f, max: 1f),
                    y: Math.Clamp(value: af.Y + (af.Height / 2f), min: 0f, max: 1f)
                );
        }

        /// <summary>
        ///     Gains outside the device's supported range make the setter throw, and the maximum
        ///     differs per device — so clamp rather than trusting the conversion.
        /// </summary>
        private static AVCaptureWhiteBalanceGains Clamp(AVCaptureWhiteBalanceGains gains, AVCaptureDevice device)
        {
            float max = device.MaxWhiteBalanceGain;
            return new AVCaptureWhiteBalanceGains {
                RedGain = Math.Clamp(value: gains.RedGain, min: 1f, max: max),
                GreenGain = Math.Clamp(value: gains.GreenGain, min: 1f, max: max),
                BlueGain = Math.Clamp(value: gains.BlueGain, min: 1f, max: max),
            };
        }

        private static int KelvinOf(AVCaptureDevice device)
        {
            try
            {
                var values = device.GetTemperatureAndTintValues(device.DeviceWhiteBalanceGains);
                return (int)Math.Round(values.Temperature);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        ///     What this device can do, read once at open. iOS is uniform enough that most of it
        ///     is simply "yes", but focus range and exposure bounds are genuinely per-device and
        ///     the ultra-wide differs from the main lens on the same phone.
        /// </summary>
        private static CameraCapabilities Probe(AVCaptureDevice device)
        {
            var format = device.ActiveFormat;
            bool custom = device.IsExposureModeSupported(AVCaptureExposureMode.Custom);

            var iso = custom
                ? new IsoRange(Min: (int)format.MinISO, Max: (int)format.MaxISO)
                : default;

            var shutter = default(ShutterRange);
            if (custom)
            {
                var min = format.MinExposureDuration;
                var max = format.MaxExposureDuration;
                shutter = new ShutterRange(
                    MinNs: (long)(min.Seconds * 1_000_000_000.0),
                    MaxNs: (long)(max.Seconds * 1_000_000_000.0)
                );
            }

            bool manualFocus = device.FocusPointOfInterestSupported
                               || device.IsFocusModeSupported(AVCaptureFocusMode.Locked);

            return new CameraCapabilities(
                Iso: iso,
                Shutter: shutter,
                // The bias API is continuous in stops, so the "step" is the dial's resolution.
                EvStep: 1f / 3f,
                EvRange: (
                    (int)Math.Round(device.MinExposureTargetBias * 3f),
                    (int)Math.Round(device.MaxExposureTargetBias * 3f)
                ),
                Kelvin: device.IsWhiteBalanceModeSupported(AVCaptureWhiteBalanceMode.Locked)
                    ? (WhiteBalance.MinKelvin, WhiteBalance.MaxKelvin)
                    : (0, 0),
                Tint: device.IsWhiteBalanceModeSupported(AVCaptureWhiteBalanceMode.Locked),
                // iOS gives a normalized lens position, not a distance. Report the standard
                // 10-diopter (10 cm) close focus so the app's dial has a real unit to show.
                MinFocusDiopters: manualFocus ? 10f : 0f,
                ManualFocus: manualFocus,
                // Photo stabilization is the system's business on iOS and cannot be switched off.
                OisToggle: false,
                Regions: device.FocusPointOfInterestSupported || device.ExposurePointOfInterestSupported,
                // Claimed from what the device reports, not from what the model name suggests:
                // the ultra-wide on the same phone frequently offers no raw at all.
                Raw: device.IsExposureModeSupported(AVCaptureExposureMode.Custom)
            );
        }

        public AvSession(
            string? deviceId,
            int maxHeight,
            bool minimalProcessing,
            FrameMailbox frames,
            Action<string> onError,
            Action<string>? onInterrupted = null)
        {
            var device = (deviceId is null
                             ? AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video)
                             : AVCaptureDevice.DeviceWithUniqueID(deviceId))
                         ?? throw new InvalidOperationException("No camera found.");

            var input = AVCaptureDeviceInput.FromDevice(device: device, error: out var inputError)
                        ?? throw new InvalidOperationException(
                            inputError?.LocalizedDescription ?? "Cannot open the camera."
                        );

            _device = device;
            Capabilities = Probe(device);
            _onInterrupted = onInterrupted;

            _session = new AVCaptureSession {
                // The nearest standard preset at or under the cap; 0 (native) takes the largest.
                SessionPreset = maxHeight switch {
                    > 0 and <= 480 => AVCaptureSession.Preset640x480,
                    > 480 and <= 720 => AVCaptureSession.Preset1280x720,
                    _ => AVCaptureSession.Preset1920x1080,
                },
            };
            _session.AddInput(input);

            var output = new AVCaptureVideoDataOutput {
                // Late frames are dropped at the source: a preview never wants a backlog.
                AlwaysDiscardsLateVideoFrames = true,
                WeakVideoSettings = new CVPixelBufferAttributes {
                    PixelFormatType = CVPixelFormatType.CV32BGRA,
                }.Dictionary,
            };
            _queue = new DispatchQueue("zigote-camera");
            _delegate = new FrameDelegate(frames: frames, onError: onError);
            output.SetSampleBufferDelegate(sampleBufferDelegate: _delegate, sampleBufferCallbackQueue: _queue);
            _session.AddOutput(output);

            // A separate output for stills: the preview stream is a downscaled video feed, and a
            // raw file has to come off the sensor at full size through the photo path.
            if (Capabilities.Raw)
            {
                var photo = new AVCapturePhotoOutput();
                if (_session.CanAddOutput(photo))
                {
                    _session.AddOutput(photo);
                    _photoOutput = photo;
                }
                else
                {
                    photo.Dispose();
                }
            }

            var connection = output.ConnectionFromMediaType(AVMediaTypes.Video.GetConstant()!);
            if (connection is not null)
            {
                if (connection.SupportsVideoOrientation)
                    connection.VideoOrientation = AVCaptureVideoOrientation.Portrait;
                // Front camera unmirrored, like the other platforms — mirroring is a view concern.
                if (minimalProcessing && connection.SupportsVideoStabilization)
                    connection.PreferredVideoStabilizationMode = AVCaptureVideoStabilizationMode.Off;
            }

            // A call, an alarm, Split View, or another app claiming the camera. Expected and
            // temporary, so it is reported as an interruption rather than an error.
            _interruptionObserver = Foundation.NSNotificationCenter.DefaultCenter.AddObserver(
                aName: AVCaptureSession.WasInterruptedNotification,
                notify: n => _onInterrupted?.Invoke(Describe(n)),
                objectToObserve: _session
            );

            // StartRunning blocks while the pipeline spins up — that wait belongs off the app
            // thread, like every other driver's open path.
            Task.Run(() =>
                {
                    try
                    {
                        _session.StartRunning();
                    }
                    catch (Exception ex)
                    {
                        onError(ex.Message);
                    }
                }
            );
        }

        /// <summary>
        ///     A DNG off the sensor. ProRAW where the device offers it (Apple's own multi-frame
        ///     result, still a DNG), Bayer raw otherwise — both come back from AVFoundation as a
        ///     complete file, so nothing here has to understand the sensor's layout.
        /// </summary>
        public Task<RawPhoto> CaptureRawAsync()
        {
            if (_disposed || _photoOutput is null)
                return Task.FromException<RawPhoto>(
                    new InvalidOperationException("The camera has no RAW path running.")
                );

            uint[] rawFormats = _photoOutput.AvailableRawPhotoPixelFormatTypes ?? [];
            if (rawFormats.Length == 0)
                return Task.FromException<RawPhoto>(
                    new NotSupportedException("This camera cannot capture RAW.")
                );

            var tcs = new TaskCompletionSource<RawPhoto>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var settings = AVCapturePhotoSettings.FromRawPixelFormatType(rawFormats[0]);
                // The delegate must outlive the call: AVFoundation holds it weakly, so it is
                // rooted here until the capture completes.
                var handler = new PhotoDelegate(tcs, Capabilities);
                _photoOutput.CapturePhoto(settings: settings, cb: handler);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        /// <summary>
        ///     Holds itself alive until the photo lands. AVFoundation keeps only a weak reference
        ///     to a capture delegate, so a local would be collected mid-capture and the callback
        ///     would never arrive.
        /// </summary>
        private sealed class PhotoDelegate : AVCapturePhotoCaptureDelegate
        {
            private readonly TaskCompletionSource<RawPhoto> _tcs;
            private readonly CameraCapabilities _capabilities;
            private GCHandle _root;

            public PhotoDelegate(TaskCompletionSource<RawPhoto> tcs, CameraCapabilities capabilities)
            {
                _tcs = tcs;
                _capabilities = capabilities;
                _root = GCHandle.Alloc(this);
            }

            public override void DidFinishProcessingPhoto(
                AVCapturePhotoOutput output,
                AVCapturePhoto photo,
                Foundation.NSError? error)
            {
                try
                {
                    if (error is not null)
                    {
                        _tcs.TrySetException(new InvalidOperationException(error.LocalizedDescription));
                        return;
                    }

                    // fileDataRepresentation is already a complete DNG for a raw capture.
                    byte[]? dng = photo.FileDataRepresentation?.ToArray();
                    if (dng is null || dng.Length == 0)
                    {
                        _tcs.TrySetException(
                            new InvalidOperationException("The RAW capture produced no file.")
                        );
                        return;
                    }

                    _tcs.TrySetResult(new RawPhoto(Dng: dng, Metadata: MetadataOf(photo)));
                }
                finally
                {
                    if (_root.IsAllocated) _root.Free();
                }
            }

            /// <summary>What the sensor did, read off the photo's own EXIF rather than the device.</summary>
            private CaptureMetadata? MetadataOf(AVCapturePhoto photo)
            {
                try
                {
                    var exif = photo.Metadata?[CoreGraphics.CGImageProperties.ExifDictionary]
                        as Foundation.NSDictionary;

                    float Read(string key) =>
                        exif?[key] is Foundation.NSNumber n ? n.FloatValue : 0f;

                    float seconds = Read("ExposureTime");
                    return new CaptureMetadata(
                        Iso: (int)Read("ISOSpeedRatings"),
                        ShutterNs: (long)(seconds * 1_000_000_000.0),
                        Aperture: Read("FNumber"),
                        FocalLengthMm: Read("FocalLength"),
                        FocusDiopters: float.NaN,
                        Kelvin: 0,
                        AeConverged: true,
                        AfConverged: true
                    );
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>The system's own reason, mapped to something a photographer can act on.</summary>
        private static string Describe(Foundation.NSNotification notification)
        {
            var reason = notification.UserInfo?[AVCaptureSession.InterruptionReasonKey] as Foundation.NSNumber;
            return (AVCaptureSessionInterruptionReason?)reason?.Int32Value switch {
                AVCaptureSessionInterruptionReason.VideoDeviceInUseByAnotherClient =>
                    "Another app is using the camera.",
                AVCaptureSessionInterruptionReason.VideoDeviceNotAvailableWithMultipleForegroundApps =>
                    "The camera is unavailable in split view.",
                AVCaptureSessionInterruptionReason.VideoDeviceNotAvailableInBackground =>
                    "The camera is unavailable in the background.",
                AVCaptureSessionInterruptionReason.VideoDeviceNotAvailableDueToSystemPressure =>
                    "The camera paused — the device is too hot.",
                _ => "The camera was interrupted.",
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_interruptionObserver is { } observer)
            {
                Foundation.NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
                _interruptionObserver = null;
            }

            _delegate.Stopped = true;
            _photoOutput?.Dispose();
            Task.Run(() =>
                {
                    try
                    {
                        _session.StopRunning();
                    }
                    catch (Exception)
                    {
                        // Already torn down.
                    }

                    _session.Dispose();
                }
            );
        }

        private sealed class FrameDelegate(FrameMailbox frames, Action<string> onError)
            : AVCaptureVideoDataOutputSampleBufferDelegate
        {
            public volatile bool Stopped;
            private bool _failed;

            public override void DidOutputSampleBuffer(
                AVCaptureOutput captureOutput,
                CMSampleBuffer sampleBuffer,
                AVCaptureConnection connection)
            {
                // Sample buffers are a strictly limited pool — dispose promptly or capture stalls.
                using var _ = sampleBuffer;
                if (Stopped) return;

                try
                {
                    using var pixels = sampleBuffer.GetImageBuffer() as CVPixelBuffer;
                    if (pixels is null) return;
                    Convert(pixels);
                }
                catch (Exception ex)
                {
                    if (_failed) return;
                    _failed = true;
                    onError(ex.Message);
                }
            }

            private unsafe void Convert(CVPixelBuffer pixels)
            {
                pixels.Lock(CVPixelBufferLock.ReadOnly);
                try
                {
                    int w = (int)pixels.Width;
                    int h = (int)pixels.Height;
                    int stride = (int)pixels.BytesPerRow;
                    byte* src = (byte*)pixels.BaseAddress;
                    if (src is null || w <= 0 || h <= 0) return;

                    byte[] rgba = frames.Rent(w * h * 4);
                    fixed (byte* dstBase = rgba)
                    {
                        for (int row = 0; row < h; row++)
                        {
                            byte* s = src + (row * stride);
                            byte* d = dstBase + (row * w * 4);
                            for (int x = 0; x < w; x++, s += 4, d += 4)
                            {
                                d[0] = s[2];
                                d[1] = s[1];
                                d[2] = s[0];
                                d[3] = s[3];
                            }
                        }
                    }

                    frames.Publish(buffer: rgba, width: w, height: h);
                }
                finally
                {
                    pixels.Unlock(CVPixelBufferLock.ReadOnly);
                }
            }
        }
    }
}
