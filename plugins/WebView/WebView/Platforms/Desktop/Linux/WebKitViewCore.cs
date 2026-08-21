using System.Runtime.InteropServices;
using Serilog;
using static WebView.GtkNative;

namespace WebView;

/// <summary>
///     The part both Linux backends share: one WebKitWebView — its settings, its user content
///     manager (the host message bridge and document-start scripts), its page-event signals,
///     navigation policy, and JS evaluation — independent of how it reaches the screen (the X11
///     overlay window or the Wayland offscreen texture). All methods marshal themselves onto the
///     GTK thread; page events cross back through <c>post</c>.
/// </summary>
internal sealed unsafe class WebKitViewCore : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WebKitViewCore>();

    /// <summary>Every WebKitWebsiteDataTypes bit — cookies, caches, storage, service workers.
    ///     More bits than the enum defines today, which is the point: a new data type must not
    ///     silently survive a "clear everything".</summary>
    private const uint AllWebsiteData = 0xFFFF;

    /// <summary>WebKitHardwareAccelerationPolicy, webkit2gtk-4.1: ON_DEMAND 0, ALWAYS 1, NEVER 2.
    ///     Named because webkitgtk-6.0 renumbers them (ALWAYS 0, NEVER 1) — a port that keeps the
    ///     literals would silently rasterize on the CPU.</summary>
    private const int AccelerationAlways = 1;

    private const int AccelerationNever = 2;

    private readonly WebViewController _owner;
    private readonly Action<Action> _post;
    private GCHandle _self;
    private nint _userContent;
    private bool _accelerated;
    private static bool _configured;

    public WebKitViewCore(WebViewController owner, Action<Action> post)
    {
        _owner = owner;
        _post = post;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>The WebKitWebView*, valid on the GTK thread after <see cref="CreateView" />.</summary>
    public nint View { get; private set; }

    /// <summary>
    ///     Process-wide WebKit setup, once, on the GTK thread before the first view exists.
    ///     <para>
    ///         The extra reference is not a leak, it is the fix for one: GObject finalizes the
    ///         default context at process exit, from a C++ static destructor running on the process
    ///         main thread — and <c>WebsiteDataStore</c>'s destructor asserts it is on WebKit's main
    ///         thread, which is the GTK thread when we own one. That abort (verified in the core
    ///         dump: <c>~WebsiteDataStore</c> → <c>WTFCrashWithInfo</c>) is avoided by keeping the
    ///         context alive past exit, which costs nothing: the process is going away with it.
    ///     </para>
    ///     <para>
    ///         The rest is what separates a browser from a webview: a cookie jar and a favicon
    ///         database that survive a restart, both under the app's own data directory.
    ///     </para>
    /// </summary>
    private static void ConfigureProcessWide()
    {
        if (_configured) return;
        _configured = true;

        nint context = webkit_web_context_get_default();
        g_object_ref(context);
        webkit_web_context_set_cache_model(context, 1 /* WEBKIT_CACHE_MODEL_WEB_BROWSER */);

        string profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "zigote-webview");
        try
        {
            Directory.CreateDirectory(profile);
            webkit_web_context_set_favicon_database_directory(context, Path.Combine(profile, "icons"));
            nint cookies = webkit_website_data_manager_get_cookie_manager(
                webkit_web_context_get_website_data_manager(context));
            if (cookies != 0)
                webkit_cookie_manager_set_persistent_storage(cookies,
                    Path.Combine(profile, "cookies.sqlite"), 1 /* SQLITE */);
            Log.Debug("WebKit profile at {Profile} (persistent cookies, favicon database)", profile);
        }
        catch (IOException ex)
        {
            Log.Debug(ex, "no writable profile directory — cookies and favicons stay in memory");
        }
    }

    /// <summary>GTK thread: create the view inside <paramref name="container" /> (a GtkWindow).</summary>
    /// <param name="accelerated">
    ///     Whether this embedding can give WebKit a GL surface. True for a real GdkWindow (the X11
    ///     overlay), false for a <c>GtkOffscreenWindow</c> — GDK cannot create a GL context for one,
    ///     and WebKit's compositor <c>g_error</c>s (aborts the process) when it asks and is refused.
    /// </param>
    public void CreateView(nint container, bool accelerated)
    {
        _accelerated = accelerated;
        ConfigureProcessWide();
        // The content manager must exist before the view: WebKit takes it at construction and
        // there is no setter. It carries the message handler and every user script.
        _userContent = webkit_user_content_manager_new();
        webkit_user_content_manager_register_script_message_handler(_userContent, WebViewBridge.HandlerName);
        View = webkit_web_view_new_with_user_content_manager(_userContent);

        ApplySettings();
        gtk_container_add(container, View);

        nint data = GCHandle.ToIntPtr(_self);
        g_signal_connect_data(_userContent, $"script-message-received::{WebViewBridge.HandlerName}",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnScriptMessage, data, 0, 0);
        g_signal_connect_data(View, "load-changed",
            (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnLoadChanged, data, 0, 0);
        g_signal_connect_data(View, "load-failed",
            (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint, int>)&OnLoadFailed, data, 0, 0);
        g_signal_connect_data(View, "web-process-terminated",
            (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnWebProcessTerminated, data, 0, 0);
        g_signal_connect_data(View, "decide-policy",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, int>)&OnDecidePolicy, data, 0, 0);
        g_signal_connect_data(View, "notify::title",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnTitleNotify, data, 0, 0);
        g_signal_connect_data(View, "notify::uri",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnUriNotify, data, 0, 0);
        // WebKit's own context menu is a GTK menu in a popup window: offscreen it has nowhere to
        // appear, and in overlay mode it would sit outside the app's own chrome. Suppressed here —
        // right-click still reaches the page, so a web app's own menu works, and a browser shell
        // draws its own. Returning TRUE is what prevents the default.
        g_signal_connect_data(View, "context-menu",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, int>)&OnContextMenu, data, 0, 0);
        g_signal_connect_data(View, "notify::estimated-load-progress",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnProgressNotify, data, 0, 0);

        // The bridge shim first, so a user script added before mount can already use it.
        AddUserScript(WebViewBridge.Shim($"window.webkit.messageHandlers.{WebViewBridge.HandlerName}.postMessage.bind(window.webkit.messageHandlers.{WebViewBridge.HandlerName})"));
    }

    /// <summary>GTK thread, once at creation — <see cref="WebViewSettings" /> is immutable.</summary>
    private void ApplySettings()
    {
        var settings = _owner.Settings;
        nint s = webkit_web_view_get_settings(View);
        // ALWAYS where the embedding has a real GdkWindow to hang a GL context on, and this is
        // what a browser is made of: WebGL, GPU-decoded video, composited CSS transforms and
        // filters all need WebKit's accelerated compositor. NEVER on the offscreen texture path,
        // where GDK has no GL context to give and WebKit aborts the process rather than fall back.
        bool accelerate = _accelerated && settings.HardwareAcceleration;
        webkit_settings_set_hardware_acceleration_policy(s, accelerate ? AccelerationAlways : AccelerationNever);
        webkit_settings_set_enable_webgl(s, accelerate);
        webkit_settings_set_enable_javascript(s, settings.JavaScriptEnabled);
        webkit_settings_set_enable_developer_extras(s, settings.DevToolsEnabled);
        webkit_settings_set_media_playback_requires_user_gesture(s, !settings.AllowAutoplay);
        if (settings.UserAgent is { Length: > 0 } agent)
        {
            webkit_settings_set_user_agent(s, agent);
        }
        else
        {
            // Appended to the platform UA rather than replacing it: a site's feature detection
            // still sees a WebKit browser, and its logs still see which app is embedding it.
            var app = System.Reflection.Assembly.GetEntryAssembly()?.GetName();
            webkit_settings_set_user_agent_with_application_details(s,
                app?.Name ?? "Zigote", app?.Version?.ToString(3));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnContextMenu(nint view, nint menu, nint evt, nint hitTest, nint data) => 1;

    /// <summary>Injected at document-start of every page from now on. Safe before
    ///     <see cref="CreateView" /> only through the owner, which replays its list on attach.</summary>
    public void AddUserScript(string source) => GtkThread.Run(() =>
    {
        if (_userContent == 0) return;
        // ALL_FRAMES + injection at START: an iframed payment widget needs the bridge too, and
        // "before the page's own scripts" is the only moment a shim is worth anything.
        nint script = webkit_user_script_new(source, injected_frames: 0, injection_time: 0, 0, 0);
        webkit_user_content_manager_add_script(_userContent, script);
        webkit_user_script_unref(script);
    });

    public void Navigate(string url) => GtkThread.Run(() =>
    {
        if (View != 0) webkit_web_view_load_uri(View, url);
    });

    public void LoadHtml(string html, string? baseUrl) => GtkThread.Run(() =>
    {
        if (View != 0) webkit_web_view_load_html(View, html, baseUrl);
    });

    public void GoBack() => GtkThread.Run(() =>
    {
        if (View != 0) webkit_web_view_go_back(View);
    });

    public void GoForward() => GtkThread.Run(() =>
    {
        if (View != 0) webkit_web_view_go_forward(View);
    });

    public void Reload() => GtkThread.Run(() =>
    {
        if (View != 0) webkit_web_view_reload(View);
    });

    public void StopLoading() => GtkThread.Run(() =>
    {
        if (View != 0) webkit_web_view_stop_loading(View);
    });

    public Task<string?> EvaluateJavaScriptAsync(string script)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        GtkThread.Run(() =>
        {
            if (View == 0)
            {
                tcs.TrySetResult(null);
                return;
            }

            var handle = GCHandle.Alloc(tcs);
            webkit_web_view_evaluate_javascript(
                web_view: View,
                script: script,
                length: -1,
                world_name: null,
                source_uri: null,
                cancellable: 0,
                callback: (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnJsDone,
                user_data: GCHandle.ToIntPtr(handle)
            );
        });
        return tcs.Task;
    }

    public Task ClearBrowsingDataAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GtkThread.Run(() =>
        {
            nint manager = View == 0 ? 0 : webkit_web_view_get_website_data_manager(View);
            if (manager == 0)
            {
                tcs.TrySetResult();
                return;
            }

            var handle = GCHandle.Alloc(tcs);
            webkit_website_data_manager_clear(
                manager: manager,
                types: AllWebsiteData,
                timespan: 0, // everything ever stored
                cancellable: 0,
                callback: (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnClearDone,
                user_data: GCHandle.ToIntPtr(handle)
            );
        });
        return tcs.Task;
    }

    /// <summary>GTK thread. The container window's destroy takes the view with it.</summary>
    public void Dispose()
    {
        View = 0;
        if (_userContent != 0) g_object_unref(_userContent);
        _userContent = 0;
        if (_self.IsAllocated) _self.Free();
        _self = default;
    }

    private static WebKitViewCore? FromData(nint data)
    {
        return GCHandle.FromIntPtr(data).Target as WebKitViewCore;
    }

    /// <summary>Every page-state signal ends here: state the owner tracks per navigation step.</summary>
    private void PostHistory()
    {
        if (View == 0) return;
        bool back = webkit_web_view_can_go_back(View);
        bool forward = webkit_web_view_can_go_forward(View);
        _post(() => _owner.OnHistoryChanged(back, forward));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnLoadChanged(nint webView, int loadEvent, nint data)
    {
        if (FromData(data) is not { } self) return;
        Log.Verbose("load-changed {Event} {Uri}", loadEvent,
            Marshal.PtrToStringUTF8(webkit_web_view_get_uri(webView)));
        // WEBKIT_LOAD_STARTED = 0 … WEBKIT_LOAD_FINISHED = 3.
        if (loadEvent == 0) self._post(() => self._owner.OnLoadingChanged(true));
        else if (loadEvent == 3) self._post(() => self._owner.OnLoadingChanged(false));
        self.PostHistory();
    }

    /// <summary>gboolean load-failed(view, load_event, failing_uri, GError*, user_data).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnLoadFailed(nint webView, int loadEvent, nint failingUri, nint error, nint data)
    {
        string uri = Marshal.PtrToStringUTF8(failingUri) ?? "";
        string message = GErrorMessage(error);
        if (FromData(data) is { } self)
            self._post(() =>
            {
                self._owner.OnLoadFailed(uri, message);
                self._owner.OnLoadingChanged(false);
            });
        return 0; // let WebKit show its own error page too
    }

    /// <summary>reason: 0 = crashed, 1 = exceeded memory, 2 = terminated by API.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnWebProcessTerminated(nint webView, int reason, nint data)
    {
        if (FromData(data) is not { } self) return;
        string uri = Marshal.PtrToStringUTF8(webkit_web_view_get_uri(webView)) ?? "";
        self._post(() =>
        {
            self._owner.OnLoadFailed(uri, $"the web process terminated (reason {reason})");
            self._owner.OnLoadingChanged(false);
        });
    }

    /// <summary>
    ///     decide-policy(view, decision, decision_type, user_data). Type 0 = NAVIGATION_ACTION,
    ///     1 = NEW_WINDOW_ACTION, 2 = RESPONSE. Only real navigations go past the filter; a
    ///     new-window request (target=_blank) is offered to the filter too and, when allowed,
    ///     loaded in this same view rather than a popup WebKit would never show anyway.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnDecidePolicy(nint webView, nint decision, int decisionType, nint data)
    {
        if (decisionType is not (0 or 1)) return 0;
        if (FromData(data) is not { } self) return 0;

        nint action = webkit_navigation_policy_decision_get_navigation_action(decision);
        nint request = action == 0 ? 0 : webkit_navigation_action_get_request(action);
        string? uri = request == 0 ? null : Marshal.PtrToStringUTF8(webkit_uri_request_get_uri(request));
        if (uri is null) return 0;

        if (!self._owner.AllowNavigation(uri))
        {
            webkit_policy_decision_ignore(decision);
            return 1; // handled — WebKit must not apply its default policy on top
        }

        if (decisionType == 1)
        {
            webkit_policy_decision_ignore(decision);
            webkit_web_view_load_uri(webView, uri);
            return 1;
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnTitleNotify(nint webView, nint pspec, nint data)
    {
        if (FromData(data) is not { } self) return;
        string? title = Marshal.PtrToStringUTF8(webkit_web_view_get_title(webView));
        if (title is not null) self._post(() => self._owner.OnTitleChanged(title));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnUriNotify(nint webView, nint pspec, nint data)
    {
        if (FromData(data) is not { } self) return;
        string? uri = Marshal.PtrToStringUTF8(webkit_web_view_get_uri(webView));
        if (uri is not null) self._post(() => self._owner.OnUrlChanged(uri));
        self.PostHistory();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnProgressNotify(nint webView, nint pspec, nint data)
    {
        if (FromData(data) is not { } self) return;
        double progress = webkit_web_view_get_estimated_load_progress(webView);
        self._post(() => self._owner.OnProgressChanged(progress));
    }

    /// <summary>The page called <c>window.zigote.postMessage</c>. API 4.1 hands a
    ///     WebKitJavascriptResult*; the JSCValue inside is borrowed.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnScriptMessage(nint manager, nint jsResult, nint data)
    {
        if (FromData(data) is not { } self) return;
        nint value = webkit_javascript_result_get_js_value(jsResult);
        if (value == 0) return;
        nint str = jsc_value_to_string(value);
        string? text = Marshal.PtrToStringUTF8(str);
        if (str != 0) g_free(str);
        if (text is not null) self._post(() => self._owner.OnMessageReceived(text));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnJsDone(nint webView, nint result, nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        var tcs = (TaskCompletionSource<string?>)handle.Target!;
        handle.Free();

        nint value = webkit_web_view_evaluate_javascript_finish(webView, result, out nint error);
        if (value == 0)
        {
            if (error != 0)
            {
                Log.Debug("Script evaluation failed: {Message}", GErrorMessage(error));
                g_error_free(error);
            }

            tcs.TrySetResult(null);
            return;
        }

        nint str = jsc_value_to_string(value);
        string? text = Marshal.PtrToStringUTF8(str);
        if (str != 0) g_free(str);
        g_object_unref(value);
        tcs.TrySetResult(text);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnClearDone(nint manager, nint result, nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        var tcs = (TaskCompletionSource)handle.Target!;
        handle.Free();
        if (!webkit_website_data_manager_clear_finish(manager, result, out nint error) && error != 0)
        {
            Log.Warning("Clearing website data failed: {Message}", GErrorMessage(error));
            g_error_free(error);
        }

        tcs.TrySetResult();
    }

    private static string GErrorMessage(nint error)
    {
        // GError layout: GQuark domain (u32), gint code (i32), gchar* message.
        if (error == 0) return "unknown";
        nint message = Marshal.ReadIntPtr(error, 8);
        return Marshal.PtrToStringUTF8(message) ?? "unknown";
    }
}
