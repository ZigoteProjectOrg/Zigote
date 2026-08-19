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
| Linux (Wayland) | WebKitGTK offscreen (software path) → engine texture, input synthesized as GDK events | **texture** — composites like any widget | integration-tested (frames, input, bridge, policy, history, settings) |
| Linux (X11/XWayland) | WebKitGTK in a GTK popup, XReparentWindow into the SDL X11 window | overlay | runtime-verified |
| Windows | WebView2 against the engine HWND (needs the WebView2 Runtime; in Windows 11 by default) | overlay | compile-verified |
| Android | android.webkit.WebView in SDLActivity's layout via JNI | overlay | compile-verified |
| iOS | WKWebView over the UIWindow | overlay | written, needs a mac build |
| macOS desktop | not yet (WKWebView over the NSWindow contentView — the iOS file is the shape) | — | — |

On Wayland nothing needs configuring; `EnsureEmbeddableVideoDriver()` remains only as an
opt-in to the X11 overlay (a real native child window, via XWayland).

## Frame cost (Linux texture mode)

Built to sit inside a 60 fps budget, and measured — the plugin reports its own cost through
`Zigote.Core.Diagnostics.Profiler`, so `WebView.Pump` (the page's whole CPU cost per frame) and
`WebView.Convert` (frames the page actually repainted) show up in the DevTools profiler panel and
in the `profile` inspect command next to the engine's own scopes.

Three things keep it cheap:

- **Damage-driven frames.** GTK reports the rows WebKit repainted; only those are converted (SIMD
  BGRA→RGBA, four pixels per vector) and only then is a frame published. An idle page costs one
  branch per tick — no conversion, no upload. Measured steady-state idle: **~0.14 ms/frame** for
  the whole webview.
- **A private `GMainContext`.** WebKit's GLib sources are deliberately kept off the default
  context. Left on it, SDL's libdecor plugin dispatches them from inside `SDL_PollEvent` — measured
  at **3.8 ms/frame**, 23% of a 60 fps budget, invisible to any profiler scope. On the private
  context the same work is **0.27 ms** of `PollEvents` plus a named, bounded `WebView.Pump`.
- **One clock.** The page is pumped, and its surface read, from a `Ticker` — once per frame, on the
  UI thread, at a fixed point before layout. Not a `System.Threading.Timer` and not a GLib timeout:
  either of those fires at a phase unrelated to the frame being drawn, so content lands on an
  irregular beat and scrolling stutters even at a 60 fps average. A repaint WebKit produces during
  a frame is converted and painted in that same frame.

The pump is capped at half a frame, so a page that floods its main loop cannot hold the UI thread
past the budget; the leftover work is dispatched next frame. Unmounting the widget (a background
tab) stops the conversion and the upload while the page keeps running its timers, sockets and JS.

## Known ceilings

- Overlay platforms: the page always draws above Zigote content, and a native view scrolled
  half out of a viewport does not clip. The Wayland texture mode has neither limitation.
- Texture mode uses WebKit's software renderer: no accelerated WebGL/video compositing.
- Linux X11 keyboard focus is click-to-focus between the page and Zigote content.
- `LoadHtml` ignores `baseUrl` on Windows (`NavigateToString` has no base URL — the upgrade
  path is `SetVirtualHostNameToFolderMapping`).
- Android has no document-start injection API: user scripts run from `OnPageStarted`, which is
  before the page's scripts in practice but not guaranteed. Upgrade path is androidx.webkit's
  `WebViewCompat.addDocumentStartJavaScript`.
- Windows reports no load progress: `Progress` jumps 0 → 1 there.
