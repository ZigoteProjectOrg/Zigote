using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using Zigote.Core.Animation;
using Zigote.Core;
using Zigote.Core.Engine;
using static WebView.GtkNative;

namespace WebView;

/// <summary>
///     Linux X11 overlay backend: the shared <see cref="WebKitViewCore" /> inside a GTK popup
///     window, reparented (XReparentWindow) into the engine's X11 window and moved with the
///     widget. Used when the engine runs under X11/XWayland (see
///     <see cref="WebViewController.EnsureEmbeddableVideoDriver" />); on native Wayland the
///     texture backend (<see cref="WebKitOffscreenBackend" />) takes over instead.
///     <para>
///         Known ceiling: keyboard focus is click-to-focus between the SDL window and the
///         embedded window, and the page composites OVER engine content.
///     </para>
/// </summary>
internal sealed unsafe class WebKitGtkBackend : IWebViewBackend
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WebKitGtkBackend>();

    private readonly WebViewController _owner;
    private readonly WebKitViewCore _core;
    private GCHandle _self;

    // UI-thread state (as is everything here -- see GtkThread).
    private nint _window;
    private ulong _xid;
    private ulong _parent;
    private (int X, int Y, uint W, uint H)? _bounds;
    private bool _visible;
    private bool _mapped;

    /// <summary>GTK's Xlib connection — X calls issued on it are ordered with GTK's own window
    ///     requests, which is what makes reparent-right-after-realize safe.</summary>
    private static nint _xDisplay;

    private WebKitGtkBackend(WebViewController owner, Action<Action> post)
    {
        _owner = owner;
        _core = new WebKitViewCore(owner, post);
        _self = GCHandle.Alloc(this);
    }

    public static WebKitGtkBackend? TryCreate(WebViewController owner)
    {
        if (!GtkThread.Start())
        {
            owner.LastError = "GTK could not initialize (no display reachable?)";
            return null;
        }

        // Same-thread by design: GLib sources fire inside the UI thread's pump, so page
        // events are already where widgets live.
        GtkThread.AddPumpClient();
        return new WebKitGtkBackend(owner, post: static a => a());
    }

    public void Attach(NativeParent parent)
    {
        if (parent.Kind != NativeParentKind.X11)
        {
            _owner.LastError = $"the X11 overlay backend needs an X11 window, got {parent.Kind}";
            return;
        }

        _parent = (ulong)parent.Ptr2;
        GtkThread.Run(() => CreateView());
    }

    public void SetBounds(Rect windowRect, float scale)
    {
        var px = (
            X: (int)MathF.Round(windowRect.X * scale),
            Y: (int)MathF.Round(windowRect.Y * scale),
            W: (uint)Math.Max(1, (int)MathF.Round(windowRect.Width * scale)),
            H: (uint)Math.Max(1, (int)MathF.Round(windowRect.Height * scale))
        );
        GtkThread.Run(() =>
        {
            _bounds = px;
            if (_xid == 0) return;
            XMoveResizeWindow(_xDisplay, _xid, px.X, px.Y, px.W, px.H);
            SyncMapped();
            XFlush(_xDisplay);
        });
    }

    public void SetVisible(bool visible)
    {
        GtkThread.Run(() =>
        {
            _visible = visible;
            if (_xid == 0) return;
            SyncMapped();
            XFlush(_xDisplay);
        });
    }

    public void Navigate(string url) => _core.Navigate(url);

    public void LoadHtml(string html, string? baseUrl) => _core.LoadHtml(html, baseUrl);

    public void GoBack() => _core.GoBack();

    public void GoForward() => _core.GoForward();

    public void Reload() => _core.Reload();

    public void StopLoading() => _core.StopLoading();

    public Task<string?> EvaluateJavaScriptAsync(string script) => _core.EvaluateJavaScriptAsync(script);

    public void AddUserScript(string source) => _core.AddUserScript(source);

    public Task ClearBrowsingDataAsync() => _core.ClearBrowsingDataAsync();

    public void Dispose()
    {
        var self = _self;
        GtkThread.Run(() =>
        {
            if (_window != 0) gtk_widget_destroy(_window);
            _window = 0;
            _xid = 0;
            _core.Dispose();
            if (self.IsAllocated) self.Free();
        });
        _self = default;
        GtkThread.RemovePumpClient();
    }

    // ── GTK thread ────────────────────────────────────────────────────────────

    private void CreateView()
    {
        if (_xDisplay == 0)
        {
            _xDisplay = gdk_x11_get_default_xdisplay();
            if (_xDisplay == 0)
            {
                Log.Error("GDK has no X11 display — is GDK_BACKEND=x11 set?");
                return;
            }

            // Xlib's default handler prints one line and EXITS the process; an async BadWindow
            // from a torn-down embed must never do that.
            XSetErrorHandler((nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&OnXError);
        }

        // A POPUP window is override-redirect: the window manager never decorates or moves it,
        // which is exactly right for a window whose real manager is our layout pass.
        _window = gtk_window_new(1 /* GTK_WINDOW_POPUP */);
        gtk_window_set_default_size(_window, 640, 480);
        _core.CreateView(_window);
        // Clicking the page pulls X keyboard focus to the embedded window so typing reaches
        // WebKit; clicking Zigote content hands it back to the SDL window naturally.
        g_signal_connect_data(_core.View, "button-press-event",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnButtonPress,
            GCHandle.ToIntPtr(_self), 0, 0);

        gtk_widget_realize(_window);
        nint gdkWindow = gtk_widget_get_window(_window);
        if (gdkWindow == 0)
        {
            Log.Error("GTK window did not realize — no X11 backend?");
            return;
        }

        _xid = gdk_x11_window_get_xid(gdkWindow);
        // On GTK's OWN display connection: requests are ordered with the CreateWindow the realize
        // just issued, so the reparent can never race it to the server. (A private connection
        // could arrive first — BadWindow, and a silently failed embed.)
        XReparentWindow(_xDisplay, _xid, _parent, 0, 0);
        gtk_widget_show_all(_window);
        _mapped = true;

        if (_bounds is { } b) XMoveResizeWindow(_xDisplay, _xid, b.X, b.Y, b.W, b.H);
        SyncMapped();
        XSync(_xDisplay, false);
        Log.Debug("Embedded X window 0x{Xid:x} into 0x{Parent:x}", _xid, _parent);
    }

    /// <summary>Map exactly when the host says visible AND we have somewhere to be.</summary>
    private void SyncMapped()
    {
        bool want = _visible && _bounds is not null;
        if (want == _mapped) return;
        _mapped = want;
        if (want) XMapWindow(_xDisplay, _xid);
        else XUnmapWindow(_xDisplay, _xid);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnButtonPress(nint widget, nint gdkEvent, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is WebKitGtkBackend { _xid: not 0 } self)
            XSetInputFocus(_xDisplay, self._xid, 2 /* RevertToParent */, 0 /* CurrentTime */);
        return 0; // propagate — WebKit still handles the click
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnXError(nint display, nint errorEvent)
    {
        // XErrorEvent: type(i32), display*, resourceid, serial, error_code(u8) at offset 8+3*ptr.
        byte code = Marshal.ReadByte(errorEvent, 8 + (3 * nint.Size));
        Log.Debug("X error code {Code} (embed race or dead window) — ignored", code);
        return 0;
    }
}

/// <summary>
///     GTK on the UI thread — deliberately NOT a thread of its own. SDL's Wayland backend loads
///     libdecor's GTK plugin, which initializes GTK in-process and iterates the default
///     GMainContext from the engine's event pump; a second thread iterating that same context
///     dispatches WebKit's sources on the wrong thread and crashes inside WebKit. So this
///     process has exactly one GTK "thread": the UI thread, where every backend call already
///     arrives. <see cref="Run" /> is the shim every such call goes through.
///     <para>
///         <b>WebKit gets a PRIVATE main context.</b> Measured on a Wayland session: with
///         WebKit's sources on the default context, libdecor's own iteration inside
///         <c>SDL_PollEvent</c> dispatches them — and does it far more expensively than a bounded
///         non-blocking drain (3.8 ms/frame vs 0.8 ms for the same work, ~18% of a 60 fps budget).
///         A private context keeps libdecor away from them: sources attach to the THREAD-DEFAULT
///         context at creation, so <see cref="Run" /> pushes it around every call into
///         GTK/WebKit, and <see cref="Pump" /> is then the only thing that ever dispatches them.
///     </para>
///     <para>
///         Pumping: a <see cref="Ticker" /> drives <see cref="Pump" /> once per frame, on the UI
///         thread, on the engine's own clock. Engine-less tests call <see cref="Pump" /> directly.
///     </para>
/// </summary>
internal static class GtkThread
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(GtkThread));

    /// <summary>
    ///     How long one <see cref="Pump" /> may spend dispatching before it hands the frame back.
    ///     A page that floods its main loop (a busy load, a runaway timer) must not be able to
    ///     hold the UI thread past the frame budget — the leftover work is simply dispatched on
    ///     the next pump.
    ///     <para>ponytail: a flat half-frame budget; make it adaptive only if a real page is
    ///     measurably starved by it.</para>
    /// </summary>
    private static readonly long PumpBudgetTicks = Stopwatch.Frequency / 120;

    private static bool _started;
    private static bool _ok;

    /// <summary>WebKit's private GMainContext; 0 until <see cref="Start" /> succeeds.</summary>
    private static nint _context;

    private static Ticker? _pumpTicker;
    private static int _pumpClients;

    /// <summary>Initialize GTK on the calling (UI) thread — idempotent, also when libdecor got
    ///     there first — and create the private context WebKit's sources will live on.</summary>
    public static bool Start()
    {
        if (_started) return _ok;
        _started = true;
        _ok = GtkNative.gtk_init_check(0, 0);
        if (!_ok)
        {
            Log.Error("gtk_init failed — no Wayland or X display reachable");
            return false;
        }

        // After gtk_init: GTK's own global setup belongs on the default context (libdecor shares
        // it). Only what we create from here on lands on the private one.
        _context = GtkNative.g_main_context_new();
        return _ok;
    }

    /// <summary>
    ///     Same-thread by contract — every caller is already on the UI thread — with WebKit's
    ///     context pushed as thread-default, so any GLib source the call creates (a WebKit timer,
    ///     a network watch, the offscreen window's frame clock) attaches there instead of to the
    ///     context libdecor drives. Nesting the same context is a plain stack push in GLib.
    /// </summary>
    public static void Run(Action action)
    {
        if (_context == 0)
        {
            action();
            return;
        }

        GtkNative.g_main_context_push_thread_default(_context);
        try
        {
            action();
        }
        finally
        {
            GtkNative.g_main_context_pop_thread_default(_context);
        }
    }

    /// <summary>The context WebKit's sources live on — sources created by hand attach here.</summary>
    public static nint Context => _context;

    /// <summary>
    ///     Drain pending GLib work without blocking, up to <see cref="PumpBudgetTicks" />. UI
    ///     thread only. Pushing the context is belt-and-braces: GLib pushes it while dispatching,
    ///     but a source created between iterations must land here too.
    /// </summary>
    public static void Pump()
    {
        if (_context == 0) return;
        // Scoped: this is the page's whole CPU cost per frame, and "how much of my frame is the
        // webview eating" is the first question anyone embedding one asks.
        using var scope = Zigote.Core.Diagnostics.Profiler.Scope("WebView.Pump");
        GtkNative.g_main_context_push_thread_default(_context);
        try
        {
            long deadline = Stopwatch.GetTimestamp() + PumpBudgetTicks;
            // Both contexts. The private one carries WebKit; the default one still carries GTK's
            // own machinery — notably GdkFrameClock, which GTK3 attaches to the GLOBAL default and
            // which is what actually repaints the offscreen window. In an app SDL/libdecor
            // iterates the default anyway, so this second drain finds little; engine-less tests
            // have no other driver and need it.
            bool more = true;
            while (more)
            {
                more = GtkNative.g_main_context_iteration(_context, false);
                more |= GtkNative.g_main_context_iteration(0, false);
                if (Stopwatch.GetTimestamp() >= deadline) break;
            }
        }
        finally
        {
            GtkNative.g_main_context_pop_thread_default(_context);
        }
    }

    /// <summary>
    ///     A live backend needs its context iterated every frame — nothing else in the process
    ///     touches it now that it is private.
    ///     <para>
    ///         A <see cref="Ticker" />, deliberately, and NOT a <c>System.Threading.Timer</c>: a
    ///         threadpool timer posting into the app fires at a phase unrelated to the frame loop,
    ///         so WebKit gets pumped twice in one frame and not at all in the next — the page's
    ///         content then arrives on an irregular beat and scrolling visibly stutters even at a
    ///         60 fps average. A ticker runs once per frame, on the UI thread, at a fixed point
    ///         before layout, so the page advances on exactly the same clock the engine draws on.
    ///         <see cref="Ticker.AnyActive" /> also keeps the frame loop awake, which is what stops
    ///         a page from stalling mid-load while the app is otherwise idle.
    ///     </para>
    /// </summary>
    public static void AddPumpClient()
    {
        if (_pumpClients++ != 0) return;
        _pumpTicker = new Ticker(static _ => Pump());
        _pumpTicker.Start();
    }

    public static void RemovePumpClient()
    {
        if (--_pumpClients != 0) return;
        _pumpTicker?.Dispose();
        _pumpTicker = null;
    }
}
