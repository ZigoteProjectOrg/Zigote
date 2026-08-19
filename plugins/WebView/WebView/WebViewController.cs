using System.Text.Json;
using Serilog;
using Zigote.Core.Engine;
using Zigote.UI.Widgets;

namespace WebView;

/// <summary>
///     What a platform webview backend implements, over <see cref="INativeView" />'s
///     position/visibility contract. One backend = one live browser view; created on first
///     attach, disposed with the controller. All calls arrive on the UI thread; backends that
///     answer events from other threads post back through their owner's callbacks.
///     <para>
///         Backends read policy straight off their owner — <see cref="WebViewController.Settings" />
///         when they build the view, <see cref="WebViewController.NavigationFilter" /> on every
///         navigation — so adding a knob does not widen this interface.
///     </para>
/// </summary>
internal interface IWebViewBackend : INativeView
{
    void Attach(NativeParent parent);

    void Navigate(string url);

    void LoadHtml(string html, string? baseUrl);

    void GoBack();

    void GoForward();

    void Reload();

    void StopLoading();

    /// <summary>Run script in the page; null when evaluation failed or produced no value.</summary>
    Task<string?> EvaluateJavaScriptAsync(string script);

    /// <summary>Run <paramref name="source" /> at document-start of every page from now on —
    ///     before the page's own scripts, which is the only useful moment for a bridge or a
    ///     polyfill. Not applied to the document already loaded.</summary>
    void AddUserScript(string source);

    /// <summary>Drop cookies, caches and DOM storage for this webview's profile — the "log out
    ///     for real" button. Completes when the platform says the data is gone.</summary>
    Task ClearBrowsingDataAsync();
}

/// <summary>
///     A backend that renders the page into an engine texture instead of overlaying a native
///     view — the page composites like any widget, and input travels the other way: the widget
///     forwards its events here. Linux-Wayland today; the seam any future offscreen backend
///     (WPE, CEF) implements.
/// </summary>
internal interface ITextureWebViewBackend
{
    /// <summary>A new frame is ready (UI thread) — the surface widget repaints on it.</summary>
    event Action? FrameArrived;

    (uint Width, uint Height) TextureSize { get; }

    /// <summary>Create-or-update the engine texture for the latest frame. UI thread, during
    ///     paint. 0 until the first frame lands.</summary>
    ulong AcquireTexture();

    /// <summary>The widget's logical size and display scale — the page surface follows it.</summary>
    void SetSurfaceSize(float logicalWidth, float logicalHeight, float scale);

    // Input, in widget-local physical pixels (logical × scale — the page surface's space).

    void PointerDown(float x, float y);

    void PointerUp(float x, float y);

    void PointerMove(float x, float y);

    void Scroll(float dx, float dy, float x, float y);

    void Key(char ch, uint scancode, bool down, Zigote.Core.Events.Modifiers mods);

    void Text(string text);

    /// <summary>The widget gained/lost keyboard focus — the page's caret, focus rings and
    ///     selection rendering follow it.</summary>
    void SetPageFocus(bool focused);

    /// <summary>Whether the surface widget is mounted. False (a background tab) keeps the page
    ///     running but stops converting and uploading frames nobody is looking at.</summary>
    void SetDisplayed(bool displayed);
}

