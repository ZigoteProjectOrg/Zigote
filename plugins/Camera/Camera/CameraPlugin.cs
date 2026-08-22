using Zigote.Core.Platform;
using Zigote.UI.Host;

namespace Camera;

/// <summary>
///     Camera — cross-platform camera capture for Zigote: preview into a GPU texture
///     (<see cref="CameraView" />), photo snapshots, device enumeration and permissions.
///     Desktop capture rides ffmpeg (v4l2 / avfoundation / dshow), Android is camera2, iOS is
///     AVFoundation — the csproj compiles exactly one <c>Platforms/CameraDriver</c> per target
///     framework, and the shared code binds to it statically.
///     <para>
///         Register once, in each head, before the App:
///         <c>PluginHost.Register(new CameraPlugin());</c>. Registration is what wires app
///         lifecycle through to running sessions — backgrounding releases the camera, resuming
///         reopens it — and, on Android, what lets the permission prompt resolve.
///     </para>
/// </summary>
public sealed class CameraPlugin : IPlatformPlugin, IAppLifecycleObserver
{
    private static event Action<bool>? LifecycleChanged;

    public string Name => "camera";

    public void Start()
    {
    }

    public void Stop()
    {
    }

    /// <summary>
    ///     Whether the app may use the camera, prompting the user if the platform requires it and
    ///     has not asked before. Call from a user gesture, before
    ///     <see cref="CameraController.StartAsync" />. Desktop answers true without prompting.
    /// </summary>
    /// <remarks>
    ///     The head must declare the platform permission: <c>android.permission.CAMERA</c> in the
    ///     Android manifest, <c>NSCameraUsageDescription</c> in the iOS Info.plist.
    /// </remarks>
    public static Task<bool> RequestPermissionAsync() => CameraDriver.RequestPermissionAsync();

    /// <summary>The cameras this device has, in the platform's order. Empty when there are none.</summary>
    public static Task<CameraDeviceInfo[]> GetDevicesAsync() => CameraDriver.GetDevicesAsync();

    /// <summary>
    ///     Encode raw RGBA as a JPEG using the platform's own encoder — the same one
    ///     <see cref="CameraController.TakePhotoAsync" /> uses, so a photo an app renders itself
    ///     (a re-develop, a composite, a thumbnail) comes out of the same code path as a capture.
    ///     Runs off the calling thread.
    /// </summary>
    /// <param name="rgba">Tightly packed, <paramref name="width" /> × <paramref name="height" /> × 4.</param>
    /// <param name="quality">1–100.</param>
    /// <summary>
    ///     Publish a finished photo to the platform's own photo library, so it appears in the
    ///     system gallery alongside every other camera's output. Returns a display path.
    ///     <para>
    ///         On Android this is a MediaStore insert under <c>Pictures/<paramref name="album" /></c>,
    ///         written while <c>IS_PENDING</c> so a half-written file is never visible; on desktop
    ///         it is a plain write into the pictures folder. An app keeps its own write-ahead copy
    ///         either way — this is the publish step, not the durability step.
    ///     </para>
    /// </summary>
    /// <param name="bytes">The complete encoded image.</param>
    /// <param name="fileName">Including extension, e.g. <c>APT_20260821_120000.jpg</c>.</param>
    /// <param name="album">Sub-folder of the pictures directory.</param>
    public static Task<string> PublishPhotoAsync(byte[] bytes, string fileName, string album = "Aperture")
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return CameraDriver.PublishPhotoAsync(bytes: bytes, fileName: fileName, album: album);
    }

    /// <summary>Whether this device can write <paramref name="format" /> at all.</summary>
    public static bool SupportsFormat(StillFormat format) => CameraDriver.SupportsFormat(format);

    /// <summary>
    ///     Encode raw RGBA in the requested format, using whatever this platform has. Falls back
    ///     to JPEG when the format is unavailable rather than throwing: a photographer pressing
    ///     the shutter should get a photograph, and the format is a preference, not a contract.
    ///     Ask <see cref="SupportsFormat" /> first if the difference matters.
    /// </summary>
    public static Task<byte[]> EncodeAsync(
        byte[] rgba,
        int width,
        int height,
        StillFormat format,
        int quality = 90)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var chosen = SupportsFormat(format) ? format : StillFormat.Jpeg;
        return CameraDriver.EncodeAsync(
            rgba: rgba,
            width: width,
            height: height,
            format: chosen,
            quality: Math.Clamp(value: quality, min: 1, max: 100)
        );
    }

    /// <summary>The file extension <paramref name="format" /> is written with.</summary>
    public static string ExtensionFor(StillFormat format) => format switch {
        StillFormat.Png => ".png",
        StillFormat.JpegXl => ".jxl",
        _ => ".jpg",
    };

    public static Task<byte[]> EncodeJpegAsync(byte[] rgba, int width, int height, int quality = 90)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length < (long)width * height * 4)
            throw new ArgumentException(
                message: $"Need {(long)width * height * 4} bytes for {width}×{height}, got {rgba.Length}.",
                paramName: nameof(rgba)
            );

        return CameraDriver.EncodeJpegAsync(
            rgba: rgba,
            width: width,
            height: height,
            quality: Math.Clamp(value: quality, min: 1, max: 100)
        );
    }

    void IAppLifecycleObserver.OnLifecycleChanged(AppLifecycleState state)
    {
        // Inactive (focus lost, sheet over the app) keeps the camera; only a real background
        // releases it.
        if (state == AppLifecycleState.Inactive) return;
        bool paused = state == AppLifecycleState.Paused;
        CameraDriver.OnAppLifecycle(paused);
        LifecycleChanged?.Invoke(paused);
    }

    /// <summary>Controllers subscribe here; the plugin instance is what the App delivers to.</summary>
    internal static IDisposable OnLifecycle(Action<bool> onChanged)
    {
        LifecycleChanged += onChanged;
        return new Unsubscribe(onChanged);
    }

    private sealed class Unsubscribe(Action<bool> handler) : IDisposable
    {
        public void Dispose() => LifecycleChanged -= handler;
    }
}
