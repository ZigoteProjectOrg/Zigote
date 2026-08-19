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
        Action<string> onError) =>
        new AvSession(
            deviceId: deviceId,
            maxHeight: maxHeight,
            minimalProcessing: minimalProcessing,
            frames: frames,
            onError: onError
        );

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
        private readonly FrameDelegate _delegate;
        private readonly DispatchQueue _queue;
        private bool _disposed;

        public AvSession(
            string? deviceId,
            int maxHeight,
            bool minimalProcessing,
            FrameMailbox frames,
            Action<string> onError)
        {
            var device = (deviceId is null
                             ? AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video)
                             : AVCaptureDevice.DeviceWithUniqueID(deviceId))
                         ?? throw new InvalidOperationException("No camera found.");

            var input = AVCaptureDeviceInput.FromDevice(device: device, error: out var inputError)
                        ?? throw new InvalidOperationException(
                            inputError?.LocalizedDescription ?? "Cannot open the camera."
                        );

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

            var connection = output.ConnectionFromMediaType(AVMediaTypes.Video.GetConstant()!);
            if (connection is not null)
            {
                if (connection.SupportsVideoOrientation)
                    connection.VideoOrientation = AVCaptureVideoOrientation.Portrait;
                // Front camera unmirrored, like the other platforms — mirroring is a view concern.
                if (minimalProcessing && connection.SupportsVideoStabilization)
                    connection.PreferredVideoStabilizationMode = AVCaptureVideoStabilizationMode.Off;
            }

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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _delegate.Stopped = true;
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
