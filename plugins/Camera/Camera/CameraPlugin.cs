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
