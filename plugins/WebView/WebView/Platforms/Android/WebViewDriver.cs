using Android.App;
using Android.Runtime;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Serilog;
using Zigote.Core;
using Zigote.Core.Engine;
using AWebView = Android.Webkit.WebView;

namespace WebView;

/// <summary>Android: android.webkit.WebView in SDL's own view hierarchy — no engine handle needed.</summary>
internal static class WebViewDriver
{
    public static IWebViewBackend? Create(WebViewController owner, Zigote.Core.Engine.NativeParent parent)
    {
        var backend = AndroidWebViewBackend.TryCreate(owner);
        if (backend is null) owner.LastError = "SDLActivity is not up yet";
        return backend;
    }
}

/// <summary>
///     The platform view goes into SDLActivity's RelativeLayout (the same ViewGroup the SDL
///     surface lives in), positioned by margins in physical pixels. SDLActivity is vendored Java
///     with no C# binding, so the activity and its layout are fetched once over JNI.
///     <para>
///         IWebViewBackend calls arrive on the app (SDL) thread; every view touch is posted to
///         the UI thread. RunOnUiThread preserves order, so state lives behind it with no lock —
///         a Navigate posted after Attach always finds the created view.
///     </para>
/// </summary>
internal sealed class AndroidWebViewBackend : IWebViewBackend
{
    private static readonly ILogger Log = Serilog.Log.ForContext<AndroidWebViewBackend>();

    private readonly WebViewController _owner;
    private readonly Activity _activity;
    private readonly ViewGroup _layout;

    // UI-thread state.
    private AWebView? _view;
    private (int X, int Y, int W, int H)? _bounds;
    private bool _visible;

    /// <summary>Document-start scripts, replayed into every page from OnPageStarted. The bridge
    ///     shim is always first.</summary>
    private readonly List<string> _userScripts = [];

    private AndroidWebViewBackend(WebViewController owner, Activity activity, ViewGroup layout)
    {
        _owner = owner;
        _activity = activity;
        _layout = layout;
        _userScripts.Add(WebViewBridge.Shim($"window.{WebViewBridge.AndroidObject}.postMessage"));
    }

    public static AndroidWebViewBackend? TryCreate(WebViewController owner)
    {
        nint cls = JNIEnv.FindClass("org/libsdl/app/SDLActivity");
        nint getContext = JNIEnv.GetStaticMethodID(cls, "getContext", "()Landroid/app/Activity;");
        nint getContentView = JNIEnv.GetStaticMethodID(cls, "getContentView", "()Landroid/view/View;");
        var activity = Java.Lang.Object.GetObject<Activity>(
            JNIEnv.CallStaticObjectMethod(cls, getContext), JniHandleOwnership.TransferLocalRef);
        var layout = Java.Lang.Object.GetObject<ViewGroup>(
            JNIEnv.CallStaticObjectMethod(cls, getContentView), JniHandleOwnership.TransferLocalRef);
        if (activity is null || layout is null) return null;
        return new AndroidWebViewBackend(owner, activity, layout);
    }

    public void Attach(NativeParent parent)
    {
        // parent is unused: on Android the attachment point is the activity's layout, not the
        // ANativeWindow the engine reports.
        _activity.RunOnUiThread(() =>
        {
            var view = new AWebView(_activity);
            ApplySettings(view);
            view.SetWebViewClient(new Client(this));
            view.SetWebChromeClient(new ChromeClient(_owner));
            view.AddJavascriptInterface(new HostBridge(this), WebViewBridge.AndroidObject);
            view.Visibility = ViewStates.Gone;
            _layout.AddView(view);
            _view = view;
            Apply();
            Log.Debug("Android WebView added to the SDL layout");
        });
    }

    /// <summary>UI thread, once at creation — <see cref="WebViewSettings" /> is immutable.</summary>
    private void ApplySettings(AWebView view)
    {
        var options = _owner.Settings;
        if (view.Settings is not { } settings) return;
        settings.JavaScriptEnabled = options.JavaScriptEnabled;
        // Not optional for embedded web extensions: maps cache tiles and checkouts keep session
        // state in localStorage, and both silently misbehave without it.
        settings.DomStorageEnabled = true;
        settings.MediaPlaybackRequiresUserGesture = !options.AllowAutoplay;
        if (options.UserAgent is { Length: > 0 } agent) settings.UserAgentString = agent;
        if (options.DevToolsEnabled) AWebView.SetWebContentsDebuggingEnabled(true);
    }

    public void SetBounds(Rect windowRect, float scale)
    {
        var b = (
            X: (int)MathF.Round(windowRect.X * scale),
            Y: (int)MathF.Round(windowRect.Y * scale),
            W: Math.Max(1, (int)MathF.Round(windowRect.Width * scale)),
            H: Math.Max(1, (int)MathF.Round(windowRect.Height * scale))
        );
        _activity.RunOnUiThread(() =>
        {
            _bounds = b;
            Apply();
        });
    }

    public void SetVisible(bool visible)
    {
        _activity.RunOnUiThread(() =>
        {
            _visible = visible;
            Apply();
        });
    }

    public void Navigate(string url) => _activity.RunOnUiThread(() => _view?.LoadUrl(url));

    public void LoadHtml(string html, string? baseUrl) => _activity.RunOnUiThread(() =>
        _view?.LoadDataWithBaseURL(baseUrl, html, "text/html", "utf-8", null));

    public void GoBack() => _activity.RunOnUiThread(() => _view?.GoBack());

    public void GoForward() => _activity.RunOnUiThread(() => _view?.GoForward());

    public void Reload() => _activity.RunOnUiThread(() => _view?.Reload());

    public void StopLoading() => _activity.RunOnUiThread(() => _view?.StopLoading());

