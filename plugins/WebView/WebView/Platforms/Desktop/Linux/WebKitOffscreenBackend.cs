using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Serilog;
using Zigote.Core.Animation;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using static WebView.GtkNative;

namespace WebView;

/// <summary>
///     The native-Wayland Linux backend: the shared <see cref="WebKitViewCore" /> inside a
///     <c>GtkOffscreenWindow</c>, rendered by WebKit's Cairo software path into an image surface
///     that a 60 Hz pump copies out as RGBA frames. The widget uploads them as an engine texture —
///     so on Wayland the page composites like any other widget (z-order, transforms, no overlay
///     hole), and nothing here ever touches X.
///     <para>
///         Input goes the other way: the widget forwards pointer/scroll/key events, and this
///         backend synthesizes the matching GdkEvents into the offscreen view via
///         <c>gtk_main_do_event</c>.
///     </para>
///     <para>
///         Ceiling, from the software path: no accelerated WebGL/video compositing. The frame
///         cost itself is damage-driven — an idle page converts and uploads nothing at all.
///     </para>
/// </summary>
internal sealed unsafe class WebKitOffscreenBackend : IWebViewBackend, ITextureWebViewBackend
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WebKitOffscreenBackend>();

    private readonly WebViewController _owner;
    private readonly Action<Action> _post;
    private readonly WebKitViewCore _core;
    private readonly object _frameLock = new();

    /// <summary>Cached: this runs once per delivered frame, and a fresh closure per frame is the
    ///     kind of steady-state allocation the frame loop is supposed to have none of.</summary>
    private readonly Action _raiseFrameArrived;
    private GCHandle _self;

    // UI-thread state (as is everything here -- see GtkThread).
    private nint _window;
    private Ticker? _frameTicker;
    private int _requestW = 640, _requestH = 480;
    private bool _visible = true;
    private bool _pointerDown;

    // Damage tracking: WebKit tells us which rows it repainted, so an idle page costs nothing
    // and a caret blink costs a few rows instead of a full-surface convert-and-compare.
    // Half-open row range [_dirtyY0, _dirtyY1); the initial full range covers the first paint.
    private bool _dirty = true;
    private int _dirtyY0;
    private int _dirtyY1 = int.MaxValue;
    private long _lastClickMs;
    private (float X, float Y) _lastClick;

    // Shared under _frameLock: the latest converted frame.
    private byte[] _front = [];
    private int _frameW, _frameH;
    private int _version;

    // UI-thread state: the engine texture mirroring _front.
    private ulong _texture;
    private int _uploadedVersion;
    private (uint W, uint H) _textureSize;

    private WebKitOffscreenBackend(WebViewController owner, Action<Action> post)
    {
        _owner = owner;
        _post = post;
        _core = new WebKitViewCore(owner, post);
        _raiseFrameArrived = () => FrameArrived?.Invoke();
        _self = GCHandle.Alloc(this);
    }

    public static WebKitOffscreenBackend? TryCreate(WebViewController owner)
    {
        if (!GtkThread.Start())
        {
            owner.LastError = "GTK could not initialize (no Wayland display reachable?)";
            return null;
        }

        // Same-thread by design: GLib sources fire inside the UI thread's pump, so page
        // events are already where widgets live.
        GtkThread.AddPumpClient();
        return new WebKitOffscreenBackend(owner, post: static a => a());
    }

    public event Action? FrameArrived;

    public (uint Width, uint Height) TextureSize => _textureSize;

    /// <summary>No window to parent into — offscreen by design, the parent is ignored.</summary>
    public void Attach(NativeParent parent) => GtkThread.Run(CreateView);

    // ── ITextureWebViewBackend: frames (UI thread) ────────────────────────────

    public ulong AcquireTexture()
    {
        lock (_frameLock)
        {
            if (_frameW == 0) return 0;
            if (_version == _uploadedVersion && _texture != 0) return _texture;

            if (_texture != 0 && _textureSize == ((uint)_frameW, (uint)_frameH))
            {
                if (!ZigoteEngine.UpdateTextureRgba(_texture, _front, (uint)_frameW, (uint)_frameH))
                    return _texture;
            }
            else
            {
                if (_texture != 0) ZigoteEngine.ReleaseTexture(_texture);
                _texture = ZigoteEngine.LoadTextureFromRgba(_front, (uint)_frameW, (uint)_frameH);
                _textureSize = ((uint)_frameW, (uint)_frameH);
            }

            _uploadedVersion = _version;
            return _texture;
        }
    }

    /// <summary>Test seam: force the next pump to reconvert the whole surface, so a partial-update
    ///     frame can be diffed against the ground truth.</summary>
    internal void ForceFullRedraw() => GtkThread.Run(MarkFullyDirty);

    /// <summary>Test seam: the newest frame without an engine. Returns false before the first paint.</summary>
    internal bool TryCopyFrame(out byte[] rgba, out int width, out int height, out int version)
    {
        lock (_frameLock)
        {
            rgba = _front.Length == 0 ? [] : (byte[])_front.Clone();
            width = _frameW;
            height = _frameH;
            version = _version;
            return _frameW != 0;
        }
    }

    public void SetSurfaceSize(float logicalWidth, float logicalHeight, float scale)
    {
        // Physical-pixel surface + matching zoom: CSS layout stays logical, glyphs render sharp.
        int w = Math.Max(1, (int)MathF.Round(logicalWidth * scale));
        int h = Math.Max(1, (int)MathF.Round(logicalHeight * scale));
        GtkThread.Run(() =>
        {
            _requestW = w;
            _requestH = h;
            MarkFullyDirty();
            if (_core.View == 0) return;
            gtk_widget_set_size_request(_core.View, w, h);
            webkit_web_view_set_zoom_level(_core.View, scale);
        });
    }

    // ── ITextureWebViewBackend: input (UI thread → GTK thread) ────────────────

    public void PointerDown(float x, float y) => GtkThread.Run(() => SendButton(down: true, x, y));

    public void PointerUp(float x, float y) => GtkThread.Run(() => SendButton(down: false, x, y));

    public void PointerMove(float x, float y) => GtkThread.Run(() => SendMotion(x, y));

    public void Scroll(float dx, float dy, float x, float y) =>
        GtkThread.Run(() => SendScroll(dx, dy, x, y));

    public void Key(char ch, uint scancode, bool down, Modifiers mods) =>
        GtkThread.Run(() => SendKey(KeyvalFor(ch, scancode), down, GdkState(mods)));

    public void Text(string text) => GtkThread.Run(() =>
    {
        foreach (var rune in text.EnumerateRunes())
        {
            uint keyval = gdk_unicode_to_keyval((uint)rune.Value);
            SendKey(keyval, down: true, state: 0);
            SendKey(keyval, down: false, state: 0);
        }
    });

    public void SetPageFocus(bool focused) => GtkThread.Run(() => SendFocus(focused));

    public void SetDisplayed(bool displayed) => GtkThread.Run(() => _visible = displayed);

    // ── IWebViewBackend: navigation delegates to the core ─────────────────────

    public void Navigate(string url) => _core.Navigate(url);

    public void LoadHtml(string html, string? baseUrl) => _core.LoadHtml(html, baseUrl);

    public void GoBack() => _core.GoBack();

    public void GoForward() => _core.GoForward();

    public void Reload() => _core.Reload();

    public void StopLoading() => _core.StopLoading();

    public Task<string?> EvaluateJavaScriptAsync(string script) => _core.EvaluateJavaScriptAsync(script);

    public void AddUserScript(string source) => _core.AddUserScript(source);

    public Task ClearBrowsingDataAsync() => _core.ClearBrowsingDataAsync();

    /// <summary>Widget-driven positioning is meaningless offscreen; only the size matters.</summary>
    public void SetBounds(Rect windowRect, float scale) =>
        SetSurfaceSize(windowRect.Width, windowRect.Height, scale);

    public void SetVisible(bool visible) => GtkThread.Run(() => _visible = visible);

    public void Dispose()
    {
        ulong texture = _texture;
        _texture = 0;
        if (texture != 0) ZigoteEngine.ReleaseTexture(texture);

        var self = _self;
        GtkThread.Run(() =>
        {
            _frameTicker?.Dispose();
            _frameTicker = null;
            if (_window != 0) gtk_widget_destroy(_window);
            _window = 0;
            _core.Dispose();
            if (self.IsAllocated) self.Free();
        });
        _self = default;
        GtkThread.RemovePumpClient();
    }

    // ── GTK thread ────────────────────────────────────────────────────────────

    private void CreateView()
    {
        _window = gtk_offscreen_window_new();
        _core.CreateView(_window);
        gtk_widget_set_size_request(_core.View, _requestW, _requestH);
        gtk_widget_show_all(_window);
        gtk_widget_grab_focus(_core.View);
        // The whole reason an idle page is free: GTK reports the rows WebKit actually repainted.
        // The timer below only coalesces those reports down to one conversion per frame.
        g_signal_connect_data(_window, "damage-event",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnDamage,
            GCHandle.ToIntPtr(_self), 0, 0);
        // Read the surface on the ENGINE's clock, not a clock of our own. A GLib timeout at 16 ms
        // is a third independent oscillator (engine frame loop, GTK frame clock, this) and the beat
        // between them is exactly what scrolling looks like when it stutters. The ticker fires once
        // per frame, right after GtkThread's pump ticker, so a repaint WebKit produced this frame is
        // converted and painted in the same frame.
        _frameTicker = new Ticker(_ => PumpFrame());
        _frameTicker.Start();
        Log.Debug("Offscreen view up ({Width}×{Height}, Wayland texture mode)", _requestW, _requestH);
    }

    /// <summary>
    ///     GTK thread: bring the latest damaged rows of the offscreen surface into the frame the
    ///     widget uploads. Early-out when nothing was damaged — which is the common case, and the
    ///     difference between an idle webview costing one branch per tick and costing a
    ///     full-surface convert plus a full-surface compare sixty times a second.
    /// </summary>
    private void PumpFrame()
    {
        if (!_visible || _window == 0 || !_dirty) return;
        nint surface = gtk_offscreen_window_get_surface(_window);
        if (surface == 0) return;

        cairo_surface_flush(surface);
        int w = cairo_image_surface_get_width(surface);
        int h = cairo_image_surface_get_height(surface);
        int stride = cairo_image_surface_get_stride(surface);
        byte* src = (byte*)cairo_image_surface_get_data(surface);
        if (src == null || w <= 0 || h <= 0) return;

        lock (_frameLock)
        {
            int bytes = w * h * 4;
            if (_frameW != w || _frameH != h || _front.Length != bytes)
            {
                // A resized surface shares nothing with the old one: everything is dirty.
                _front = new byte[bytes];
                _frameW = w;
                _frameH = h;
                _dirtyY0 = 0;
                _dirtyY1 = h;
            }

            int y0 = Math.Clamp(_dirtyY0, 0, h);
            int y1 = Math.Clamp(_dirtyY1, 0, h);
            if (y1 <= y0)
            {
                ClearDamage();
                return;
            }

            Swizzle(src: src, stride: stride, dst: _front, width: w, y0: y0, y1: y1);
            _version++;
        }

        ClearDamage();
        _post(_raiseFrameArrived);
    }

    private void MarkFullyDirty()
    {
        _dirty = true;
        _dirtyY0 = 0;
        _dirtyY1 = int.MaxValue;
    }

    private void ClearDamage()
    {
        _dirty = false;
        _dirtyY0 = int.MaxValue;
        _dirtyY1 = 0;
    }

    /// <summary>
    ///     damage-event(widget, GdkEventExpose*, user_data) — the rows WebKit just repainted,
    ///     unioned into one range for the next tick. GdkEventExpose on the GTK3 x86-64 ABI:
    ///     type(0) window(8) send_event(16) area{x,y,w,h}(20,24,28,32).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnDamage(nint widget, nint evt, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is not WebKitOffscreenBackend self) return 0;
        int y = Marshal.ReadInt32(evt, 24);
        int height = Marshal.ReadInt32(evt, 32);
        self._dirtyY0 = Math.Min(self._dirtyY0, y);
        self._dirtyY1 = Math.Max(self._dirtyY1, y + height);
        self._dirty = true;
        return 0; // propagate
    }

    /// <summary>
    ///     Rows [y0, y1) of Cairo's ARGB32 surface into the engine's RGBA, in place. Cairo ARGB32
    ///     is premultiplied BGRA in memory and pages are opaque, so a byte shuffle with forced
    ///     alpha is the whole conversion — sixteen bytes at a time where the hardware has it,
    ///     which is every CPU .NET vectorizes on.
    /// </summary>
    internal static void Swizzle(byte* src, int stride, byte[] dst, int width, int y0, int y1)
    {
        // BGRA → RGBA inside each 4-byte pixel, four pixels per vector.
        var shuffle = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
        // 0xFF000000 per pixel, little-endian: the alpha byte of each of the four pixels.
        var opaque = Vector128.Create(0xFF000000u).AsByte();
        bool vectorized = Vector128.IsHardwareAccelerated;
        int vectorEnd = width & ~3;

        fixed (byte* dstBase = dst)
        {
            for (int y = y0; y < y1; y++)
            {
                byte* row = src + (y * stride);
                byte* outRow = dstBase + (y * width * 4);
                int x = 0;
                if (vectorized)
                    for (; x < vectorEnd; x += 4)
                    {
                        var pixels = Vector128.Load(row + (x * 4));
                        (Vector128.Shuffle(pixels, shuffle) | opaque).Store(outRow + (x * 4));
                    }

                for (; x < width; x++)
                {
                    uint p = ((uint*)row)[x];
                    ((uint*)outRow)[x] =
                        0xFF000000u | ((p & 0x00FF0000) >> 16) | (p & 0x0000FF00) | ((p & 0x000000FF) << 16);
                }
            }
        }
    }

    // ── GdkEvent synthesis (GTK thread) ───────────────────────────────────────
    // Struct offsets are the stable GTK3 x86-64 ABI; gdk_event_new zero-fills, gdk_event_set_device
    // knows each type's device field, and the freed event unrefs the window we ref here.

    private nint TargetWindow()
    {
        return _core.View == 0 ? 0 : gtk_widget_get_window(_core.View);
    }

    private static nint EventFor(nint window, int type, bool keyboard)
    {
        nint e = gdk_event_new(type);
        Marshal.WriteIntPtr(e, 8, g_object_ref(window));
        Marshal.WriteByte(e, 16, 1); // send_event
        nint seat = gdk_display_get_default_seat(gdk_display_get_default());
        gdk_event_set_device(e, keyboard ? gdk_seat_get_keyboard(seat) : gdk_seat_get_pointer(seat));
        return e;
    }

    private void SendButton(bool down, float x, float y)
    {
        nint window = TargetWindow();
        if (window == 0) return;
        // A motion first: WebKit derives hover/hit state from the last pointer position.
        SendMotion(x, y);

        if (down)
        {
            // Double-click detection lives in the compositor normally; offscreen it is ours.
            // GDK's convention: the second press arrives as BUTTON_PRESS *and* 2BUTTON_PRESS.
            long now = Environment.TickCount64;
            bool isDouble = now - _lastClickMs < 400 &&
                            MathF.Abs(x - _lastClick.X) < 5 && MathF.Abs(y - _lastClick.Y) < 5;
            _lastClickMs = now;
            _lastClick = (x, y);
            EmitButton(window, type: 4 /* BUTTON_PRESS */, x, y, state: 0);
            if (isDouble) EmitButton(window, type: 5 /* 2BUTTON_PRESS */, x, y, state: 0);
            _pointerDown = true;
        }
        else
        {
            // X semantics: state describes the buttons held BEFORE the event — a release
            // carries the mask of the button it releases.
            EmitButton(window, type: 7 /* BUTTON_RELEASE */, x, y, state: 0x100 /* BUTTON1_MASK */);
            _pointerDown = false;
        }
    }

    private void EmitButton(nint window, int type, float x, float y, uint state)
    {
        nint e = EventFor(window, type, keyboard: false);
        *(double*)(e + 24) = x;
        *(double*)(e + 32) = y;
        *(uint*)(e + 48) = state;
        *(uint*)(e + 52) = 1; // left button
        *(double*)(e + 64) = x;
        *(double*)(e + 72) = y;
        gtk_main_do_event(e);
        gdk_event_free(e);
    }

    private void SendMotion(float x, float y)
    {
        nint window = TargetWindow();
        if (window == 0) return;
        nint e = EventFor(window, 3 /* MOTION_NOTIFY */, keyboard: false);
        *(double*)(e + 24) = x;
        *(double*)(e + 32) = y;
        // The held-button mask is what turns motion into a drag — without it WebKit never
        // starts a text selection.
        *(uint*)(e + 48) = _pointerDown ? 0x100u /* BUTTON1_MASK */ : 0u;
        *(double*)(e + 64) = x;
        *(double*)(e + 72) = y;
        gtk_main_do_event(e);
        gdk_event_free(e);
    }

    /// <summary>
    ///     GDK_FOCUS_CHANGE aimed at the TOPLEVEL: GtkWindow answers it by flipping is-active and
    ///     forwarding focus to its focus widget (the webview, grabbed at create) — which is what
    ///     makes WebKit draw the caret, focus rings and active-selection colors. An offscreen
    ///     window never hears this from a window manager, so the widget's focus drives it.
    /// </summary>
    private void SendFocus(bool focused)
    {
        if (_window == 0) return;
        nint window = gtk_widget_get_window(_window);
        if (window == 0) return;
        nint e = gdk_event_new(12 /* FOCUS_CHANGE */);
        Marshal.WriteIntPtr(e, 8, g_object_ref(window));
        Marshal.WriteByte(e, 16, 1); // send_event
        Marshal.WriteInt16(e, 18, (short)(focused ? 1 : 0)); // in
        nint seat = gdk_display_get_default_seat(gdk_display_get_default());
        gdk_event_set_device(e, gdk_seat_get_keyboard(seat));
        gtk_main_do_event(e);
        gdk_event_free(e);
    }

    private void SendScroll(float dx, float dy, float x, float y)
    {
        nint window = TargetWindow();
        if (window == 0) return;
        nint e = EventFor(window, 31 /* SCROLL */, keyboard: false);
        *(double*)(e + 24) = x;
        *(double*)(e + 32) = y;
        *(uint*)(e + 44) = 4; // GDK_SCROLL_SMOOTH
        *(double*)(e + 56) = x; // x_root
        *(double*)(e + 64) = y; // y_root
        // Wheel ticks map 1:1 to smooth-scroll units; GDK's positive delta_y scrolls DOWN,
        // Zigote's positive dy is scroll-up (natural wheel), hence the negation.
        *(double*)(e + 72) = -dx;
        *(double*)(e + 80) = -dy;
        gtk_main_do_event(e);
        gdk_event_free(e);
    }

    private void SendKey(uint keyval, bool down, uint state)
    {
        if (keyval == 0) return;
        nint window = TargetWindow();
        if (window == 0) return;
        nint e = EventFor(window, down ? 8 /* KEY_PRESS */ : 9 /* KEY_RELEASE */, keyboard: true);
        *(uint*)(e + 24) = state;
        *(uint*)(e + 28) = keyval;
        gtk_main_do_event(e);
        gdk_event_free(e);
    }

    /// <summary>SDL scancodes for the editing/navigation keys WebKit needs; text comes through
    ///     <see cref="Text" /> as unicode keyvals.</summary>
    private static uint KeyvalFor(char ch, uint scancode)
    {
        return scancode switch
        {
            40 or 88 => 0xFF0Du, // Return / keypad Enter
            41 => 0xFF1Bu, // Escape
            42 => 0xFF08u, // Backspace
            43 => 0xFF09u, // Tab
            74 => 0xFF50u, // Home
            75 => 0xFF55u, // PageUp
            76 => 0xFFFFu, // Delete
            77 => 0xFF57u, // End
            78 => 0xFF56u, // PageDown
            79 => 0xFF53u, // Right
            80 => 0xFF51u, // Left
            81 => 0xFF54u, // Down
            82 => 0xFF52u, // Up
            _ => ch >= ' ' ? gdk_unicode_to_keyval(ch) : 0u,
        };
    }

    private static uint GdkState(Modifiers mods)
    {
        uint state = 0;
        if ((mods & Modifiers.Shift) != 0) state |= 1; // GDK_SHIFT_MASK
        if ((mods & Modifiers.Ctrl) != 0) state |= 4; // GDK_CONTROL_MASK
        if ((mods & Modifiers.Alt) != 0) state |= 8; // GDK_MOD1_MASK
        return state;
    }
}
