# WebView

Embedded web content for Zigote — the `webview_flutter` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md), built on the engine's
[native view layer](../../docs/native-views.md). Built for **JS web extensions**: a map, a
checkout, a chart library that only exists as a web widget, talking to its host over a typed
message bridge.

```csharp
var controller = new WebViewController(new WebViewSettings { UserAgent = "MyApp/1.0" });

controller.AddUserScript("window.MAPS_KEY = 'pk_live_…';");   // runs before the page's scripts
controller.NavigationFilter = url => url.StartsWith("https://checkout.example.com");
controller.ReceiveJson<PaymentResult>(r => Complete(r));      // page → host, typed
controller.Navigate("https://checkout.example.com/pay");      // queued until the widget mounts

await controller.PostMessageAsync(new { kind = "cart", total = 42.00m });  // host → page

new WebView(controller)   // in the widget tree
```

The page side is the same on every platform:

```js
window.zigote.postMessage({ kind: 'paid', id: 'ch_123' });   // → MessageReceived / ReceiveJson<T>
window.zigote.onMessage(m => render(m));                     // ← PostMessageAsync
window.addEventListener('zigote:message', e => render(e.detail));
```

The controller outlives the widget: unmounting hides the page (a tab switch does not reload),
disposing the controller destroys it. `controller.LastError` says why when there is no view.
Logging goes through `Zigote.Logging` — configure it with `AppLog.Bootstrap(LogEventLevel.Debug)`
in `Main` to see embed, load, navigation-block and page-message breadcrumbs.

`dotnet run --project example/WebViewExample` is a minimal browser exercising all of it.

## What the controller gives you

| | |
|---|---|
| Navigation | `Navigate`, `LoadHtml`, `GoBack`/`GoForward`, `Reload`, `StopLoading` |
| Page state | `Url`, `Title`, `IsLoading`, `Progress`, `CanGoBack`/`CanGoForward`, `LastError` |
| Events | `UrlChanged`, `TitleChanged`, `LoadingChanged`, `ProgressChanged`, `HistoryChanged`, `LoadFailed`, `MessageReceived` |
| Bridge | `PostMessageAsync`, `ReceiveJson<T>`, `AddUserScript` (document-start, all frames) |
| Policy | `NavigationFilter` — veto any navigation, including redirects and `target=_blank` |
| Session | `EvaluateJavaScriptAsync`, `ClearBrowsingDataAsync` (cookies, caches, DOM storage) |
| Settings | `JavaScriptEnabled`, `UserAgent`, `DevToolsEnabled`, `AllowAutoplay` |

`NavigationFilter` is a security hook: a filter that throws blocks the navigation rather than
allowing it. Everything is UI-thread only, and every event fires there.

## Backends

| Platform | Engine | Mode | Verified |
|---|---|---|---|
| Linux (Wayland) | WebKitGTK offscreen (software path, no WebGL) on its own GTK thread → engine texture, damaged rows only, BGRA with no conversion pass, input synthesized as GDK events | **texture** — composites like any widget | integration-tested (frames, input, typing, bridge, policy, history, settings, clean exit) + a GPU capture check |
| Linux (X11/XWayland) | WebKitGTK in a GTK popup, XReparentWindow into the SDL X11 window, GPU compositing on | overlay | runtime-verified |
| Windows | WebView2 against the engine HWND (needs the WebView2 Runtime; in Windows 11 by default) | overlay | compile-verified |
| Android | android.webkit.WebView in SDLActivity's layout via JNI | overlay | compile-verified |
| iOS | WKWebView over the UIWindow | overlay | written, needs a mac build |
| macOS desktop | not yet (WKWebView over the NSWindow contentView — the iOS file is the shape) | — | — |

On Wayland nothing needs configuring; `EnsureEmbeddableVideoDriver()` remains only as an
opt-in to the X11 overlay (a real native child window, via XWayland).

## What a frame costs

On Wayland the page runs on its own GTK thread and reaches the GPU without a single conversion
pass. Call `WebViewController.EnsureThreadedWebView()` before creating the App to get the thread —
it points libdecor at its Cairo plugin so SDL stops initializing GTK on the UI thread, which is the
only thing standing in the way of a second GTK thread. Without the call (or the plugin) the webview
falls back to pumping on the UI thread, exactly as before, and everything still works.

Scrolling a document at 1920×1080, 300 frames, `ZIGOTE_WEBVIEW_BENCH=1`:

| | UI thread per frame (p50 / p99) | frames over the 16.7 ms budget | page frames delivered |
|---|---|---|---|
| pumped on the UI thread | 9.7 ms / 20.9 ms | 20 / 300 | 151 / 300 |
| own GTK thread | 0.02 ms / 0.11 ms | 0 / 300 | ~190 / 300 |

and an idle page (nothing animating, `IdlePageCost`) costs **0.21 %** of one core against 0.87 %
on the UI-thread pump — the GTK thread blocks in GLib rather than polling, so a tab that is merely
open wakes nobody.