    public void AddUserScript(string source) => _activity.RunOnUiThread(() => _userScripts.Add(source));

    public Task ClearBrowsingDataAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activity.RunOnUiThread(() =>
        {
            CookieManager.Instance?.RemoveAllCookies(null);
            CookieManager.Instance?.Flush();
            WebStorage.Instance?.DeleteAllData();
            _view?.ClearCache(true);
            _view?.ClearHistory();
            _view?.ClearFormData();
            tcs.TrySetResult();
        });
        return tcs.Task;
    }

    public Task<string?> EvaluateJavaScriptAsync(string script)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _activity.RunOnUiThread(() =>
        {
            if (_view is not { } view)
            {
                tcs.TrySetResult(null);
                return;
            }

            view.EvaluateJavascript(script, new JsCallback(tcs));
        });
        return tcs.Task;
    }

    public void Dispose() => _activity.RunOnUiThread(() =>
    {
        if (_view is not { } view) return;
        _view = null;
        view.RemoveJavascriptInterface(WebViewBridge.AndroidObject);
        _layout.RemoveView(view);
        view.Destroy();
    });

    /// <summary>UI thread: put the view where the host said, shown only once it has a place.</summary>
    private void Apply()
    {
        if (_view is not { } view) return;
        if (_bounds is { } b)
            view.LayoutParameters = new RelativeLayout.LayoutParams(b.W, b.H) {
                LeftMargin = b.X,
                TopMargin = b.Y,
            };
        view.Visibility = _visible && _bounds is not null ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>
    ///     UI thread: (re-)install the bridge and every user script into the document that just
    ///     started loading.
    ///     <para>
    ///         ponytail: Android has no document-start injection API, so this runs from
    ///         OnPageStarted — after the document exists, in practice before its own scripts run,
    ///         but not guaranteed against a script in the very first bytes of &lt;head&gt;. The
    ///         upgrade path is androidx.webkit's WebViewCompat.addDocumentStartJavaScript, which
    ///         would add an AndroidX dependency to the plugin.
    ///     </para>
    /// </summary>
    private void InjectUserScripts(AWebView view)
    {
        foreach (string source in _userScripts) view.EvaluateJavascript(source, null);
    }

    private void ReportHistory(AWebView view) => _owner.OnHistoryChanged(view.CanGoBack(), view.CanGoForward());

    // WebViewClient/WebChromeClient callbacks already arrive on the UI thread, which is what the
    // shared controller expects — no re-posting.

    private sealed class Client(AndroidWebViewBackend backend) : WebViewClient
    {
        public override void OnPageStarted(AWebView? view, string? url, Android.Graphics.Bitmap? favicon)
        {
            if (url is not null) backend._owner.OnUrlChanged(url);
            backend._owner.OnLoadingChanged(true);
            if (view is not null)
            {
                backend.InjectUserScripts(view);
                backend.ReportHistory(view);
            }
        }

        public override void OnPageFinished(AWebView? view, string? url)
        {
            if (url is not null) backend._owner.OnUrlChanged(url);
            backend._owner.OnLoadingChanged(false);
            if (view is not null) backend.ReportHistory(view);
        }

        public override bool ShouldOverrideUrlLoading(AWebView? view, IWebResourceRequest? request)
        {
            string? url = request?.Url?.ToString();
            // true = "the app handled it", which for a blocked URL means "nothing happens".
            return url is not null && !backend._owner.AllowNavigation(url);
        }

        public override void OnReceivedError(
            AWebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            // Subresource failures are noise; only the main document's failure is the page's.
            if (request?.IsForMainFrame != true) return;
            backend._owner.OnLoadFailed(
                request.Url?.ToString() ?? "",
                error?.Description ?? $"error {(int?)error?.ErrorCode}");
        }
    }

    private sealed class ChromeClient(WebViewController owner) : WebChromeClient
    {
        public override void OnReceivedTitle(AWebView? view, string? title)
        {
            if (title is not null) owner.OnTitleChanged(title);
        }

        public override void OnProgressChanged(AWebView? view, int newProgress) =>
            owner.OnProgressChanged(newProgress / 100.0);

        public override bool OnConsoleMessage(ConsoleMessage? consoleMessage)
        {
            if (consoleMessage is null) return false;
            Log.Debug("console[{Level}] {Message} ({Source}:{Line})",
                consoleMessage.InvokeMessageLevel(), consoleMessage.Message(),
                consoleMessage.SourceId(), consoleMessage.LineNumber());
            return true;
        }
    }

    /// <summary>
    ///     The page's <c>window.__zigoteHost</c>. Calls arrive on WebView's JavaBridge thread,
    ///     never the UI thread, so the hop is mandatory — the controller's events are UI-thread only.
    /// </summary>
    private sealed class HostBridge(AndroidWebViewBackend backend) : Java.Lang.Object
    {
        [Java.Interop.Export("postMessage")]
        [JavascriptInterface]
        public void PostMessage(string message) =>
            backend._activity.RunOnUiThread(() => backend._owner.OnMessageReceived(message));
    }

    private sealed class JsCallback(TaskCompletionSource<string?> tcs) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value)
        {
            string? raw = value?.ToString();
            if (raw is null or "null")
            {
                tcs.TrySetResult(null);
                return;
            }

            // EvaluateJavascript answers with a JSON value — strings arrive quoted and escaped.
            try
            {
                tcs.TrySetResult(raw.StartsWith('"')
                    ? System.Text.Json.JsonSerializer.Deserialize<string>(raw)
                    : raw);
            }
            catch (System.Text.Json.JsonException)
            {
                tcs.TrySetResult(raw);
            }
        }
    }
}
