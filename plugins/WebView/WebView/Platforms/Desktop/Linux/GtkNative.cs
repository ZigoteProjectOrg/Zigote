using System.Runtime.InteropServices;

namespace WebView;

/// <summary>
///     The minimum of GTK3 / WebKitGTK 4.1 / GLib / Xlib this backend speaks, bound by soname so
///     no -devel packages are needed at runtime. webkit2gtk-4.1 is the GTK3 API line — GTK3
///     because a reparented toplevel is a well-trodden embedding there, where GTK4 removed the
///     per-widget X windows this technique rides on.
/// </summary>
internal static partial class GtkNative
{
    // Booleans here are C ints, not bytes: GLib's gboolean is a gint and Xlib's Bool is an int, so
    // every one of them is marshalled as UnmanagedType.Bool (4 bytes). UnmanagedType.I1 happens to
    // work for returns (the low byte of a 0/1 int) and happens to work for arguments (the JIT
    // zero-extends), which is exactly the kind of accident that stops being one on a new ABI.

    private const string Gtk = "libgtk-3.so.0";
    private const string Gdk = "libgdk-3.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string WebKit = "libwebkit2gtk-4.1.so.0";
    private const string Jsc = "libjavascriptcoregtk-4.1.so.0";
    private const string X11 = "libX11.so.6";
    private const string Cairo = "libcairo.so.2";

    // ── GTK ───────────────────────────────────────────────────────────────────

