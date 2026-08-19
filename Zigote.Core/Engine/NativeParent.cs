namespace Zigote.Core.Engine;

/// <summary>Which platform windowing handle <see cref="NativeParent" /> carries.</summary>
public enum NativeParentKind : uint
{
    /// <summary>No native handle available (headless, unsupported platform).</summary>
    None = 0,

    /// <summary>Windows: <see cref="NativeParent.Ptr1" /> is the HWND.</summary>
    Win32Hwnd = 1,

    /// <summary>macOS: <see cref="NativeParent.Ptr1" /> is the NSWindow*.</summary>
    MacNsWindow = 2,

    /// <summary>X11: <see cref="NativeParent.Ptr1" /> is the Display*, <see cref="NativeParent.Ptr2" /> the Window.</summary>
    X11 = 3,

    /// <summary>
    ///     Wayland: <see cref="NativeParent.Ptr1" /> is the wl_display*, <see cref="NativeParent.Ptr2" />
    ///     the wl_surface*. Foreign child views cannot be embedded here — run under
    ///     <c>SDL_VIDEO_DRIVER=x11</c> (XWayland) when a native view is needed.
    /// </summary>
    Wayland = 4,

    /// <summary>Android: <see cref="NativeParent.Ptr1" /> is the ANativeWindow*. View attachment goes
    ///     through SDLActivity.getLayout() on the Java side instead.</summary>
    AndroidWindow = 5,

    /// <summary>iOS: <see cref="NativeParent.Ptr1" /> is the UIWindow*.</summary>
    IosUiWindow = 6,
}

/// <summary>
///     A window's native parent handle, from <see cref="ZigoteEngine.GetNativeParent" /> — what a
///     platform child view (a webview, a native control) is parented into for overlay embedding.
/// </summary>
public readonly record struct NativeParent(NativeParentKind Kind, nint Ptr1, nint Ptr2);
