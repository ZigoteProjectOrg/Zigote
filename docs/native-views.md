# Native views

How a platform-owned view — a webview, a map, an ad, anything the OS draws — appears inside a
Zigote layout. Three layers, each small:

1. **Engine**: `zigote_window_native_parent` — the one new export. It answers the window's
   platform parent handle (HWND / NSWindow / X11 Display+Window / wl_display+wl_surface /
   ANativeWindow / UIWindow) so a host can parent a child view into it. Reached from C# as
   `ZigoteEngine.GetNativeParent()`.
2. **Zigote.UI**: `NativeViewHost` — a widget that reserves layout space and pushes its
   window-space rectangle (plus display scale) into an `INativeView` every time layout moves it:
   a resize, a scroll, a tab switch. Unmounting hides the view; the view's lifetime belongs to
   whoever created it.
3. **A plugin** implements `INativeView` per platform and owns the actual child view. The
   WebView plugin (`plugins/WebView`) is the first consumer and the reference.

## The compositing model — overlay, not texture

A native view is a real OS child view positioned over the engine's surface. That buys real
platform behavior (the OS browser, its updates, its accessibility) at a fixed cost: **the native
view always draws on top of engine content.** Nothing Zigote paints — popups, sheets, toasts —
can appear over it. Design layouts so overlays sit beside native views, or hide the view while a
dialog is up (`NativeViewHost` hides it automatically when the host leaves the tree).

A future texture-mode backend (offscreen render → wgpu texture, full compositing) can slot in
behind the same `INativeView` seam; nothing in the API precludes it.

## Per-platform attachment

| Platform | Parent | Child positioning |
|---|---|---|
| Windows | HWND | child HWND, physical px |
| macOS | NSWindow → contentView | NSView frame, points |
| Linux | X11 Window | XReparentWindow + XMoveResizeWindow, physical px |
| Android | SDLActivity's RelativeLayout (Java side) | LayoutParams margins, physical px |
| iOS | UIWindow | UIView frame, points |

**Linux, Wayland vs X11.** A Wayland client cannot embed a foreign toolkit's surface — there is
no XReparentWindow equivalent across clients. So on native Wayland the WebView plugin switches
model entirely: the page renders offscreen (GtkOffscreenWindow, WebKit's software path) into an
engine texture and composites like any widget — full z-order, no overlay hole — with input
synthesized back into GTK. The overlay path still exists under X11/XWayland
(`WebViewController.EnsureEmbeddableVideoDriver()` opts in). The texture seam
(`ITextureWebViewBackend`) is the slot any future offscreen backend (WPE, CEF) implements.

## Known ceilings

- No partial clipping: a native view scrolled half out of a viewport is a rectangle, not a
  clipped one. (X11 could clip via an intermediate window; add when an app needs it.)
- Linux keyboard focus is click-to-focus between the SDL window and the embedded window
  (see the button-press handler in the WebKitGTK backend).
- One window: hosts attach to the main window today; secondary-window support means passing the
  `NativeWindow` handle through `GetNativeParent(windowHandle)` — the export already takes it.
