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
///     whose damaged rows are handed to the engine as a texture — BGRA, at Cairo's own row stride,
///     so there is no conversion pass and no repacking anywhere in the frame path. That upload runs
///     on the GTK thread where there is one (see GtkThread), on the engine's frame clock where GTK
///     has to share the UI thread; either way the widget just paints the handle —
///     so on Wayland the page composites like any other widget (z-order, transforms, no overlay
///     hole), and nothing here ever touches X.
///     <para>
///         Input goes the other way: the widget forwards pointer/scroll/key events, and this
///         backend synthesizes the matching GdkEvents into the offscreen view via
///         <c>gtk_main_do_event</c>.
///     </para>
///     <para>
///         Ceiling, from the software path: no accelerated WebGL/video compositing. The frame
///         cost itself is damage-driven end to end — an idle page uploads nothing at all, a caret
///         blink uploads its few rows, and only a scroll or a resize pays for the whole surface.
///         What the UI thread pays, in every case, is one volatile read.
///     </para>
/// </summary>
internal sealed unsafe class WebKitOffscreenBackend : IWebViewBackend, ITextureWebViewBackend
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WebKitOffscreenBackend>();

    private readonly WebViewController _owner;
    private readonly Action<Action> _post;
    private readonly WebKitViewCore _core;

    /// <summary>Cached: this runs once per delivered frame, and a fresh closure per frame is the
    ///     kind of steady-state allocation the frame loop is supposed to have none of.</summary>
    private readonly Action _raiseFrameArrived;

    /// <summary>Cached for <see cref="GtkThread.AddBeat" />/<see cref="GtkThread.RemoveBeat" />,
    ///     which match on the delegate instance.</summary>
    private readonly Action _pumpFrame;
    private GCHandle _self;

    // UI-thread state (as is everything here -- see GtkThread).
    private nint _window;
    private Ticker? _frameTicker;
    private int _requestW = 640, _requestH = 480;
    private bool _visible = true;
    private uint _pointerButtons;
    private bool _smoothScrolling = true;
    private nint _lastCursor = -1;
    private MouseCursor _publishedCursor = MouseCursor.Default;
    private (nint Handle, MouseCursor Cursor)[] _cursorTable = [];

    // Damage tracking: WebKit tells us which rows it repainted, so an idle page costs nothing
    // and a caret blink costs a few rows instead of a full-surface convert-and-compare.
    // Half-open row range [_dirtyY0, _dirtyY1); the initial full range covers the first paint.
    private bool _dirty = true;
    private int _dirtyY0;
    private int _dirtyY1 = int.MaxValue;
    private long _lastClickMs;
    private (float X, float Y) _lastClick;

    // GTK-thread state: the engine texture the page is uploaded into. Created, updated and
    // released entirely on that thread — ZigoteEngine's texture calls are thread-safe by
    // construction (the engine re-checks liveness under its own image lock), so the UI thread
    // never touches pixels at all.
    private ulong _texture;
    private int _texW, _texH;
    private uint _texStride;

    /// <summary>
    ///     The handoff, and the entire shared state between the two threads: an immutable record
    ///     the GTK thread publishes and the UI thread reads while painting. A reference write is
    ///     atomic, so the widget can never pair a new texture with an old size — and because the
    ///     tuple only changes when the surface is (re)created, the steady state allocates nothing:
    ///     a page painting sixty frames a second republishes nothing at all.
    /// </summary>
    private sealed record PublishedTexture(ulong Handle, uint Width, uint Height);

    private PublishedTexture? _published;

    /// <summary>Bumped per converted frame. The test seam and the benchmark read it; the widget
    ///     does not need it, because it always paints whatever is published.</summary>
    private int _version;

    /// <summary>Rows uploaded since the test seam last reset it. GTK-thread written.</summary>
    private (int Y0, int Y1) _pumpedRows = (int.MaxValue, 0);
    private int _pumpCount;
    private int _partialPumpCount;

    /// <summary>Set while a FrameArrived post is in flight, so a page painting faster than the
    ///     engine draws does not queue a redundant repaint per extra frame.</summary>
    private int _framePostPending;

    private WebKitOffscreenBackend(WebViewController owner, Action<Action> post)
    {
        _owner = owner;
        _post = post;
        _core = new WebKitViewCore(owner, post);
        _raiseFrameArrived = () =>
        {
            // Cleared BEFORE the handler runs: a frame that arrives during the repaint walk must
            // still be able to queue the next post, or it would sit unpainted until the page
            // happens to damage something else.
            Volatile.Write(ref _framePostPending, 0);
            FrameArrived?.Invoke();
        };
        _pumpFrame = PumpFrame;
        _self = GCHandle.Alloc(this);
    }

    public static WebKitOffscreenBackend? TryCreate(WebViewController owner)
    {
        if (!GtkThread.Start())
        {
            owner.LastError = "GTK could not initialize (no Wayland display reachable?)";
            return null;
        }

        GtkThread.AddPumpClient();
        return new WebKitOffscreenBackend(owner, post: GtkThread.PostToUi);
    }

    public event Action? FrameArrived;

    public event Action<MouseCursor>? CursorChanged;

    public (uint Width, uint Height) TextureSize =>
        Volatile.Read(ref _published) is { } frame ? (frame.Width, frame.Height) : (0u, 0u);

    /// <summary>No window to parent into — offscreen by design, the parent is ignored.</summary>
    public void Attach(NativeParent parent) => GtkThread.Run(CreateView);

    // ── ITextureWebViewBackend: frames (UI thread) ────────────────────────────

    /// <summary>
    ///     What the widget paints: the newest texture the GTK thread has finished uploading. The
    ///     upload itself already happened there, so this costs one volatile read — no conversion,
    ///     no copy, no lock, nothing that scales with the surface.
    /// </summary>
    public ulong AcquireTexture() => Volatile.Read(ref _published)?.Handle ?? 0;

    /// <summary>Test seam: force the next pump to reconvert the whole surface, so a partial-update
    ///     frame can be diffed against the ground truth.</summary>
    internal void ForceFullRedraw() => GtkThread.Run(MarkFullyDirty);

    /// <summary>How many frames the page has produced. Cheap — the benchmark reads it per tick.</summary>
    internal int FrameVersion => Volatile.Read(ref _version);

    /// <summary>Test seam: every row band handed to the engine since <see cref="ResetPumpedRows" />,
    ///     unioned. Damage tracking is what makes the upload small, and leaving a repainted row out
    ///     of it is the one way this path can put stale pixels on screen — so the property under
    ///     test is "every row that changed was in a band we uploaded".</summary>
    internal (int Y0, int Y1) PumpedRows => _pumpedRows;

    /// <summary>Test seam: how many pumps uploaded rows, and how many of those were a strict
    ///     sub-band. A backend that quietly gave up on damage tracking would still satisfy the
    ///     union check above while uploading the whole surface every time.</summary>
    internal (int Pumps, int Partial) PumpCounts => (_pumpCount, _partialPumpCount);

    internal void ResetPumpedRows() => GtkThread.RunSync(() =>
    {
        _pumpedRows = (int.MaxValue, 0);
        _pumpCount = 0;
        _partialPumpCount = 0;
    });

    /// <summary>
    ///     Test seam: the current surface as RGBA, without an engine. Converts on demand on the GTK
    ///     thread — the steady-state path never does this, because the engine takes Cairo's BGRA
    ///     rows as they are.
    /// </summary>
    internal bool TryCopyFrame(out byte[] rgba, out int width, out int height, out int version)
    {
        byte[] copy = [];
        int w = 0, h = 0;
        GtkThread.RunSync(() =>
        {
            if (_window == 0) return;
            nint surface = gtk_offscreen_window_get_surface(_window);
            if (surface == 0) return;
            cairo_surface_flush(surface);
            w = cairo_image_surface_get_width(surface);
            h = cairo_image_surface_get_height(surface);
            byte* src = (byte*)cairo_image_surface_get_data(surface);
            if (src == null || w <= 0 || h <= 0 || _version == 0)
            {
                w = h = 0;
                return;
            }

            copy = new byte[w * h * 4];
            Swizzle(src: src, stride: cairo_image_surface_get_stride(surface), dst: copy,
                width: w, y0: 0, y1: h);
        });

        rgba = copy;
        width = w;
        height = h;
        version = Volatile.Read(ref _version);
        return w != 0;
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

    public void PointerDown(float x, float y, int button) =>
        GtkThread.Run(() => SendButton(down: true, x, y, button));

    public void PointerUp(float x, float y, int button) =>
        GtkThread.Run(() => SendButton(down: false, x, y, button));

    public void PointerMove(float x, float y) => GtkThread.Run(() => SendMotion(x, y));

    /// <summary>The pointer entered or left the widget. Without the crossing event WebKit never
    ///     runs the page's mouseleave, so the last link stays lit after the pointer is gone.</summary>
    public void PointerCrossing(bool entered, float x, float y) =>
        GtkThread.Run(() => SendCrossing(entered, x, y));

    public void Scroll(float dx, float dy, float x, float y, Modifiers mods) =>
        GtkThread.Run(() => SendScroll(dx, dy, x, y, GdkState(mods)));

    public void Key(char ch, uint scancode, bool down, Modifiers mods) =>
        GtkThread.Run(() => SendKey(KeyvalFor(ch, scancode, mods), down, GdkState(mods)));

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
        // Unpublish first, release second: the widget must stop being handed a texture before the
        // handle behind it goes away, and both hops are ordered by the GTK thread's queue.
        Volatile.Write(ref _published, null);

        var self = _self;
        GtkThread.RemoveBeat(_pumpFrame);
        // SYNCHRONOUS, unlike every other hop: the caller is entitled to assume that when Dispose
        // returns, this view no longer exists — and the thing it usually goes on to do is shut the
        // engine down, which would pull the texture registry out from under an upload still in
        // flight on the GTK thread.
        GtkThread.RunSync(() =>
        {
            if (_texture != 0) ZigoteEngine.ReleaseTexture(_texture);
            _texture = 0;
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
        _core.CreateView(_window, accelerated: false);
        gtk_widget_set_size_request(_core.View, _requestW, _requestH);
        gtk_widget_show_all(_window);
        gtk_widget_grab_focus(_core.View);
        // The whole reason an idle page is free: GTK reports the rows WebKit actually repainted,
        // and the pump below turns exactly those rows into exactly that much GPU traffic.
        g_signal_connect_data(_window, "damage-event",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnDamage,
            GCHandle.ToIntPtr(_self), 0, 0);
        // Who converts the surface, and on whose clock:
        if (GtkThread.Threaded)
        {
            // The GTK thread, right after whatever woke it. The UI thread never sees the surface
            // at all — it paints the texture this thread already uploaded, and a frame the page
            // produced twice in one engine frame simply replaces itself. Nothing beats against
            // anything, because nobody is being driven.
            GtkThread.AddBeat(_pumpFrame);
        }
        else
        {
            // The UI thread, on the ENGINE's clock — not a clock of our own. Here a GLib timeout
            // at 16 ms would be a third independent oscillator (engine frame loop, GTK frame
            // clock, this) and the beat between them is exactly what scrolling looks like when it
            // stutters. The ticker fires once per frame, right after GtkThread's pump ticker, so a
            // repaint WebKit produced this frame is converted and painted in the same frame.
            _frameTicker = new Ticker(_ => PumpFrame());
            _frameTicker.Start();
        }
        Log.Debug("Offscreen view up ({Width}×{Height}, Wayland texture mode)", _requestW, _requestH);
    }

    /// <summary>
    ///     GTK thread: hand the rows WebKit just repainted to the engine, straight out of Cairo's
    ///     surface. Nothing is converted and nothing is repacked — the texture is created in
    ///     Cairo's own channel order (BGRA) and with Cairo's own row stride, so the only copy left
    ///     in the whole path is the engine's, and it covers the damaged rows alone.
    ///     <para>
    ///         Early-out when nothing was damaged, which is the common case: an idle page costs one
    ///         branch, not a surface.
    ///     </para>
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
        if (src == null || w <= 0 || h <= 0 || stride < w * 4) return;

        int y0 = Math.Clamp(_dirtyY0, 0, h);
        int y1 = Math.Clamp(_dirtyY1, 0, h);
        if (y1 <= y0)
        {
            ClearDamage();
            return;
        }

        // A resized surface shares nothing with the old one, and a texture cannot be resized in
        // place — so the handle is rebuilt, and then the whole surface is what is new.
        bool fresh = _texture == 0 || _texW != w || _texH != h || _texStride != (uint)stride;

        // No engine yet — a headless test, or a controller built before the app. The surface still
        // advances (the page is running); _texW stays 0, so the first pump that finds an engine
        // uploads the whole thing.
        if (ZigoteEngine.Instance is not null)
        {
            var pixels = new ReadOnlySpan<byte>(src, stride * h);
            if (fresh)
            {
                (y0, y1) = (0, h);
                ulong stale = _texture;
                _texture = ZigoteEngine.LoadTextureFromPixels(pixels, (uint)w, (uint)h, (uint)stride, bgra: true);
                if (_texture == 0) return; // keep the damage and try again on the next beat
                if (stale != 0) ZigoteEngine.ReleaseTexture(stale);
                (_texW, _texH, _texStride) = (w, h, (uint)stride);
                Volatile.Write(ref _published, new PublishedTexture(_texture, (uint)w, (uint)h));
            }
            else if (!ZigoteEngine.UpdateTextureRows(
                         _texture, pixels, (uint)w, (uint)h, (uint)stride, (uint)y0, (uint)y1))
            {
                return; // engine gone or handle rejected: keep the damage, try again next beat
            }
        }

        _pumpedRows = (Math.Min(_pumpedRows.Y0, y0), Math.Max(_pumpedRows.Y1, y1));
        _pumpCount++;
        if (y1 - y0 < h) _partialPumpCount++;
        ClearDamage();
        PublishCursor();
        Volatile.Write(ref _version, Volatile.Read(ref _version) + 1);

        // One repaint request in flight at a time: the page can produce frames faster than the
        // engine draws them, and every extra post is a redundant walk of the widget tree.
        if (Interlocked.Exchange(ref _framePostPending, 1) == 0) _post(_raiseFrameArrived);
    }

    /// <summary>
    ///     GTK thread: hand the page's cursor to the widget when it changes. WebKit sets it on the
    ///     view's GdkWindow exactly as it would in a real window — nothing reads it there when the
    ///     window is offscreen, so this is the only way the hand ever appears over a link.
    ///     <para>
    ///         Identity comparison, not a name lookup: GdkCursor* for a given name is interned by
    ///         GDK, so the common case is a pointer compare against the last one seen.
    ///     </para>
    /// </summary>
    private void PublishCursor()
    {
        if (_core.View == 0) return;
        nint gdkWindow = gtk_widget_get_window(_core.View);
        if (gdkWindow == 0) return;
        nint cursor = gdk_window_get_cursor(gdkWindow);
        if (cursor == _lastCursor) return;
        _lastCursor = cursor;

        var mapped = MouseCursor.Default;
        if (cursor != 0)
        {
            EnsureCursorTable();
            for (int i = 0; i < _cursorTable.Length; i++)
            {
                if (_cursorTable[i].Handle != cursor) continue;
                mapped = _cursorTable[i].Cursor;
                break;
            }
        }

        if (mapped == _publishedCursor) return;
        _publishedCursor = mapped;
        _post(() => CursorChanged?.Invoke(mapped));
    }

    /// <summary>The handful of CSS cursors the engine can show, resolved once to the GdkCursor*
    ///     instances GDK hands back for those names.</summary>
    private void EnsureCursorTable()
    {
        if (_cursorTable.Length != 0) return;
        nint display = gdk_display_get_default();
        (string Name, MouseCursor Cursor)[] names =
        [
            ("pointer", MouseCursor.Pointer), ("text", MouseCursor.Text),
            ("crosshair", MouseCursor.Crosshair), ("wait", MouseCursor.Wait),
            ("progress", MouseCursor.Progress), ("not-allowed", MouseCursor.NotAllowed),
            ("no-drop", MouseCursor.NotAllowed), ("move", MouseCursor.Move),
            ("all-scroll", MouseCursor.Move), ("grab", MouseCursor.Move),
            ("grabbing", MouseCursor.Move), ("ew-resize", MouseCursor.ResizeEW),
            ("col-resize", MouseCursor.ResizeEW), ("ns-resize", MouseCursor.ResizeNS),
            ("row-resize", MouseCursor.ResizeNS), ("nwse-resize", MouseCursor.ResizeNWSE),
            ("se-resize", MouseCursor.ResizeNWSE), ("nesw-resize", MouseCursor.ResizeNESW),
            ("ne-resize", MouseCursor.ResizeNESW),
        ];
        var table = new List<(nint Handle, MouseCursor Cursor)>(names.Length);
        foreach (var (name, cursor) in names)
        {
            nint handle = gdk_cursor_new_from_name(display, name);
            if (handle != 0) table.Add((handle, cursor));
        }

        _cursorTable = table.ToArray();
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
    ///     Rows [y0, y1) of Cairo's ARGB32 surface as RGBA. The frame path does NOT use this — the
    ///     engine takes Cairo's BGRA rows as they are — but the test seam does, and it is the
    ///     reference the engine's channel-order handling is checked against. Cairo ARGB32
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

    private static nint EventFor(nint window, int type, bool keyboard, int timeOffset = 20)
    {
        nint e = gdk_event_new(type);
        Marshal.WriteIntPtr(e, 8, g_object_ref(window));
        Marshal.WriteByte(e, 16, 1); // send_event
        // GdkEvent::time, in milliseconds of GLib's monotonic clock. Zero here is what makes
        // event.timeStamp zero in the page, which breaks anything computing gesture velocity.
        Marshal.WriteInt32(e, timeOffset, unchecked((int)(uint)(g_get_monotonic_time() / 1000)));
        nint seat = gdk_display_get_default_seat(gdk_display_get_default());
        gdk_event_set_device(e, keyboard ? gdk_seat_get_keyboard(seat) : gdk_seat_get_pointer(seat));
        return e;
    }

    private void SendButton(bool down, float x, float y, int button)
    {
        nint window = TargetWindow();
        if (window == 0) return;
        // A motion first: WebKit derives hover/hit state from the last pointer position.
        SendMotion(x, y);

        // GDK's held-button mask: button 1 is 1<<8, 2 is 1<<9, 3 is 1<<10.
        uint mask = button is >= 1 and <= 5 ? 1u << (7 + button) : 0x100u;

        if (down)
        {
            // Double-click detection lives in the compositor normally; offscreen it is ours.
            // GDK's convention: the second press arrives as BUTTON_PRESS *and* 2BUTTON_PRESS.
            long now = Environment.TickCount64;
            bool isDouble = now - _lastClickMs < 400 &&
                            MathF.Abs(x - _lastClick.X) < 5 && MathF.Abs(y - _lastClick.Y) < 5;
            _lastClickMs = now;
            _lastClick = (x, y);
            EmitButton(window, type: 4 /* BUTTON_PRESS */, x, y, state: _pointerButtons, button);
            if (isDouble) EmitButton(window, type: 5 /* 2BUTTON_PRESS */, x, y, _pointerButtons, button);
            _pointerButtons |= mask;
        }
        else
        {
            // X semantics: state describes the buttons held BEFORE the event — a release
            // carries the mask of the button it releases.
            EmitButton(window, type: 7 /* BUTTON_RELEASE */, x, y, state: _pointerButtons | mask, button);
            _pointerButtons &= ~mask;
        }
    }

    private void EmitButton(nint window, int type, float x, float y, uint state, int button)
    {
        nint e = EventFor(window, type, keyboard: false);
        *(double*)(e + 24) = x;
        *(double*)(e + 32) = y;
        *(uint*)(e + 48) = state;
        *(uint*)(e + 52) = (uint)button;
        *(double*)(e + 64) = x;
        *(double*)(e + 72) = y;
        gtk_main_do_event(e);
        gdk_event_free(e);
    }

    /// <summary>
    ///     GdkEventCrossing on the GTK3 x86-64 ABI: type(0) window(8) send_event(16) subwindow(24)
    ///     time(32) x(40) y(48) x_root(56) y_root(64) mode(72) detail(76) focus(80) state(84).
    /// </summary>
    private void SendCrossing(bool entered, float x, float y)
    {
        nint window = TargetWindow();
        if (window == 0) return;
        nint e = EventFor(window, entered ? 10 /* ENTER_NOTIFY */ : 11 /* LEAVE_NOTIFY */,
            keyboard: false, timeOffset: 32);
        *(double*)(e + 40) = x;
        *(double*)(e + 48) = y;
        *(double*)(e + 56) = x;
        *(double*)(e + 64) = y;
        *(uint*)(e + 72) = 0; // GDK_CROSSING_NORMAL
        *(uint*)(e + 76) = 2; // GDK_NOTIFY_NONLINEAR — a different toplevel, which is the truth
        *(uint*)(e + 84) = _pointerButtons;
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
        *(uint*)(e + 48) = _pointerButtons;
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

    private void SendScroll(float dx, float dy, float x, float y, uint state)
    {
        nint window = TargetWindow();
        if (window == 0) return;

        // Whole ticks are a mouse wheel and want WebKit's easing (one notch should glide, not
        // jump). Fractional deltas are a trackpad, where the finger IS the animation: easing them
        // leaves the page sliding for ~200 ms after the fingers lift, which is the single biggest
        // "this feels wrong" in the whole input path.
        bool precise = dx != MathF.Round(dx) || dy != MathF.Round(dy);
        SetSmoothScrolling(!precise);

        nint e = EventFor(window, 31 /* SCROLL */, keyboard: false);
        *(double*)(e + 24) = x;
        *(double*)(e + 32) = y;
        *(uint*)(e + 40) = state; // GdkEventScroll::state — ctrl+wheel is zoom, shift+wheel is x
        *(uint*)(e + 44) = 4; // GDK_SCROLL_SMOOTH
        *(double*)(e + 56) = x; // x_root
        *(double*)(e + 64) = y; // y_root
        // GDK's positive delta_y scrolls DOWN and Zigote's positive dy is scroll-up (natural
        // wheel), so Y is negated. X is NOT: both call rightward positive.
        *(double*)(e + 72) = dx;
        *(double*)(e + 80) = -dy;
        gtk_main_do_event(e);
        gdk_event_free(e);
    }

    /// <summary>GTK thread. Flipping this per gesture is cheap (a settings property), and it is
    ///     the only lever WebKit gives us over its scroll animation.</summary>
    private void SetSmoothScrolling(bool enabled)
    {
        if (_core.View == 0 || _smoothScrolling == enabled) return;
        _smoothScrolling = enabled;
        webkit_settings_set_enable_smooth_scrolling(webkit_web_view_get_settings(_core.View), enabled);
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

    /// <summary>
    ///     SDL scancodes for the editing/navigation keys WebKit needs. Printable characters are
    ///     deliberately NOT here: they arrive again through <see cref="Text" /> (which is what
    ///     carries the composed, layout-correct character), and synthesizing them from both places
    ///     is how every 'a' used to reach the page as "aa". The exception is a chord — Ctrl+C,
    ///     Alt+D — where the character IS the shortcut and no text event follows.
    /// </summary>
    private static uint KeyvalFor(char ch, uint scancode, Modifiers mods)
    {
        bool chord = (mods & (Modifiers.Ctrl | Modifiers.Alt | Modifiers.Cmd)) != 0;
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
        if ((mods & Modifiers.Cmd) != 0) state |= 1 << 26; // GDK_SUPER_MASK
        return state;
    }
}
