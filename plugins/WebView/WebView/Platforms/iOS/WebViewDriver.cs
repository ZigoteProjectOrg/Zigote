using CoreFoundation;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Serilog;
using UIKit;
using WebKit;
using Zigote.Core;
using Zigote.Core.Engine;

namespace WebView;

/// <summary>iOS backend selection — WKWebView is the only browser view the platform allows.</summary>
internal static class WebViewDriver
{
    public static IWebViewBackend? Create(WebViewController owner, Zigote.Core.Engine.NativeParent parent)
    {
        var app = Zigote.UI.Host.App.Active;
        return new WkWebViewBackend(owner, post: app is null ? a => a() : app.Post);
    }
}

/// <summary>
///     iOS backend: a WKWebView added as a subview of the SDL UIWindow and framed to the widget.
///     UIKit is main-thread-only and IWebViewBackend calls arrive on the app/SDL thread, so every
///     UIKit touch is dispatched onto the main queue; delegate callbacks (already on the main
///     queue) post page state back to the app thread, mirroring the Linux backend's _post.
///     <para>
///         The host bridge and every document-start script live on the configuration's
///         WKUserContentController, which — like WebKitGTK's — must be complete before the view
///         is built. Scripts added later are appended there and take effect from the next load.
///     </para>
/// </summary>
internal sealed class WkWebViewBackend : IWebViewBackend
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WkWebViewBackend>();

    private readonly WebViewController _owner;
    private readonly Action<Action> _post;

    // Main-queue state: created in Attach's dispatch, touched only inside DispatchAsync blocks.
    private WKWebView? _webView;
    private WKUserContentController? _userContent;
    private NavigationDelegate? _delegate;
    private HostBridge? _bridge;
    private IDisposable? _progressObserver;

    internal WkWebViewBackend(WebViewController owner, Action<Action> post)
    {
        _owner = owner;
        _post = post;
    }

    public void Attach(NativeParent parent)
    {
        if (parent.Kind != NativeParentKind.IosUiWindow)
        {
            _owner.LastError = $"iOS backend needs a UIWindow, got {parent.Kind}";
            return;
        }

        nint windowPtr = parent.Ptr1;
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (Runtime.GetNSObject<UIWindow>(windowPtr) is not { } window)
            {
                _owner.LastError = "UIWindow handle did not resolve";
                return;
            }

            _bridge = new HostBridge(this);
            _userContent = new WKUserContentController();
            _userContent.AddScriptMessageHandler(_bridge, WebViewBridge.HandlerName);
            AddUserScript(WebViewBridge.Shim(
                $"window.webkit.messageHandlers.{WebViewBridge.HandlerName}.postMessage.bind(" +
                $"window.webkit.messageHandlers.{WebViewBridge.HandlerName})"));

            var configuration = new WKWebViewConfiguration { UserContentController = _userContent };
            configuration.DefaultWebpagePreferences.AllowsContentJavaScript = _owner.Settings.JavaScriptEnabled;
            configuration.AllowsInlineMediaPlayback = true;
            configuration.MediaTypesRequiringUserActionForPlayback = _owner.Settings.AllowAutoplay
                ? WKAudiovisualMediaTypes.None
                : WKAudiovisualMediaTypes.All;

            _delegate = new NavigationDelegate(this);
            _webView = new WKWebView(CGRect.Empty, configuration) {
                NavigationDelegate = _delegate,
                Hidden = true,
            };
            if (_owner.Settings.UserAgent is { Length: > 0 } agent) _webView.CustomUserAgent = agent;
            // Safari's Web Inspector against this view. 16.4+ made it opt-in; before that every
            // debug build was inspectable anyway, so there is nothing to do on older systems.
            if (_owner.Settings.DevToolsEnabled && OperatingSystem.IsIOSVersionAtLeast(16, 4))
                _webView.Inspectable = true;

            // WKWebView reports load progress only through KVO — there is no delegate callback.
            _progressObserver = _webView.AddObserver(
                "estimatedProgress",
                NSKeyValueObservingOptions.New,
                _ =>
                {
                    double progress = _webView?.EstimatedProgress ?? 0;
                    _post(() => _owner.OnProgressChanged(progress));
                });

            window.AddSubview(_webView);
            Log.Debug("WKWebView added to the SDL UIWindow");
        });
    }

    public void SetBounds(Rect windowRect, float scale)
    {
        // The engine's logical coordinates ARE points on iOS; scale only separates points from
        // device pixels, which UIKit handles itself — so the rect passes through untouched.
        var frame = new CGRect(windowRect.X, windowRect.Y, windowRect.Width, windowRect.Height);
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (_webView is { } view) view.Frame = frame;
        });
    }

    public void SetVisible(bool visible)
    {
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (_webView is { } view) view.Hidden = !visible;
        });
    }

    public void Navigate(string url)
    {
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            // NSUrl.FromString answers null for strings that are not URLs at all; a request from
            // a null NSUrl throws, so the malformed case degrades to "nothing happens".
            if (_webView is { } view && NSUrl.FromString(url) is { } nsUrl)
                view.LoadRequest(new NSUrlRequest(nsUrl));
        });
    }

    public void LoadHtml(string html, string? baseUrl)
    {
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (_webView is { } view)
                view.LoadHtmlString(html, baseUrl is null ? null : NSUrl.FromString(baseUrl));
        });
    }

    public void GoBack()
    {
        DispatchQueue.MainQueue.DispatchAsync(() => _webView?.GoBack());
    }

    public void GoForward()
    {
        DispatchQueue.MainQueue.DispatchAsync(() => _webView?.GoForward());
    }

    public void Reload()
    {
        DispatchQueue.MainQueue.DispatchAsync(() => _webView?.Reload());
    }

    public void StopLoading()
    {
        DispatchQueue.MainQueue.DispatchAsync(() => _webView?.StopLoading());
    }

    /// <summary>Document-start, all frames: an iframed payment widget needs the bridge too.</summary>
    public void AddUserScript(string source)
    {
        DispatchQueue.MainQueue.DispatchAsync(() => _userContent?.AddUserScript(
            new WKUserScript(new NSString(source), WKUserScriptInjectionTime.AtDocumentStart,
                isForMainFrameOnly: false)));
    }

    public Task ClearBrowsingDataAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchQueue.MainQueue.DispatchAsync(() => WKWebsiteDataStore.DefaultDataStore.RemoveDataOfTypes(
            websiteDataTypes: WKWebsiteDataStore.AllWebsiteDataTypes,
            date: NSDate.FromTimeIntervalSince1970(0), // everything ever stored
            completionHandler: () => tcs.TrySetResult()));
        return tcs.Task;
    }

    public Task<string?> EvaluateJavaScriptAsync(string script)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchQueue.MainQueue.DispatchAsync(async () =>
        {
            if (_webView is not { } view)
            {
                tcs.TrySetResult(null);
                return;
            }

            try
            {
                NSObject? result = await view.EvaluateJavaScriptAsync(script);
                tcs.TrySetResult(result?.ToString());
            }
            catch (NSErrorException ex)
            {
                // The script threw, or produced a value WebKit cannot bridge — same null the
                // other backends answer.
                Log.Debug("Script evaluation failed: {Message}", ex.Message);
                tcs.TrySetResult(null);
            }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            _progressObserver?.Dispose();
            _progressObserver = null;
            if (_userContent is { } content)
            {
                content.RemoveScriptMessageHandler(WebViewBridge.HandlerName);
                content.RemoveAllUserScripts();
            }

            if (_webView is { } view)
            {
                view.NavigationDelegate = null;
                view.RemoveFromSuperview();
                view.Dispose();
            }

            _webView = null;
            _userContent?.Dispose();
            _userContent = null;
            _delegate?.Dispose();
            _delegate = null;
            _bridge?.Dispose();
            _bridge = null;
        });
    }

    /// <summary>Main queue → app thread, with the history state the controller tracks.</summary>
    private void PostHistory(WKWebView view)
    {
        bool back = view.CanGoBack;
        bool forward = view.CanGoForward;
        _post(() => _owner.OnHistoryChanged(back, forward));
    }

    /// <summary>The page's <c>window.webkit.messageHandlers.zigote</c>. Callbacks arrive on the
    ///     main queue; the controller's events belong on the app thread.</summary>
    private sealed class HostBridge(WkWebViewBackend backend) : NSObject, IWKScriptMessageHandler
    {
        public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
        {
            if (message.Body?.ToString() is not { } text) return;
            backend._post(() => backend._owner.OnMessageReceived(text));
        }
    }

    /// <summary>
    ///     Title is read in DidFinishNavigation rather than observed via KVO — one method instead
    ///     of observer lifecycle plumbing. The trade: an SPA that rewrites document.title without
    ///     navigating is missed; add KVO on "title" if that ever matters.
    /// </summary>
    private sealed class NavigationDelegate(WkWebViewBackend backend) : WKNavigationDelegate
    {
        /// <summary>The navigation filter, on the only hook WebKit offers for it. Runs the
        ///     owner's callback inline — the controller's contract is that it is cheap.</summary>
        public override void DecidePolicy(
            WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            string? url = navigationAction.Request?.Url?.AbsoluteString;
            bool allowed = url is null || backend._owner.AllowNavigation(url);
            decisionHandler(allowed ? WKNavigationActionPolicy.Allow : WKNavigationActionPolicy.Cancel);
        }

        public override void DidStartProvisionalNavigation(WKWebView webView, WKNavigation navigation)
        {
            string? url = webView.Url?.AbsoluteString;
            backend._post(() =>
            {
                backend._owner.OnLoadingChanged(true);
                if (url is not null) backend._owner.OnUrlChanged(url);
            });
            backend.PostHistory(webView);
        }

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
        {
            string? url = webView.Url?.AbsoluteString;
            string? title = webView.Title;
            backend._post(() =>
            {
                backend._owner.OnLoadingChanged(false);
                if (url is not null) backend._owner.OnUrlChanged(url);
                if (!string.IsNullOrEmpty(title)) backend._owner.OnTitleChanged(title);
            });
            backend.PostHistory(webView);
        }

        public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error) =>
            Fail(webView, error);

        public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error) =>
            Fail(webView, error);

        private void Fail(WKWebView webView, NSError error)
        {
            string url = webView.Url?.AbsoluteString ?? "";
            // -999 is NSURLErrorCancelled: what a filter's Cancel and a StopLoading both produce.
            // Neither is a failure the app should surface.
            string? message = error.Code == -999 ? null : error.LocalizedDescription;
            backend._post(() =>
            {
                if (message is not null) backend._owner.OnLoadFailed(url, message);
                backend._owner.OnLoadingChanged(false);
            });
        }
    }
}