/// <summary>
///     WebViewController — the app-facing half of a webview: navigation, page state, JS, and the
///     two-way message bridge an embedded web extension (a map, a checkout, a chart) talks to its
///     host over. Pair it with a <see cref="WebView" /> widget.
///     <para>
///         The controller outlives the widget: unmounting the widget hides the view (a tab
///         switch does not reload the page); disposing the controller destroys it. Calls made
///         before the first mount are queued and applied when the platform view exists — so a
///         whole session can be configured in a constructor.
///     </para>
///     <para>
///         <b>Threading:</b> everything here is UI-thread only, and every event fires there too.
///     </para>
///     <para>
///         <b>Linux:</b> on a native Wayland session the page renders offscreen into an engine
///         texture and composites like any widget — nothing to configure. Under X11 (or after
///         <see cref="EnsureEmbeddableVideoDriver" /> opted into XWayland) it is a true overlay
///         native view instead.
///     </para>
/// </summary>
public sealed class WebViewController : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WebViewController>();

    /// <summary>camelCase in, camelCase out, case-insensitive on the way back — the bridge speaks
    ///     JavaScript's conventions, not .NET's, so a page can send <c>{ kind: 'tap' }</c> and a
    ///     host record can call the property Kind.</summary>
    private static readonly JsonSerializerOptions BridgeJson = JsonSerializerOptions.Web;

    private readonly List<string> _userScripts = [];
    private IWebViewBackend? _backend;
    private string? _pendingUrl;
    private (string Html, string? BaseUrl)? _pendingHtml;
    private bool _disposed;

    public WebViewController(WebViewSettings? settings = null) => Settings = settings ?? new WebViewSettings();

    /// <summary>Fixed at construction; backends read it when they build the view.</summary>
    public WebViewSettings Settings { get; }

    /// <summary>The page's current URL, tracked through navigation and redirects.</summary>
    public string? Url { get; private set; }

    /// <summary>The page's document title.</summary>
    public string? Title { get; private set; }

    /// <summary>True while the page is loading.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Load progress, 0 → 1. Jumps 0 → 1 on backends with no progress reporting
    ///     (WebView2), so drive a spinner off <see cref="IsLoading" /> and a bar off this.</summary>
    public double Progress { get; private set; }

    /// <summary>Whether there is history to walk. Updated before <see cref="HistoryChanged" />.</summary>
    public bool CanGoBack { get; private set; }

    public bool CanGoForward { get; private set; }

    /// <summary>Why there is no webview, when there is none (no backend on this platform, no
    ///     WebView2 runtime, GTK could not start). Null while everything is fine.</summary>
    public string? LastError { get; internal set; }

    /// <summary>
    ///     Veto navigations: return false and the page stays where it is. Runs on the UI thread
    ///     before every top-level navigation, including redirects and link clicks — the hook a
    ///     checkout flow uses to catch its <c>myapp://return</c> URL, or an embedded widget uses
    ///     to keep the user off the open web. Null allows everything.
    ///     <para>
    ///         Keep it fast and side-effect-light: on WebKit and Android the page's navigation
    ///         is blocked until it answers.
    ///     </para>
    /// </summary>
    public Func<string, bool>? NavigationFilter { get; set; }

    public event Action<string>? UrlChanged;

    public event Action<string>? TitleChanged;

    /// <summary>Loading started (true) or finished/failed (false).</summary>
    public event Action<bool>? LoadingChanged;

    public event Action<double>? ProgressChanged;

    /// <summary><see cref="CanGoBack" />/<see cref="CanGoForward" /> moved.</summary>
    public event Action? HistoryChanged;

    /// <summary>A load did not finish. The platform still shows its own error page.</summary>
    public event Action<WebViewError>? LoadFailed;

    /// <summary>
    ///     The page called <c>window.zigote.postMessage(...)</c>. Objects arrive as JSON text
    ///     (<see cref="ReceiveJson{T}" /> is the typed shortcut); strings arrive verbatim.
    /// </summary>
    public event Action<string>? MessageReceived;

    /// <summary>Load a URL (http/https/file). Callable before the widget first mounts.</summary>
    public void Navigate(string url)
    {
        if (_backend is { } b) b.Navigate(url);
        else
        {
            _pendingUrl = url;
            _pendingHtml = null;
        }
    }

    /// <summary>Load an HTML string; relative references resolve against <paramref name="baseUrl" />.</summary>
    public void LoadHtml(string html, string? baseUrl = null)
    {
        if (_backend is { } b) b.LoadHtml(html: html, baseUrl: baseUrl);
        else
        {
            _pendingHtml = (html, baseUrl);
            _pendingUrl = null;
        }
    }

    public void GoBack() => _backend?.GoBack();

    public void GoForward() => _backend?.GoForward();

    public void Reload() => _backend?.Reload();

    public void StopLoading() => _backend?.StopLoading();

    /// <summary>Evaluate script in the page. Null when there is no view yet, the script threw,
    ///     or it produced no stringifiable value.</summary>
    public Task<string?> EvaluateJavaScriptAsync(string script)
    {
        return _backend?.EvaluateJavaScriptAsync(script) ?? Task.FromResult<string?>(null);
    }

    /// <summary>
    ///     Run <paramref name="source" /> at the start of every document this webview loads, before
    ///     the page's own scripts — where a bridge, a polyfill or an API key belongs. Registered
    ///     scripts survive navigation and reloads; calling this before the first mount is fine and
    ///     is the usual place. Not applied to a document already loaded (reload for that).
    /// </summary>
    public void AddUserScript(string source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _userScripts.Add(source);
        _backend?.AddUserScript(source);
    }

    /// <summary>
    ///     Send a message to the page: <c>window.zigote.onMessage</c> handlers and the
    ///     <c>zigote:message</c> event both see it. Strings arrive as strings; anything else is
    ///     serialized to JSON and arrives as a parsed object, so a page and its host can speak
    ///     records rather than string soup.
    /// </summary>
    public Task PostMessageAsync(object message)
    {
        // JSON both ways: serializing a string produces a quoted JS string literal, an object
        // produces an object literal, and both are valid arguments — one path, no escaping bugs.
        string json = JsonSerializer.Serialize(message, BridgeJson);
        return EvaluateJavaScriptAsync($"window.zigote && window.zigote.__deliver({json})");
    }

    /// <summary>
    ///     <see cref="MessageReceived" /> as typed values: JSON that does not fit
    ///     <typeparamref name="T" /> is logged and dropped rather than thrown into the UI thread
    ///     from inside a page's script.
    /// </summary>
    public IDisposable ReceiveJson<T>(Action<T> handler)
    {
        void OnMessage(string raw)
        {
            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(raw, BridgeJson);
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "Page message is not valid {Type} JSON: {Raw}", typeof(T).Name, Truncate(raw));
                return;
            }

            if (value is not null) handler(value);
        }

        MessageReceived += OnMessage;
        return new Unsubscribe(() => MessageReceived -= OnMessage);
    }

    /// <summary>Drop cookies, caches and DOM storage — a real log-out. No-op before the first
    ///     mount (there is nothing stored yet for a view that never existed).</summary>
    public Task ClearBrowsingDataAsync() => _backend?.ClearBrowsingDataAsync() ?? Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend?.Dispose();
        _backend = null;
        TextureBackend = null;
    }

    /// <summary>
    ///     Linux only, no-op elsewhere — and OPTIONAL: native Wayland already gets a webview via
    ///     the offscreen texture backend. Call this before constructing the App only to prefer
    ///     the X11 overlay backend instead (a real native child window, at the cost of running
    ///     the whole app through XWayland and the overlay's always-on-top ceiling). Respects an
    ///     explicit SDL_VIDEO_DRIVER the user already set.
    /// </summary>
    public static void EnsureEmbeddableVideoDriver()
    {
        if (!OperatingSystem.IsLinux()) return;
        SetNativeEnv("SDL_VIDEO_DRIVER", "x11");
        SetNativeEnv("GDK_BACKEND", "x11");
        // WebKitGTK's DMABUF renderer is a known blank-page under XWayland (rendering happens,
        // nothing reaches the X pixmap). Shared-memory rendering is the reliable path here.
        SetNativeEnv("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
    }

    /// <summary>
    ///     Both environments, not just the managed one: on Linux,
    ///     <see cref="Environment.SetEnvironmentVariable(string,string)" /> updates only .NET's
    ///     managed environment block — native <c>getenv</c>, which is what SDL and GTK read,
    ///     never sees it. Respects a value the user already exported.
    /// </summary>
    private static void SetNativeEnv(string name, string value)
    {
        if (Environment.GetEnvironmentVariable(name) is not null) return;
        Environment.SetEnvironmentVariable(name, value);
        setenv(name, value, 1);
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    /// <summary>The texture-mode backend, when this platform renders the page into the engine
    ///     (Linux-Wayland); null on overlay platforms. Valid after <see cref="EnsureAttached" />.</summary>
    internal ITextureWebViewBackend? TextureBackend { get; private set; }

    /// <summary>
    ///     The widget mounted: make sure the platform view exists. Returns the overlay view for
    ///     the host widget to position — or null, either because this platform is texture-mode
    ///     (see <see cref="TextureBackend" />) or because there is no backend at all
    ///     (<see cref="LastError" /> says which).
    /// </summary>
    internal INativeView? EnsureAttached(NativeParent parent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backend is not null) return _backend as INativeView;

        if (parent.Kind is NativeParentKind.None)
        {
            LastError = "no native window handle on this platform";
            Log.Warning("No webview: {Reason}", LastError);
            return null;
        }

        _backend = WebViewDriver.Create(this, parent);
        if (_backend is null)
        {
            LastError ??= "no webview backend for this platform";
            Log.Warning("No webview: {Reason}", LastError);
            return null;
        }

        TextureBackend = _backend as ITextureWebViewBackend;
        _backend.Attach(parent);
        foreach (string script in _userScripts) _backend.AddUserScript(script);
        if (_pendingUrl is { } url) _backend.Navigate(url);
        else if (_pendingHtml is { } html) _backend.LoadHtml(html: html.Html, baseUrl: html.BaseUrl);
        _pendingUrl = null;
        _pendingHtml = null;
        Log.Debug("Webview attached: {Backend}, {Mode} mode",
            _backend.GetType().Name, TextureBackend is null ? "overlay" : "texture");
        return TextureBackend is null ? _backend as INativeView : null;
    }

    /// <summary>Backends ask before navigating. Never throws into a page's navigation:
    ///     a filter that blows up blocks, which is the safe side of a security hook.</summary>
    internal bool AllowNavigation(string url)
    {
        if (NavigationFilter is not { } filter) return true;
        try
        {
            bool allowed = filter(url);
            if (!allowed) Log.Debug("Navigation blocked by filter: {Url}", url);
            return allowed;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Navigation filter threw for {Url}; blocking", url);
            return false;
        }
    }

    // Backends report page state through these; already marshaled to the UI thread by the backend.

    internal void OnUrlChanged(string url)
    {
        Url = url;
        UrlChanged?.Invoke(url);
    }

    internal void OnTitleChanged(string title)
    {
        Title = title;
        TitleChanged?.Invoke(title);
    }

    internal void OnLoadingChanged(bool loading)
    {
        IsLoading = loading;
        if (!loading) OnProgressChanged(1);
        LoadingChanged?.Invoke(loading);
    }

    internal void OnProgressChanged(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        if (Math.Abs(progress - Progress) < 0.0001) return;
        Progress = progress;
        ProgressChanged?.Invoke(progress);
    }

    internal void OnHistoryChanged(bool canGoBack, bool canGoForward)
    {
        if (canGoBack == CanGoBack && canGoForward == CanGoForward) return;
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        HistoryChanged?.Invoke();
    }

    internal void OnLoadFailed(string url, string message)
    {
        // A failed load deserves a line even when nobody subscribed: "no page and no logs" is the
        // worst failure shape a webview can have.
        Log.Warning("Load failed: {Url} — {Message}", url, message);
        LoadFailed?.Invoke(new WebViewError(url, message));
    }

    internal void OnMessageReceived(string message)
    {
        Log.Verbose("Page message: {Message}", Truncate(message));
        MessageReceived?.Invoke(message);
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private sealed class Unsubscribe(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
