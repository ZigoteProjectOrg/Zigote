using Zigote.Core.Engine;

namespace WebView;

/// <summary>Desktop backend selection — by OS and windowing, since one desktop build serves all.</summary>
internal static class WebViewDriver
{
    public static IWebViewBackend? Create(WebViewController owner, NativeParent parent)
    {
        if (OperatingSystem.IsLinux())
        {
            // Native Wayland cannot embed a foreign child view, and doesn't need to: the page
            // renders offscreen into an engine texture there. X11/XWayland gets the true overlay.
            return parent.Kind == NativeParentKind.X11
                ? WebKitGtkBackend.TryCreate(owner)
                : WebKitOffscreenBackend.TryCreate(owner);
        }

        if (OperatingSystem.IsWindows()) return WebView2Backend.TryCreate(owner);
        // ponytail: macOS desktop backend (WKWebView over the NSWindow's contentView) not built
        // yet — needs a mac to compile against AppKit; the iOS folder carries the WKWebView shape.
        return null;
    }
}