What is left in the path, per repainted frame: WebKit rasterizes (CPU, software), GTK reports the
damaged rows, and those rows are handed to the engine **as they are** — Cairo's BGRA at Cairo's own
stride, into a `bgra8_unorm_srgb` texture, with the channel order resolved by the sampler. There is
no swizzle, no intermediate buffer, no lock between the threads (the UI thread reads one published
reference), and no full-surface upload unless the surface was resized. The page still rasterizes on
the CPU, so it is the *engine* that stops stuttering; the page gets smoother because it is no longer
gated by the frame loop.

Hardware acceleration (`WebViewSettings.HardwareAcceleration`, on by default) is what makes WebGL,
GPU-decoded video and composited CSS transforms work, and it needs a real native window: every
overlay backend has one, the Wayland texture path does not. A `GtkOffscreenWindow` cannot give GDK
a GL context, and WebKit aborts the process rather than fall back — so that one backend pins the
policy to NEVER and rasterizes on the CPU. A browser wanting the accelerated engine on Linux calls
`WebViewController.EnsureEmbeddableVideoDriver()` and runs in overlay mode.

## Frame cost (Linux texture mode)

Built to sit inside a 60 fps budget, and measured — the plugin reports its own cost through
`Zigote.Core.Diagnostics.Profiler`, so `WebView.Pump` (the page's whole CPU cost per frame) and
`WebView.Convert` (frames the page actually repainted) show up in the DevTools profiler panel and
in the `profile` inspect command next to the engine's own scopes.

Three things keep it cheap:

- **Damage-driven frames.** GTK reports the rows WebKit repainted; only those are uploaded. An idle
  page costs one branch per wake-up — no upload, no conversion, nothing.
- **No conversion at all.** The engine takes Cairo's premultiplied BGRA at Cairo's own row stride
  (`LoadTextureFromPixels` / `UpdateTextureRows`), so the frame path has no pixel pass left in it:
  the channel order is the texture's format and the padding is the copy's stride.
- **A thread, or a private `GMainContext`.** With GTK to ourselves the whole stack runs on its own
  thread, blocking in GLib and touching the UI thread only to publish a texture handle. When
  something else owns GTK (SDL's libdecor plugin), WebKit's sources are instead kept on a private
  context and pumped by a `Ticker` — because left on the default context libdecor dispatches them
  from inside `SDL_PollEvent`, measured at **3.8 ms/frame**, invisible to any profiler scope.
- **One clock, when it matters.** In the shared-thread mode the page is pumped once per frame,
  before layout — not on a timer of its own, which would land content on a beat unrelated to the
  frame being drawn. On its own thread the question disappears: the UI thread samples the newest
  finished frame when it paints, and extra frames replace themselves.

In the shared-thread mode the pump is capped at half a frame, so a page that floods its main loop
cannot hold the UI thread past the budget. Unmounting the widget (a background tab) stops the
uploads while the page keeps running its timers, sockets and JS.

## Browser behaviour

Beyond navigation and the bridge, the Linux backends carry what a page expects from a browser:
per-gesture scroll semantics (WebKit's easing for wheel notches, off for trackpad deltas, so the
page stops sliding after your fingers lift), ctrl/shift+wheel, real mouse buttons with right-click
reaching the page (WebKit's own GTK context menu is suppressed — a browser shell draws its own),
enter/leave crossing so hover states clear, the page's cursor mapped onto `MouseCursor` (hand over
links, I-beam over text), event timestamps, and arrow/page keys handled by the page rather than
moving focus around the app.

The process-wide WebKit setup gives a real profile: cookies persisted to
`~/.local/share/zigote-webview/cookies.sqlite`, a favicon database beside it, the browser cache
model, and a user agent that appends the app's name and version to the platform's rather than
replacing it.

## Known ceilings

- Overlay platforms: the page always draws above Zigote content, and a native view scrolled
  half out of a viewport does not clip. The Wayland texture mode has neither limitation.
- Texture mode uses WebKit's software renderer: no accelerated WebGL/video compositing. This is not
  a policy choice — a `GtkOffscreenWindow` cannot give GDK a GL context, and WebKit `g_error`s
  ("GDK is not able to create a GL context") rather than fall back. An accelerated offscreen backend
  is possible on GTK4/webkitgtk-6.0 (measured: 3.6 ms/frame at 1080p, GPU-rendered, RGBA readback),
  but GTK4 exports no `GdkEvent` constructors and removed `gtk_main_do_event`, so such a view can
  render and cannot be clicked. Zero-copy is out on both toolkits: neither exposes the dmabuf behind
  a texture, and the pinned wgpu-native has no external-memory import (Metal-only native handles).
- Tab and Escape are not taken from the app while a page has focus (that needs `IKeyboardTrap` and
  an agreed way back out), so form-field traversal stops at the page's edge.
- Linux X11 keyboard focus is click-to-focus between the page and Zigote content.
- `LoadHtml` ignores `baseUrl` on Windows (`NavigateToString` has no base URL — the upgrade
  path is `SetVirtualHostNameToFolderMapping`).
- Android has no document-start injection API: user scripts run from `OnPageStarted`, which is
  before the page's scripts in practice but not guaranteed. Upgrade path is androidx.webkit's
  `WebViewCompat.addDocumentStartJavaScript`.
- Windows reports no load progress: `Progress` jumps 0 → 1 there.
