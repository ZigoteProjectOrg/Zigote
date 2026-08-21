namespace WebView;

/// <summary>
///     What the page is allowed to do, fixed for the life of a <see cref="WebViewController" />
///     (backends read it once, when they build their view). Defaults are what an embedded web
///     extension — a map, a checkout flow — needs to work at all: scripts and DOM storage on,
///     the platform's own user agent, developer tools off.
/// </summary>
public sealed record WebViewSettings
{
    /// <summary>Scripts in the page. Off makes <see cref="WebViewController.EvaluateJavaScriptAsync" />
    ///     and the message bridge useless too — only worth it for untrusted static content.</summary>
    public bool JavaScriptEnabled { get; init; } = true;

    /// <summary>Override the browser's User-Agent. Null keeps the platform's own, which is what
    ///     most sites are tested against.</summary>
    public string? UserAgent { get; init; }

    /// <summary>The platform's web inspector (WebKit's, WebView2's, Chrome remote debugging on
    ///     Android). A development switch — it is a debugging port on someone else's machine.</summary>
    public bool DevToolsEnabled { get; init; }

    /// <summary>
    ///     Let WebKit composite the page on the GPU — WebGL, GPU-decoded video, composited CSS
    ///     transforms and filters. On by default, and honoured wherever the embedding can hand
    ///     WebKit a GL surface: the Linux X11 overlay, Windows, Android and iOS. The Linux
    ///     <i>Wayland</i> texture path ignores it and stays on the software rasterizer — an
    ///     offscreen GDK window has no GL context to give, so there is nothing to accelerate into.
    ///     Turn it off to work around a broken GL driver.
    /// </summary>
    public bool HardwareAcceleration { get; init; } = true;

    /// <summary>Let the page start audio/video without a user gesture. Off matches every
    ///     mobile browser and is what an embedded map or checkout wants.</summary>
    public bool AllowAutoplay { get; init; }
}

/// <summary>A load that did not finish: the URL that failed and the platform's own reason.</summary>
public readonly record struct WebViewError(string Url, string Message)
{
    public override string ToString() => $"{Url}: {Message}";
}

/// <summary>
///     The page-side half of the host bridge, injected into every document before the page's own
///     scripts run. The same <c>window.zigote</c> API on every platform; only the native send
///     primitive differs, which is why backends supply just that expression.
///     <code>
///     window.zigote.postMessage({ kind: 'ready' });        // page → host (objects are JSON)
///     window.zigote.onMessage(m => console.log(m));        // host → page
///     window.addEventListener('zigote:message', e => ...); // the same delivery, as an event
///     </code>
/// </summary>
internal static class WebViewBridge
{
    /// <summary>The handler name both WebKit backends register (<c>window.webkit.messageHandlers.NAME</c>).</summary>
    public const string HandlerName = "zigote";

    /// <summary>The Android <c>@JavascriptInterface</c> object name.</summary>
    public const string AndroidObject = "__zigoteHost";

    /// <summary>The shim, parameterized by how this platform hands a string to the host.</summary>
    /// <param name="sendExpression">
    ///     A JS callable taking one string — e.g.
    ///     <c>window.webkit.messageHandlers.zigote.postMessage</c>. Called with the message,
    ///     already stringified.
    /// </param>
    public static string Shim(string sendExpression) =>
        $$"""
          (function () {
              if (window.zigote && window.zigote.__zigote) return;
              var listeners = [];
              var send = function (s) { ({{sendExpression}})(s); };
              window.zigote = {
                  __zigote: true,
                  postMessage: function (m) {
                      send(typeof m === 'string' ? m : JSON.stringify(m));
                  },
                  onMessage: function (fn) {
                      listeners.push(fn);
                      return function () { listeners = listeners.filter(function (f) { return f !== fn; }); };
                  },
                  // Host → page. Called by EvaluateJavaScript, never by the page.
                  __deliver: function (m) {
                      if (typeof window.zigote.onmessage === 'function') {
                          try { window.zigote.onmessage(m); } catch (e) { console.error(e); }
                      }
                      listeners.slice().forEach(function (fn) {
                          try { fn(m); } catch (e) { console.error(e); }
                      });
                      window.dispatchEvent(new CustomEvent('zigote:message', { detail: m }));
                  },
              };
          })();
          """;
}