    [LibraryImport(Gtk)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool gtk_init_check(nint argc, nint argv);

    /// <summary>window_type 0 = GTK_WINDOW_TOPLEVEL, 1 = GTK_WINDOW_POPUP.</summary>
    [LibraryImport(Gtk)]
    internal static partial nint gtk_window_new(int window_type);

    [LibraryImport(Gtk)]
    internal static partial void gtk_window_set_default_size(nint window, int width, int height);

    [LibraryImport(Gtk)]
    internal static partial void gtk_window_resize(nint window, int width, int height);

    [LibraryImport(Gtk)]
    internal static partial void gtk_container_add(nint container, nint widget);

    [LibraryImport(Gtk)]
    internal static partial void gtk_widget_show_all(nint widget);

    [LibraryImport(Gtk)]
    internal static partial void gtk_widget_realize(nint widget);

    [LibraryImport(Gtk)]
    internal static partial void gtk_widget_destroy(nint widget);

    [LibraryImport(Gtk)]
    internal static partial nint gtk_widget_get_window(nint widget);

    [LibraryImport(Gdk)]
    internal static partial ulong gdk_x11_window_get_xid(nint gdk_window);

    /// <summary>GTK's own Xlib connection — X calls issued on it are ordered with GTK's, which
    ///     is what makes reparent-right-after-realize safe.</summary>
    [LibraryImport(Gdk)]
    internal static partial nint gdk_x11_get_default_xdisplay();

    // ── Offscreen rendering (the Wayland texture path) ───────────────────────

    [LibraryImport(Gtk)]
    internal static partial nint gtk_offscreen_window_new();

    /// <summary>A cairo surface owned by the window — flush before reading, never free.</summary>
    [LibraryImport(Gtk)]
    internal static partial nint gtk_offscreen_window_get_surface(nint window);

    [LibraryImport(Gtk)]
    internal static partial void gtk_widget_set_size_request(nint widget, int width, int height);

    [LibraryImport(Gtk)]
    internal static partial void gtk_widget_grab_focus(nint widget);

    [LibraryImport(Cairo)]
    internal static partial void cairo_surface_flush(nint surface);

    [LibraryImport(Cairo)]
    internal static partial nint cairo_image_surface_get_data(nint surface);

    [LibraryImport(Cairo)]
    internal static partial int cairo_image_surface_get_width(nint surface);

    [LibraryImport(Cairo)]
    internal static partial int cairo_image_surface_get_height(nint surface);

    [LibraryImport(Cairo)]
    internal static partial int cairo_image_surface_get_stride(nint surface);

    // ── Synthetic input (GDK events into the offscreen view) ─────────────────

    [LibraryImport(Gdk)]
    internal static partial nint gdk_event_new(int type);

    [LibraryImport(Gdk)]
    internal static partial void gdk_event_free(nint evt);

    [LibraryImport(Gdk)]
    internal static partial void gdk_event_set_device(nint evt, nint device);

    [LibraryImport(Gdk)]
    internal static partial nint gdk_display_get_default();

    [LibraryImport(Gdk)]
    internal static partial nint gdk_display_get_default_seat(nint display);

    [LibraryImport(Gdk)]
    internal static partial nint gdk_seat_get_pointer(nint seat);

    [LibraryImport(Gdk)]
    internal static partial nint gdk_seat_get_keyboard(nint seat);

    [LibraryImport(Gdk)]
    internal static partial uint gdk_unicode_to_keyval(uint wc);

    /// <summary>The cursor the page asked for, so the app can show the hand over a link and the
    ///     I-beam over text — WebKit sets it on the view's GdkWindow like any GTK widget.</summary>
    [LibraryImport(Gdk)]
    internal static partial nint gdk_window_get_cursor(nint window);

    [LibraryImport(Gdk, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint gdk_cursor_new_from_name(nint display, string name);

    /// <summary>Monotonic microseconds — GdkEvent.time is milliseconds of the same clock, and a
    ///     page that measures gesture velocity reads it as event.timeStamp.</summary>
    [LibraryImport(GLib)]
    internal static partial long g_get_monotonic_time();

    [LibraryImport(Gtk)]
    internal static partial void gtk_main_do_event(nint evt);

    [LibraryImport(GObject)]
    internal static partial nint g_object_ref(nint obj);

    // ── GLib main loop / dispatch ────────────────────────────────────────────

    /// <summary>interval ms; the callback returns 1 to keep firing, 0 to remove.</summary>
    [LibraryImport(GLib)]
    internal static partial uint g_timeout_add(uint interval, nint function, nint data);

    [LibraryImport(GLib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool g_source_remove(uint tag);

    /// <summary>A private main context for WebKit's sources, so the host's own loop (SDL's
    ///     libdecor plugin iterates the DEFAULT context every frame) never dispatches them.</summary>
    [LibraryImport(GLib)]
    internal static partial nint g_main_context_new();

    /// <summary>Sources attach to the thread-default context at creation, so every call into
    ///     GTK/WebKit runs inside a push/pop pair — see GtkThread.Run.</summary>
    [LibraryImport(GLib)]
    internal static partial void g_main_context_push_thread_default(nint context);

    [LibraryImport(GLib)]
    internal static partial void g_main_context_pop_thread_default(nint context);

    /// <summary>g_timeout_add attaches to the GLOBAL default context, never the thread-default —
    ///     a timeout that must live on the private context is built and attached by hand.</summary>
    [LibraryImport(GLib)]
    internal static partial nint g_timeout_source_new(uint interval);

    [LibraryImport(GLib)]
    internal static partial void g_source_set_callback(nint source, nint func, nint data, nint notify);

    [LibraryImport(GLib)]
    internal static partial uint g_source_attach(nint source, nint context);

    [LibraryImport(GLib)]
    internal static partial void g_source_destroy(nint source);

    [LibraryImport(GLib)]
    internal static partial void g_source_unref(nint source);

    [LibraryImport(GLib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool g_main_context_iteration(nint context, [MarshalAs(UnmanagedType.Bool)] bool may_block);

    [LibraryImport(GLib)]
    internal static partial void g_main_context_wakeup(nint context);

    /// <summary>Claim a context for this thread. Fails if another thread already owns it, which is
    ///     how the GTK thread checks that nothing else drives GLib's default context.</summary>
    [LibraryImport(GLib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool g_main_context_acquire(nint context);

    /// <summary>Run a callback on whichever thread owns <paramref name="context" /> — the hop the
    ///     UI thread takes to reach the GTK thread. Runs inline when the caller already owns it.</summary>
    [LibraryImport(GLib)]
    internal static partial void g_main_context_invoke_full(
        nint context, int priority, nint function, nint data, nint notify);

    [LibraryImport(GLib)]
    internal static partial void g_free(nint mem);

    // ── GObject signals ──────────────────────────────────────────────────────

    [LibraryImport(GObject, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ulong g_signal_connect_data(
        nint instance, string detailed_signal, nint handler, nint data, nint destroy_data, uint flags);

    [LibraryImport(GObject)]
    internal static partial void g_object_unref(nint obj);

    // ── WebKit ───────────────────────────────────────────────────────────────

    [LibraryImport(WebKit)]
    internal static partial nint webkit_web_view_new();

    /// <summary>The process-wide context every view shares unless told otherwise: caches, cookies,
    ///     favicons, the web-process pool.</summary>
    [LibraryImport(WebKit)]
    internal static partial nint webkit_web_context_get_default();

    [LibraryImport(WebKit)]
    internal static partial nint webkit_web_context_get_website_data_manager(nint context);

    [LibraryImport(WebKit)]
    internal static partial nint webkit_website_data_manager_get_cookie_manager(nint manager);

    /// <summary>storage: 0 = text (cookies.txt), 1 = sqlite. Without this a browser forgets every
    ///     login the moment it exits — the cookie jar is memory-only by default.</summary>
    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_cookie_manager_set_persistent_storage(
        nint cookie_manager, string filename, int storage);

    /// <summary>Where favicons are cached. Setting it is what turns the favicon database on at
    ///     all — tabs have no icons until a directory exists.</summary>
    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_web_context_set_favicon_database_directory(
        nint context, string? directory);

    /// <summary>0 = document viewer, 1 = web browser, 2 = document browser. The default is already
    ///     the browser model on 2.52; setting it makes that independent of the WebKit version.</summary>
    [LibraryImport(WebKit)]
    internal static partial void webkit_web_context_set_cache_model(nint context, int model);

    [LibraryImport(WebKit)]
    internal static partial nint webkit_web_view_get_settings(nint web_view);

    /// <summary>Appends the app's name and version to the platform user agent instead of replacing
    ///     it — what a browser does, and what keeps sites' feature detection working.</summary>
    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_settings_set_user_agent_with_application_details(
        nint settings, string? name, string? version);

    /// <summary>policy: 0 = ON_DEMAND, 1 = ALWAYS, 2 = NEVER.</summary>
    [LibraryImport(WebKit)]
    internal static partial void webkit_settings_set_hardware_acceleration_policy(nint settings, int policy);

    /// <summary>WebKit's own easing for wheel/trackpad scrolling. On by default, and wrong for a
    ///     stream of precise trackpad deltas: the page then keeps sliding ~220 ms after the fingers
    ///     lift, which reads as the view swimming behind the gesture.</summary>
    [LibraryImport(WebKit)]
    internal static partial void webkit_settings_set_enable_smooth_scrolling(nint settings,
        [MarshalAs(UnmanagedType.Bool)] bool enabled);

    /// <summary>WebGL needs the accelerated compositor; asking for it on the software path is how
    ///     a page ends up with a black canvas instead of a clean "no WebGL" fallback.</summary>
    [LibraryImport(WebKit)]
    internal static partial void webkit_settings_set_enable_webgl(nint settings,
        [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [LibraryImport(WebKit)]
    internal static partial void webkit_web_view_set_zoom_level(nint web_view, double zoom);

    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_web_view_load_uri(nint web_view, string uri);

    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_web_view_load_html(nint web_view, string content, string? base_uri);

    [LibraryImport(WebKit)]
    internal static partial void webkit_web_view_go_back(nint web_view);

    [LibraryImport(WebKit)]
    internal static partial void webkit_web_view_go_forward(nint web_view);

    [LibraryImport(WebKit)]
    internal static partial void webkit_web_view_reload(nint web_view);

    [LibraryImport(WebKit)]
    internal static partial void webkit_web_view_stop_loading(nint web_view);

    [LibraryImport(WebKit)]
    internal static partial nint webkit_web_view_get_uri(nint web_view);

    [LibraryImport(WebKit)]
    internal static partial nint webkit_web_view_get_title(nint web_view);

    [LibraryImport(WebKit)]
    internal static partial void webkit_settings_set_enable_javascript(nint settings,
        [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [LibraryImport(WebKit)]
    internal static partial void webkit_settings_set_enable_developer_extras(nint settings,
        [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [LibraryImport(WebKit)]
    internal static partial void webkit_settings_set_media_playback_requires_user_gesture(nint settings,
        [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_settings_set_user_agent(nint settings, string? user_agent);

    [LibraryImport(WebKit)]
    internal static partial double webkit_web_view_get_estimated_load_progress(nint web_view);

    [LibraryImport(WebKit)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool webkit_web_view_can_go_back(nint web_view);

    [LibraryImport(WebKit)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool webkit_web_view_can_go_forward(nint web_view);

    // ── User content: the message bridge and document-start scripts ──────────

    [LibraryImport(WebKit)]
    internal static partial nint webkit_user_content_manager_new();

    /// <summary>The view must be BUILT with the manager — it cannot be swapped in later.</summary>
    [LibraryImport(WebKit)]
    internal static partial nint webkit_web_view_new_with_user_content_manager(nint manager);

    /// <summary>Turns on <c>window.webkit.messageHandlers.NAME</c> and the manager's
    ///     <c>script-message-received::NAME</c> signal.</summary>
    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool webkit_user_content_manager_register_script_message_handler(
        nint manager, string name);

    [LibraryImport(WebKit)]
    internal static partial void webkit_user_content_manager_add_script(nint manager, nint script);

    /// <summary>injected_frames: 0 = ALL_FRAMES, 1 = TOP_FRAME. injection_time: 0 = START, 1 = END.</summary>
    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint webkit_user_script_new(
        string source, int injected_frames, int injection_time, nint allow_list, nint block_list);

    [LibraryImport(WebKit)]
    internal static partial void webkit_user_script_unref(nint script);

    /// <summary>API 4.1 hands the signal a WebKitJavascriptResult*; the JSCValue inside is
    ///     borrowed (valid for the callback, do not unref).</summary>
    [LibraryImport(WebKit)]
    internal static partial nint webkit_javascript_result_get_js_value(nint js_result);

    // ── Navigation policy ────────────────────────────────────────────────────

    [LibraryImport(WebKit)]
    internal static partial nint webkit_navigation_policy_decision_get_navigation_action(nint decision);

    [LibraryImport(WebKit)]
    internal static partial nint webkit_navigation_action_get_request(nint action);

    [LibraryImport(WebKit)]
    internal static partial nint webkit_uri_request_get_uri(nint request);

    [LibraryImport(WebKit)]
    internal static partial void webkit_policy_decision_use(nint decision);

    [LibraryImport(WebKit)]
    internal static partial void webkit_policy_decision_ignore(nint decision);

    // ── Website data (cookies, caches, storage) ──────────────────────────────

    [LibraryImport(WebKit)]
    internal static partial nint webkit_web_view_get_website_data_manager(nint web_view);

    /// <summary>types is a WebKitWebsiteDataTypes bitmask; timespan 0 = everything ever stored.</summary>
    [LibraryImport(WebKit)]
    internal static partial void webkit_website_data_manager_clear(
        nint manager, uint types, long timespan, nint cancellable, nint callback, nint user_data);

    [LibraryImport(WebKit)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool webkit_website_data_manager_clear_finish(
        nint manager, nint result, out nint error);

    [LibraryImport(WebKit, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void webkit_web_view_evaluate_javascript(
        nint web_view, string script, nint length, string? world_name, string? source_uri,
        nint cancellable, nint callback, nint user_data);

    /// <summary>Returns a JSCValue* (owned, unref) or null; error out-param is a GError**.</summary>
    [LibraryImport(WebKit)]
    internal static partial nint webkit_web_view_evaluate_javascript_finish(
        nint web_view, nint result, out nint error);

    [LibraryImport(Jsc)]
    internal static partial nint jsc_value_to_string(nint value); // g_free the result

    [LibraryImport(GLib)]
    internal static partial void g_error_free(nint error);

    // ── Xlib (a private connection; window ids are global to the display) ─────

    [LibraryImport(X11)]
    internal static partial int XReparentWindow(nint display, ulong window, ulong parent, int x, int y);

    [LibraryImport(X11)]
    internal static partial int XMoveResizeWindow(nint display, ulong window, int x, int y, uint width, uint height);

    [LibraryImport(X11)]
    internal static partial int XMapWindow(nint display, ulong window);

    [LibraryImport(X11)]
    internal static partial int XUnmapWindow(nint display, ulong window);

    [LibraryImport(X11)]
    internal static partial int XFlush(nint display);

    /// <summary>revert_to 2 = RevertToParent; time 0 = CurrentTime.</summary>
    [LibraryImport(X11)]
    internal static partial int XSetInputFocus(nint display, ulong focus, int revert_to, ulong time);

    [LibraryImport(X11)]
    internal static partial int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);

    /// <summary>Xlib's default handler EXITS the process on any async error — a BadWindow from a
    ///     failed embed must log instead. Returns the previous handler.</summary>
    [LibraryImport(X11)]
    internal static partial nint XSetErrorHandler(nint handler);
}
