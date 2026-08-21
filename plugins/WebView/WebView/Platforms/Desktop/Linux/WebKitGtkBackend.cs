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

        GtkThread.AddPumpClient();
        return new WebKitGtkBackend(owner, post: GtkThread.PostToUi);
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
        // Synchronous for the same reason the texture backend's is: when this returns, the view is
        // gone — the caller may be tearing the app down behind it.
        GtkThread.RunSync(() =>
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
        _core.CreateView(_window, accelerated: true);
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
///     Where GTK and WebKit run. Two modes, decided once at <see cref="Start" />:
///     <list type="bullet">
///         <item>
///             <b>Threaded</b> (the default when nothing else in the process uses GTK): a thread
///             of our own calls <c>gtk_init</c> and owns the whole stack — WebKit's main loop, the
///             offscreen window's repaints, the surface conversion. The UI thread then pays
///             <i>nothing</i> for the page. Measured on a scrolling 1280×800 document, the
///             same-thread mode spends ~7 ms of every 16.7 ms frame inside this pump (~11.5 ms at
///             1080p, over budget on 7% of frames) — that cost is the stutter, and threading is
///             what removes it.
///         </item>
///         <item>
///             <b>Same-thread</b>, when someone else got to GTK first: SDL's Wayland backend can
///             load libdecor's GTK plugin, which initializes GTK in-process and drives it from the
///             engine's event pump. Two threads inside one GTK is undefined and crashes in
///             practice, so we join them instead: the UI thread pumps, as before. See
///             <see cref="WebViewController.EnsureThreadedWebView" /> for how an app opts out of
///             that plugin and gets the threaded mode back.
///         </item>
///     </list>
///     <see cref="Run" /> is the shim every backend call goes through, and it means the same thing
///     either way: "run this where GTK lives".
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
internal static unsafe class GtkThread
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

    private static Thread? _thread;
    private static readonly ManualResetEventSlim Ready = new(false);

    /// <summary>Set at process exit to stop the loop. Volatile: written by the exiting thread,
    ///     read by the GTK thread between iterations.</summary>
    private static volatile bool _quit;

    /// <summary>Ticked by the GTK thread every heartbeat, for work that must happen there on a
    ///     clock — the offscreen backend's surface conversion. Copy-on-write: read without a lock
    ///     from the thread, replaced under one by the (rare) add/remove.</summary>
    private static Action[] _beats = [];
    private static readonly object BeatLock = new();

    /// <summary>True when GTK belongs to us and runs on its own thread — the page then costs the
    ///     UI thread nothing but a texture upload.</summary>
    public static bool Threaded { get; private set; }

    /// <summary>Bring GTK up — on its own thread where we may have it, on the calling (UI) thread
    ///     where we may not — and create the private context WebKit's sources will live on.</summary>
    public static bool Start()
    {
        if (_started) return _ok;
        _started = true;
        Threaded = CanOwnGtk();

        if (!Threaded)
        {
            _ok = GtkNative.gtk_init_check(0, 0);
            if (!_ok)
            {
                Log.Error("gtk_init failed — no Wayland or X display reachable");
                return false;
            }

            // After gtk_init: GTK's own global setup belongs on the default context (libdecor
            // shares it). Only what we create from here on lands on the private one.
            _context = GtkNative.g_main_context_new();
            Log.Debug("GTK on the UI thread — another GTK user is already in this process");
            return _ok;
        }

        // WebKit tears its globals down from a native atexit handler, and it aborts if GLib is
        // still being iterated underneath it. Managed ProcessExit runs before those handlers, so
        // this is the one moment where stopping the thread is both possible and necessary.
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Stop();
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Zigote.WebView.Gtk" };
        _thread.Start();
        Ready.Wait();
        if (_ok) Log.Debug("GTK on its own thread — the page costs the UI thread nothing");
        return _ok;
    }

    /// <summary>
    ///     Whether this process's GTK is ours alone. The one other GTK user we can land in is
    ///     libdecor's GTK plugin, which SDL loads to decorate a Wayland window — and it is mapped
    ///     by the time any webview is built, because the window comes first. Anything else that
    ///     initialized GTK is a case we cannot see, hence the escape hatch:
    ///     <c>ZIGOTE_WEBVIEW_THREADED=0</c> forces the conservative mode.
    /// </summary>
    private static bool CanOwnGtk()
    {
        if (Environment.GetEnvironmentVariable("ZIGOTE_WEBVIEW_THREADED") is { Length: > 0 } forced)
            return forced != "0";
        try
        {
            return !File.ReadAllText("/proc/self/maps").Contains("libdecor-gtk", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false; // cannot tell — take the mode that is safe everywhere
        }
    }

    /// <summary>
    ///     The GTK thread: init, then dispatch forever. WebKit's private context is what this
    ///     blocks on; GTK's own default context carries the offscreen window's frame clock (GTK3
    ///     attaches it to the GLOBAL default, not the thread-default) and is drained right after,
    ///     non-blocking and bounded. The heartbeat source is what caps that block, so the frame
    ///     clock is never more than one heartbeat late and an idle page still costs nothing.
    /// </summary>
    private static void ThreadMain()
    {
        _ok = GtkNative.gtk_init_check(0, 0);
        if (!_ok) Log.Error("gtk_init failed — no Wayland or X display reachable");

        // GLib's DEFAULT context, deliberately, and only here: this thread owns GTK, so nothing
        // else is iterating it — and GTK3 parks things on the default context regardless of the
        // thread-default (the offscreen window's GdkFrameClock is attached with a NULL context).
        // Keeping everything on one context is what lets the loop BLOCK: one context to wait on
        // means an idle page wakes this thread zero times, where a private context forced a
        // heartbeat to poll the default one. Refuse the mode if anything else owns it.
        if (_ok && !GtkNative.g_main_context_acquire(0))
        {
            Log.Error("GLib's default context is owned by another thread — falling back");
            _ok = false;
        }

        Ready.Set();
        if (!_ok) return;

        while (!_quit)
        {
            // Blocks until GLib has work: a WebKit source, GTK's frame clock, the wakeup that
            // g_main_context_invoke_full posts when the UI thread calls Run, or Stop's.
            GtkNative.g_main_context_iteration(0, may_block: true);
            if (_quit)
            {
                // Destroying a WebKitWebView is not finished when gtk_widget_destroy returns: the
                // page close is a round trip to the web process, and its completion is dispatched
                // here. Leaving that in flight is what makes WebKit's own atexit handler abort, so
                // the last thing this thread does is let the pending teardown finish.
                long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 4);
                while (GtkNative.g_main_context_iteration(0, false) &&
                       Stopwatch.GetTimestamp() < deadline)
                {
                }

                return;
            }

            var beats = _beats;
            foreach (var beat in beats)
            {
                try
                {
                    beat();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "GTK-thread beat threw");
                }
            }
        }
    }

    /// <summary>Run <paramref name="beat" /> on the GTK thread after every wake-up — which is to
    ///     say, whenever there was something to do and never otherwise. Threaded mode only; the
    ///     same-thread mode has the engine's Ticker for this.</summary>
    public static void AddBeat(Action beat)
    {
        lock (BeatLock) _beats = [.._beats, beat];
    }

    public static void RemoveBeat(Action beat)
    {
        lock (BeatLock) _beats = _beats.Where(b => b != beat).ToArray();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnInvoke(nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        try
        {
            (handle.Target as Action)?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GTK-thread action threw");
        }
        finally
        {
            handle.Free();
        }

        return 0; // G_SOURCE_REMOVE
    }

    /// <summary>
    ///     Same-thread by contract — every caller is already on the UI thread — with WebKit's
    ///     context pushed as thread-default, so any GLib source the call creates (a WebKit timer,
    ///     a network watch, the offscreen window's frame clock) attaches there instead of to the
    ///     context libdecor drives. Nesting the same context is a plain stack push in GLib.
    /// </summary>
    public static void Run(Action action)
    {
        if (Threaded)
        {
            if (!_ok)
            {
                action();
                return;
            }

            // Past Stop there is no thread to run on, and the process is on its way out: dropping
            // the action is the only safe answer — running it here would be GTK from a foreign
            // thread, which is the crash this whole class exists to avoid.
            if (_quit) return;

            // Already there (a signal handler calling back into the backend): straight through,
            // which is also what g_main_context_invoke would do.
            if (Thread.CurrentThread == _thread)
            {
                action();
                return;
            }

            var handle = GCHandle.Alloc(action);
            GtkNative.g_main_context_invoke_full(0 /* the default context */, 0 /* G_PRIORITY_DEFAULT */,
                (nint)(delegate* unmanaged[Cdecl]<nint, int>)&OnInvoke, GCHandle.ToIntPtr(handle), 0);
            return;
        }

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

    /// <summary>
    ///     How a page event gets from wherever GTK runs to where widgets live. Same-thread mode is
    ///     already there and runs it inline; the GTK thread hands it to the app, which runs it at
    ///     the top of the next frame and wakes an idle frame loop doing so. With no app (a test),
    ///     inline is all there is — the caller polls.
    /// </summary>
    public static void PostToUi(Action action)
    {
        if (!Threaded || Zigote.UI.Host.App.Active is not { } app) action();
        else app.Post(action);
    }

    /// <summary>
    ///     Stop iterating GLib and let the thread end. Idempotent, and only meaningful at process
    ///     exit: GTK cannot be initialized twice on two different threads, so there is no restart
    ///     after this — which is why nothing calls it when the last webview merely goes away.
    /// </summary>
    public static void Stop()
    {
        if (!Threaded || !_ok || _quit) return;
        _quit = true;
        GtkNative.g_main_context_wakeup(0);
        _thread?.Join(1000);
    }

    /// <summary>
    ///     <see cref="Run" /> and wait for it. For the few callers that need the result before they
    ///     continue — teardown, and the test seam that reads the surface. Runs inline (no wait)
    ///     when the caller is already the GTK thread, so it cannot deadlock on itself.
    /// </summary>
    public static void RunSync(Action action, int timeoutMs = 2000)
    {
        if (!Threaded || !_ok || _quit || Thread.CurrentThread == _thread)
        {
            Run(action);
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Run(() =>
        {
            try
            {
                action();
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(timeoutMs);
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
        if (_context == 0 || Threaded) return;
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
        if (Threaded) return;
        if (_pumpClients++ != 0) return;
        _pumpTicker = new Ticker(static _ => Pump());
        _pumpTicker.Start();
    }

    public static void RemovePumpClient()
    {
        if (Threaded) return;
        if (--_pumpClients != 0) return;
        _pumpTicker?.Dispose();
        _pumpTicker = null;
    }
}
