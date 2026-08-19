using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Zigote.Core;
using Zigote.Core.Engine;

namespace WebView;

/// <summary>
///     Windows backend: a WebView2 controller hosted directly against the engine's HWND —
///     no WinForms/WPF host, the composition-less hosting mode the Win32 API was built for.
///     <para>
///         Initialization is async (environment → controller); calls made before the controller
///         exists are queued and replayed, mirroring the controller-side pending pattern. All
///         WebView2 callbacks arrive on the creating thread — the engine's UI thread, whose SDL
///         loop pumps the Win32 messages WebView2 rides on — so events reach the owner directly.
///     </para>
///     <para>
///         Requires the WebView2 Runtime on the machine (preinstalled on Windows 11; the common
///         failure on Windows 10 without it lands in <see cref="WebViewController.LastError" />).
///         The NuGet package ships the matching WebView2Loader.dll into the app's output.
///     </para>
/// </summary>
internal sealed class WebView2Backend : IWebViewBackend
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WebView2Backend>();

    private readonly WebViewController _owner;
    private readonly List<Action> _pending = [];
    private CoreWebView2Controller? _controller;
    private Rect _rect;
    private float _scale = 1f;
    private bool _visible;
    private bool _disposed;

    private WebView2Backend(WebViewController owner) => _owner = owner;

    public static IWebViewBackend? TryCreate(WebViewController owner) => new WebView2Backend(owner);

    public void Attach(NativeParent parent)
    {
        if (parent.Kind != NativeParentKind.Win32Hwnd)
        {
            _owner.LastError = $"Windows backend needs an HWND parent, got {parent.Kind}";
            return;
        }

        _ = InitAsync(parent.Ptr1);
    }

    private async Task InitAsync(nint hwnd)
    {
        try
        {
            // Autoplay is a browser-process switch on Chromium, not a per-view setting.
            var options = new CoreWebView2EnvironmentOptions {
                AdditionalBrowserArguments = _owner.Settings.AllowAutoplay
                    ? "--autoplay-policy=no-user-gesture-required"
                    : "",
            };
            var environment = await CoreWebView2Environment.CreateAsync(options: options);
            var controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            if (_disposed)
            {
                controller.Close();
                return;
            }

            _controller = controller;
            var core = controller.CoreWebView2;
            ApplySettings(core);

            core.NavigationStarting += (_, e) =>
            {
                if (!_owner.AllowNavigation(e.Uri)) e.Cancel = true;
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) _owner.OnLoadFailed(core.Source, e.WebErrorStatus.ToString());
                _owner.OnLoadingChanged(false);
            };
            core.DocumentTitleChanged += (_, _) => _owner.OnTitleChanged(core.DocumentTitle);
            core.SourceChanged += (_, _) => _owner.OnUrlChanged(core.Source);
            core.HistoryChanged += (_, _) => _owner.OnHistoryChanged(core.CanGoBack, core.CanGoForward);
            core.WebMessageReceived += (_, e) => _owner.OnMessageReceived(MessageText(e));
            // A target=_blank link would otherwise open a second, unmanaged WebView2 window;
            // route it through the same filter and the same view instead.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (_owner.AllowNavigation(e.Uri)) core.Navigate(e.Uri);
            };
            // NavigationStarting fires before the filter can cancel, so loading is announced
            // only once the navigation actually survives it.
            core.ContentLoading += (_, _) => _owner.OnLoadingChanged(true);

            // The bridge shim first: user scripts queued behind it may already use window.zigote.
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(
                WebViewBridge.Shim("window.chrome.webview.postMessage.bind(window.chrome.webview)"));

            ApplyBounds(controller);
            controller.IsVisible = _visible;
            foreach (var action in _pending) action();
            _pending.Clear();
            Log.Debug("WebView2 controller up against HWND 0x{Hwnd:x}", hwnd);
        }
        catch (Exception ex)
        {
            // Overwhelmingly: no WebView2 Runtime installed. The widget stays a rectangle and
            // LastError says why, matching the Linux backend's failure shape.
            _owner.LastError = $"WebView2 unavailable: {ex.Message}";
            Log.Error(ex, "WebView2 could not initialize");
        }
    }

    private void ApplySettings(CoreWebView2 core)
    {
        var settings = _owner.Settings;
        core.Settings.IsScriptEnabled = settings.JavaScriptEnabled;
        core.Settings.IsWebMessageEnabled = true; // the window.chrome.webview bridge
        core.Settings.AreDevToolsEnabled = settings.DevToolsEnabled;
        core.Settings.AreDefaultContextMenusEnabled = settings.DevToolsEnabled;
        if (settings.UserAgent is { Length: > 0 } agent) core.Settings.UserAgent = agent;
    }

    /// <summary>WebMessage arrives either as a JSON value or as a raw string, and asking for the
    ///     wrong one throws — try the string first, fall back to the JSON text.</summary>
    private static string MessageText(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            return e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return e.WebMessageAsJson;
        }
    }

    private void WhenReady(Action action)
    {
        if (_controller is not null) action();
        else _pending.Add(action);
    }

    public void SetBounds(Rect windowRect, float scale)
    {
        _rect = windowRect;
        _scale = scale;
        if (_controller is { } controller) ApplyBounds(controller);
    }

    /// <summary>Controller bounds are client-area PHYSICAL pixels.</summary>
    private void ApplyBounds(CoreWebView2Controller controller)
    {
        controller.Bounds = new System.Drawing.Rectangle(
            x: (int)MathF.Round(_rect.X * _scale),
            y: (int)MathF.Round(_rect.Y * _scale),
            width: Math.Max(1, (int)MathF.Round(_rect.Width * _scale)),
            height: Math.Max(1, (int)MathF.Round(_rect.Height * _scale))
        );
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_controller is { } controller) controller.IsVisible = visible;
    }

    public void Navigate(string url) => WhenReady(() => _controller!.CoreWebView2.Navigate(url));

    public void LoadHtml(string html, string? baseUrl)
    {
        // ponytail: baseUrl ignored — NavigateToString has no base-URL parameter; add
        // SetVirtualHostNameToFolderMapping when someone needs relative resources.
        WhenReady(() => _controller!.CoreWebView2.NavigateToString(html));
    }

    public void GoBack() => _controller?.CoreWebView2.GoBack();

    public void GoForward() => _controller?.CoreWebView2.GoForward();

    public void Reload() => _controller?.CoreWebView2.Reload();

    public void StopLoading() => _controller?.CoreWebView2.Stop();

    public void AddUserScript(string source) =>
        WhenReady(() => _ = _controller!.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(source));

    public async Task ClearBrowsingDataAsync()
    {
        if (_controller?.CoreWebView2.Profile is not { } profile) return;
        await profile.ClearBrowsingDataAsync();
    }

    public async Task<string?> EvaluateJavaScriptAsync(string script)
    {
        if (_controller is not { } controller) return null;
        string json = await controller.CoreWebView2.ExecuteScriptAsync(script);
        if (json is null or "null" or "undefined") return null;
        // ExecuteScript answers JSON: unquote strings, pass every other value through as text —
        // the same "stringifiable value or null" convention as the WebKitGTK backend.
        try
        {
            return json.StartsWith('"') ? JsonSerializer.Deserialize<string>(json) : json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _pending.Clear();
        _controller?.Close();
        _controller = null;
    }
}
