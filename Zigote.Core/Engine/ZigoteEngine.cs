using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Zigote.Core.Events;
using Zigote.Core.Math3D;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.Core.Rendering;
using FontStyle = Zigote.Core.Paint.FontStyle;
using FontWeight = Zigote.Core.Paint.FontWeight;

namespace Zigote.Core.Engine;

/// <summary>
///     High-level facade over the native Zig engine.
///     Typical frame loop (v2 render graph):
///     <code>
///   using var engine = new ZigoteEngine();
///   engine.Initialize(960, 640, "My App");
/// 
///   while (!engine.ShouldQuit)
///   {
///       foreach (var evt in engine.PollEvents())
///           HandleEvent(evt);
/// 
///       var paint = new PaintList();
///       BuildUi(paint);
///       engine.BeginFrame(deltaTime);
///       engine.SubmitPaintCommands(paint);
///       engine.RenderFrameV2();
///       engine.EndFrame();
///   }
/// </code>
/// </summary>
public sealed unsafe class ZigoteEngine : IDisposable
{
    private const int EventBufferSize = 512;

    // Max UTF-8 byte length to null-terminate on the stack for FFI string args before falling back to a
    // heap byte[] (covers virtually every node name / font family; long file paths spill to the heap).
    private const int StackStringMax = 256;

    // ── 2D sprite renderer FFI ───────────────────────────────────────────────────
    /// <summary>
    ///     Floats per sprite instance — pos.xyz, rot, size.xy, uv0.xy, uv1.xy, rgba, corner_radius,
    ///     border_width. Must match SpriteSystem.INSTANCE_FLOATS and the sprite shader's VsIn.
    /// </summary>
    public const int SpriteInstanceFloats = 16;

    // Log thunk — static so &LogThunk produces a stable function pointer across instances.
    // The delegate field keeps the managed action alive while native code holds the thunk.
    private static Action<int, string>? _logAction;

    // Managed array — pinned only during the native call, safe to iterate afterwards.
    private readonly ZgEvent[] _eventBuf = new ZgEvent[EventBufferSize];

    // Reuses MouseMove/Scroll event objects across polls (they flood faster than the frame rate);
    // reset at the top of every PollEventsInto. Used only by the allocation-free frame-loop drain.
    private readonly EventPool _eventPool = new();

    private bool _allowRelativeMouseMode = true;

    // ── Domain facades ────────────────────────────────────────────────────────
    //
    // The engine's surface is one class because there is one native handle behind all of it, but it
    // covers four unrelated jobs. These name the seams without moving a single call: the Audio*,
    // Scene* and Render* methods below stay exactly where they are and every existing caller keeps
    // working — new code can take `IAudioApi` instead of the whole engine, which is what makes a
    // player's queue and equalizer testable without a sound card.

    private IAudioApi? _audio;

    // Active backend capabilities, cached at Initialize() (see Caps).
    private RendererCaps _caps;
    private bool _disposed;
    private ulong _handle;
    private bool _initialized;
    private PaintList.PinCallback? _submitOverlayCb;

    // Cached submit delegates — the lambdas capture only `this` (to read the stable _handle), so a
    // single delegate instance is allocated lazily and reused, instead of one closure per frame.
    private PaintList.PinCallback? _submitPaintCb;

    public ZigoteEngine() => Instance = this;

    public static ZigoteEngine? Instance { get; private set; }

    /// <summary>Opaque engine handle passed to all native FFI calls.</summary>
    public ulong Handle => _handle;

    /// <summary>
    ///     Media playback: files, transport, equalizer chains, offline decode. An interface, so an
    ///     app can be driven by a fake device in tests — see <see cref="IAudioApi" />. Spatial and
    ///     procedural audio (listener, one-shots, voices, buses) stay on this class: they are a
    ///     game's concern and no app should have to stub them.
    /// </summary>
    public IAudioApi Audio => _audio ??= new EngineAudioApi(this);

    /// <summary>
    ///     The 3D scene: nodes, transforms, materials, lights, cameras. A zero-allocation struct over
    ///     this engine — see <see cref="Scene3D" />.
    /// </summary>
    public Scene3D Scene => new(this);

    public bool ShouldQuit { get; private set; }

    /// <summary>Current surface width in physical pixels.</summary>
    public uint PixelWidth { get; private set; }

    /// <summary>Current surface height in physical pixels.</summary>
    public uint PixelHeight { get; private set; }

    /// <summary>HiDPI scale factor (e.g. 2.0 on Retina displays).</summary>
    public float Scale { get; private set; } = 1f;

    /// <summary>
    ///     Refresh rate of the monitor the main window is on, in Hz; 0 when the platform doesn't
    ///     report one. Re-read whenever the window is resized or moves to another display, so on a
    ///     mixed 60 Hz + 144 Hz desktop this tracks whichever panel the window is actually on.
    ///     Hosts pace their frame loop against <c>App.FrameIntervalTicks</c> rather than reading this
    ///     directly — that folds in the app's own FPS cap.
    /// </summary>
    public float DisplayRefreshHz { get; private set; }

    /// <summary>Surface width in logical pixels.</summary>
    public float LogicalWidth => PixelWidth / Scale;

    /// <summary>Surface height in logical pixels.</summary>
    public float LogicalHeight => PixelHeight / Scale;

    /// <summary>
    ///     Runtime capabilities of the active renderer backend (which backend was actually
    ///     selected, and whether vendor upscalers / hardware ray tracing are available). Cached at
    ///     <see cref="Initialize" />. Use to gate optional native features in the editor UI.
    /// </summary>
    public RendererCaps Caps
    {
        get
        {
            EnsureReady();
            return _caps;
        }
    }

    /// <summary>
    ///     SDL window id of the main engine window. Compare against
    ///     <see cref="InputEvent.WindowId" /> (0 = unknown → main) to route events between the main
    ///     window and secondary <see cref="NativeWindow" />s.
    /// </summary>
    public uint MainWindowId { get; private set; }

    /// <summary>Whether the pointer is currently captured.</summary>
    public bool RelativeMouseMode { get; private set; }

    /// <summary>
    ///     Host veto on capture. Capture hides and pins the cursor, which is right in a fullscreen game
    ///     and hostile inside a tool — an editor's other panels become unreachable. Clearing this
    ///     refuses further capture and releases it immediately, so the host has the last word and a
    ///     misbehaving script cannot trap the pointer. Lives here rather than in the UI layer because
    ///     this is the only door to capture; a veto anywhere else could be walked around.
    /// </summary>
    public bool AllowRelativeMouseMode
    {
        get => _allowRelativeMouseMode;
        set
        {
            _allowRelativeMouseMode = value;
            if (!value) SetRelativeMouseMode(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }

    /// <summary>Request the run loop to exit (e.g. from a Quit menu item).</summary>
    public void Quit() => ShouldQuit = true;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LogThunk(int level, byte* msg) => _logAction?.Invoke(
        arg1: level,
        arg2: Marshal.PtrToStringUTF8((nint)msg) ?? ""
    );

    /// <summary>
    ///     Raised (on the main thread) from the native resize event-watch while the user drags a window
    ///     edge — during that modal drag the OS blocks the normal frame loop, so the host uses this to
    ///     relayout + paint + present a live frame. The argument is the SDL window id that changed size.
    /// </summary>
    public event Action<uint>? OnLiveResize;

    // Static thunk so &LiveResizeThunk is a stable function pointer; routes to the live instance.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LiveResizeThunk(uint windowId, uint width, uint height) =>
        Instance?.HandleLiveResize(windowId: windowId, width: width, height: height);

    private void HandleLiveResize(uint windowId, uint width, uint height)
    {
        // The watch already reconfigured the surface; refresh our cached main-window size so the
        // relayout reads the new dimensions. Secondary windows refresh their own size in the host.
        if (windowId == 0 || windowId == MainWindowId) RefreshSize();
        OnLiveResize?.Invoke(windowId);
    }

    /// <summary>
    ///     Set the active OS mouse cursor shape. Process-global (SDL has one active cursor, not
    ///     per-window); the native side lazily creates and caches each system cursor, so this is cheap
    ///     to call every frame.
    /// </summary>
    public void SetCursor(MouseCursor cursor)
    {
        if (!_initialized || _disposed) return;
        NativeEngine.SetCursor((uint)cursor);
    }

    /// <summary>
    ///     Capture the pointer for mouselook: the cursor is hidden and held inside the window, and
    ///     motion arrives as <see cref="Events.MouseMoveEvent.RelativeX" />/<c>RelativeY</c> deltas
    ///     instead of a position.
    ///     <para>
    ///         A first-person camera cannot be built without this. With a free cursor the pointer
    ///         eventually reaches a window edge and stops producing motion, so the view stops turning —
    ///         no amount of application-side compensation fixes that, because the input simply is not
    ///         there. While captured, <see cref="Events.MouseMoveEvent.X" />/<c>Y</c> are meaningless
    ///         and hit-testing should be suspended.
    ///     </para>
    ///     <para>
    ///         Release it whenever the player needs the cursor back — a menu, a pause screen, or focus
    ///         leaving the window. Returns false if capture is disallowed or the platform refused.
    ///     </para>
    /// </summary>
    public bool SetRelativeMouseMode(bool enabled)
    {
        if (!_initialized || _disposed) return false;
        if (enabled && !AllowRelativeMouseMode) return false;
        if (!NativeEngine.SetRelativeMouseMode(handle: _handle, enabled: enabled)) return false;
        RelativeMouseMode = enabled;
        return true;
    }

    /// <summary>
    ///     Open the native window and initialize the GPU.
    /// </summary>
    /// <param name="width">Window width in logical pixels.</param>
    /// <param name="height">Window height in logical pixels.</param>
    /// <param name="title">Window title.</param>
    /// <param name="fontPath">Path to a .ttf/.ttc font, or null for the bundled-Inter fallback.</param>
    /// <param name="fontName">Font family name matching <paramref name="fontPath" />.</param>
    public void Initialize(
        uint width,
        uint height,
        string title,
        string? fontPath = null,
        string? fontName = null,
        RenderBackend backend = RenderBackend.Auto,
        GpuPowerPreference gpuPreference = GpuPowerPreference.Auto,
        int gpuIndex = -1)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);

        byte[] titleBytes = [.. Encoding.UTF8.GetBytes(title), 0];
        byte[]? fpBytes = fontPath is not null ? [.. Encoding.UTF8.GetBytes(fontPath), 0] : null;
        byte[]? fnBytes = fontName is not null ? [.. Encoding.UTF8.GetBytes(fontName), 0] : null;

        _logAction = (level, msg) =>
        {
            string prefix =
                level switch { 0 => "ERR", 1 => "WRN", 2 => "INF", 3 => "DBG", _ => "LOG" };
            Console.WriteLine($"[Zigote::{prefix}] {msg}");
        };
        NativeEngine.SetLogCallback(&LogThunk);

        ZgResult result;
        fixed (byte* tp = titleBytes)
        fixed (byte* fp = fpBytes)
        fixed (byte* fn = fnBytes)
        {
            result = NativeEngine.Init(
                outHandle: out _handle,
                width: width,
                height: height,
                title: tp,
                fontPath: fp,
                fontName: fn,
                backend: (uint)backend,
                gpuPower: (uint)gpuPreference,
                gpuIndex: gpuIndex
            );
        }

        if (result != ZgResult.Ok)
            throw new InvalidOperationException("zigote_init failed. Check stderr for details.");

        _initialized = true;
        ValidateAbi();
        _caps = QueryRendererCaps();
        RefreshSize();
        MainWindowId = NativeEngine.MainWindowId(_handle);

        // Register the live-resize render callback: the native SDL event-watch invokes it from inside
        // the OS modal window-resize loop so the UI keeps laying out + presenting during the drag.
        NativeEngine.SetResizeRenderCallback(handle: _handle, cb: &LiveResizeThunk);
    }

    /// <summary>
    ///     The GPUs the engine found at startup, in the order the override index refers to. The list
    ///     is snapshotted at init (the adapters not chosen are released immediately), so this is cheap
    ///     and stable — it will not notice a GPU hot-plugged afterwards.
    /// </summary>
    /// <returns>An empty list before <see cref="Initialize" />, or if enumeration found nothing.</returns>
    public IReadOnlyList<GpuInfo> EnumerateGpus()
    {
        if (!_initialized || _disposed) return [];

        const int max = 16; // gpu_select.max_gpus
        var raw = stackalloc ZgGpuInfo[max];
        int count = (int)NativeEngine.EnumerateGpus(handle: _handle, outGpus: raw, max: max);

        var list = new List<GpuInfo>(count);
        for (int i = 0; i < count; i++)
        {
            string name = Marshal.PtrToStringUTF8((IntPtr)raw[i].Name) ?? "Unknown GPU";
            list.Add(
                new GpuInfo(
                    Index: i,
                    Name: name,
                    Backend: (ZgGpuBackend)raw[i].Backend,
                    DeviceType: (ZgGpuDeviceType)raw[i].DeviceType,
                    VendorId: raw[i].VendorId,
                    DeviceId: raw[i].DeviceId
                )
            );
        }

        return list;
    }

    /// <summary>
    ///     The GPU the renderer is actually running on, or null when the engine fell back to wgpu's
    ///     own adapter pick (which is not necessarily one of the enumerated entries).
    /// </summary>
    public GpuInfo? ActiveGpu()
    {
        if (!_initialized || _disposed) return null;
        int index = NativeEngine.GetActiveGpu(_handle);
        var gpus = EnumerateGpus();
        return index >= 0 && index < gpus.Count ? gpus[index] : null;
    }

    /// <summary>Does this event belong to the main window (id matches, or unknown/global)?</summary>
    public bool IsMainWindowEvent(InputEvent evt) =>
        evt.WindowId == 0 || evt.WindowId == MainWindowId;

    /// <summary>Screen position of the main window's top-left corner (logical desktop coords).</summary>
    public (int X, int Y) MainWindowPosition()
    {
        EnsureReady();
        NativeEngine.MainWindowPosition(handle: _handle, outX: out int x, outY: out int y);
        return (x, y);
    }

    /// <summary>
    ///     Hide or show the main window without destroying it — how an app keeps running with
    ///     nothing on screen. Showing raises it too, since the only reason to show a hidden window
    ///     is that the user asked for it back.
    /// </summary>
    public void MainWindowSetVisible(bool visible)
    {
        if (!_disposed)
            NativeEngine.MainWindowSetVisible(handle: _handle, visible: visible ? 1u : 0u);
    }

    /// <summary>
    ///     Whether the application appears in the Dock and the ⌘-Tab switcher (macOS; a no-op
    ///     elsewhere). Application-wide, not per-window: hiding the main window leaves the process
    ///     a foreground app, so an app with nothing on screen keeps a Dock tile that brings back
    ///     nothing. Showing it again also brings the app forward, which is what restores its menu
    ///     bar.
    /// </summary>
    public void AppSetDockVisible(bool visible)
    {
        if (!_disposed) NativeEngine.AppSetDockVisible(visible);
    }

    // ── Window chrome (in-app titlebars) ──────────────────────────────────────
    // All take the SDL window id (the ZgEvent.WindowId / FileDialog parenting domain).

    /// <summary>
    ///     Apply a chrome style to an OS window. MacUnified keeps the native traffic lights over
    ///     a full-size content view (macOS only — false elsewhere, callers fall back);
    ///     AdwaitaCsd makes the window borderless for app-drawn decorations. System restores the
    ///     default decorations.
    /// </summary>
    public bool WindowChromeSet(uint windowId, WindowChromeStyle style) => !_disposed &&
        NativeEngine.WindowChromeSet(windowId: windowId, style: (uint)style);

    /// <summary>
    ///     Declare the window's draggable titlebar rects (x,y,w,h quads, window-relative logical
    ///     coordinates, up to 4) — the strip the OS moves the window by. Empty clears.
    /// </summary>
    public void WindowChromeDragRects(uint windowId, ReadOnlySpan<float> quads)
    {
        if (_disposed) return;
        fixed (float* rects =
                   quads)
        {
            NativeEngine.WindowChromeDragRects(
                windowId: windowId,
                rects: rects,
                count: (uint)(quads.Length / 4)
            );
        }
    }

    /// <summary>
    ///     Diagnostic readback of the chrome actually applied to a window: 1 = macOS
    ///     unified titlebar live, 0 = system, negatives = not probeable (see chrome.zig).
    /// </summary>
    public int WindowChromeProbe(uint windowId) =>
        _disposed ? -2 : NativeEngine.WindowChromeProbe(windowId);

    /// <summary>
    ///     Re-assert a window's chrome if the OS dropped it (macOS clears the unified
    ///     titlebar on fullscreen/zoom round-trips). Cheap no-op when intact — call on window
    ///     resize events.
    /// </summary>
    public void WindowChromeSync(uint windowId)
    {
        if (!_disposed) NativeEngine.WindowChromeSync(windowId);
    }

    /// <summary>
    ///     Install the app-side drag arbiter the native titlebar hit-test consults per pointer
    ///     position: (windowId, x, y) → 1 draggable, 0 content, -1 fall back to the static drag
    ///     rects. Pass a [UnmanagedCallersOnly(Cdecl)] function pointer; 0 clears.
    /// </summary>
    public void WindowChromeSetHitProvider(nint provider)
    {
        if (_disposed) return;
        NativeEngine.WindowChromeSetHitProvider(
            (delegate* unmanaged[Cdecl]<uint, float, float, int>)provider
        );
    }

    /// <summary>
    ///     Tell the platform what radius a CSD window's corners are, in logical px. Only platforms
    ///     that round the frame themselves act on it — macOS masks the window's layer, which is how
    ///     its corners come out antialiased and correctly shadowed; elsewhere the app clips its own
    ///     corners and this is inert. Re-applied by <see cref="WindowChromeSet" />.
    /// </summary>
    public void WindowChromeSetCornerRadius(uint windowId, float radius)
    {
        if (!_disposed)
            NativeEngine.WindowChromeSetCornerRadius(windowId: windowId, radius: radius);
    }

    /// <summary>Minimize the window (client-side-decoration button action).</summary>
    public void WindowChromeMinimize(uint windowId)
    {
        if (!_disposed) NativeEngine.WindowChromeMinimize(windowId);
    }

    /// <summary>Maximize the window, or restore it when already maximized.</summary>
    public void WindowChromeToggleMaximize(uint windowId)
    {
        if (!_disposed) NativeEngine.WindowChromeToggleMaximize(windowId);
    }

    /// <summary>
    ///     Request an alpha-composited main window (CSD rounded corners). Must be called BEFORE
    ///     <see cref="Initialize" /> — transparency is a window-creation property. Whether it
    ///     actually took is reported by <see cref="WindowIsTransparent" />.
    /// </summary>
    public void SetWindowTransparent(bool enabled) => NativeEngine.SetWindowTransparent(enabled);

    /// <summary>The window is maximized or fullscreen — CSD hosts square their corners.</summary>
    public bool WindowIsMaximized(uint windowId) =>
        !_disposed && NativeEngine.WindowIsMaximized(windowId);

    /// <summary>Whether the window really got an alpha channel the compositor composites.</summary>
    public bool WindowIsTransparent(uint windowId) =>
        !_disposed && NativeEngine.WindowIsTransparent(windowId);

    /// <summary>
    ///     Current OS light/dark appearance. Live changes also arrive as
    ///     <see cref="SystemThemeEvent" />s from the poll loop.
    /// </summary>
    public SystemTheme GetSystemTheme()
    {
        EnsureReady();
        return (SystemTheme)NativeEngine.GetSystemTheme(_handle);
    }

    /// <summary>
    ///     The host's scroll orientation ("natural scroll" OS setting), latched from the last mouse
    ///     wheel event. Returns <see cref="ScrollOrientation.Unknown" /> until the user first scrolls —
    ///     SDL only surfaces the direction per wheel event, not as a queryable setting.
    /// </summary>
    public ScrollOrientation GetScrollOrientation()
    {
        EnsureReady();
        return (ScrollOrientation)NativeEngine.GetScrollOrientation(_handle);
    }

    /// <summary>
    ///     Main-window safe-area insets in logical pixels: the margins an app should keep clear
    ///     of OS obstructions (notch, rounded corners, home indicator, TV overscan). All-zero on
    ///     desktop. Re-query after a <see cref="ResizeEvent" /> — rotation moves the notch.
    /// </summary>
    public (float Left, float Top, float Right, float Bottom) GetSafeArea()
    {
        EnsureReady();
        Span<float> insets = stackalloc float[4];
        fixed (float* p = insets) NativeEngine.GetSafeArea(handle: _handle, insets: p);

        return (insets[0], insets[1], insets[2], insets[3]);
    }

    /// <summary>
    ///     Drop all native text caches (shaped runs, glyph atlases) on every window, forcing a
    ///     clean re-shape next frame. Call after a wholesale text sizing change (live UI
    ///     font-scale switch) — the same invalidation a font face swap performs.
    /// </summary>
    public void ResetTextCaches()
    {
        EnsureReady();
        NativeEngine.TextResetCaches(_handle);
    }

    /// <summary>
    ///     Open a secondary UI-only OS window (2D paint path; the 3D scene stays on the main
    ///     window). The returned <see cref="NativeWindow" /> owns the native resources — dispose it
    ///     to close the window.
    /// </summary>
    public NativeWindow CreateWindow(string title, uint width, uint height)
    {
        EnsureReady();
        byte[] titleBytes = [.. Encoding.UTF8.GetBytes(title), 0];
        ulong window;
        ZgResult result;
        fixed (byte* tp = titleBytes)
        {
            result = NativeEngine.WindowCreate(
                handle: _handle,
                width: width,
                height: height,
                title: tp,
                outWindow: out window
            );
        }

        if (result != ZgResult.Ok || window == 0)
        {
            throw new InvalidOperationException(
                "zigote_window_create failed. Check stderr for details."
            );
        }

        return new NativeWindow(
            engine: this,
            window: window,
            id: NativeEngine.WindowId(handle: _handle, windowHandle: window)
        );
    }

    private RendererCaps QueryRendererCaps()
    {
        NativeEngine.GetRendererCaps(handle: _handle, outCaps: out var caps);
        return RendererCaps.From(caps);
    }

    /// <summary>
    ///     Poll SDL3 events and return them as typed <see cref="InputEvent" /> instances.
    ///     Call once per frame before building the widget tree.
    /// </summary>
    public IEnumerable<InputEvent> PollEvents()
    {
        EnsureReady();
        uint count = PollEventsNative();
        IntPtr textBase = PollTextBase();

        for (uint i = 0; i < count; i++)
        {
            ref readonly var raw = ref _eventBuf[i];

            // A secondary window's resize must not clobber the main window's cached size. A display
            // change refreshes the same cache: moving to another monitor can change the HiDPI scale
            // and always invalidates the cached refresh rate.
            if ((EventKind)raw.Kind is EventKind.Resize or EventKind.DisplayChanged &&
                (raw.WindowId == 0 || raw.WindowId == MainWindowId))
                RefreshSize();
            if ((EventKind)raw.Kind == EventKind.Quit)
                ShouldQuit = true;

            var evt = EventDecoder.Decode(e: raw, textBase: textBase);
            if (evt is not null)
                yield return evt;
        }
    }

    /// <summary>
    ///     Allocation-free variant of <see cref="PollEvents" /> for the frame loop: drains the SDL3
    ///     queue into <paramref name="buffer" /> (cleared first) instead of returning an iterator. The
    ///     loop reuses a single buffer, so a frame with no events allocates nothing on this path —
    ///     unlike <c>PollEvents().ToList()</c>, which allocates an enumerator and a list every frame.
    ///     The flooding kinds (<see cref="MouseMoveEvent" /> / <see cref="ScrollEvent" />) are rented
    ///     from a per-poll pool and REUSED by the next call, so do not retain a reference to any polled
    ///     event past the next poll — dispatch and drop it within the frame. The remaining kinds fire
    ///     at human rates and are allocated per event.
    /// </summary>
    public void PollEventsInto(List<InputEvent> buffer)
    {
        buffer.Clear();
        _eventPool.Reset();
        EnsureReady();
        uint count = PollEventsNative();
        IntPtr textBase = PollTextBase();

        for (uint i = 0; i < count; i++)
        {
            ref readonly var raw = ref _eventBuf[i];

            // A secondary window's resize must not clobber the main window's cached size. A display
            // change refreshes the same cache: moving to another monitor can change the HiDPI scale
            // and always invalidates the cached refresh rate.
            if ((EventKind)raw.Kind is EventKind.Resize or EventKind.DisplayChanged &&
                (raw.WindowId == 0 || raw.WindowId == MainWindowId))
                RefreshSize();
            if ((EventKind)raw.Kind == EventKind.Quit)
                ShouldQuit = true;

            // The flooding event kinds are rented from the per-poll pool (no allocation once warm);
            // everything else fires at human rates and takes the plain allocating decode path.
            var evt = (EventKind)raw.Kind switch {
                // A move event carries no scroll, so native reuses those two slots for the frame's
                // relative motion (see zigote_poll_events).
                EventKind.MouseMove => _eventPool.RentMouseMove(
                    x: raw.X,
                    y: raw.Y,
                    windowId: raw.WindowId,
                    relativeX: raw.ScrollX,
                    relativeY: raw.ScrollY
                ),
                EventKind.Scroll => _eventPool.RentScroll(
                    x: raw.X,
                    y: raw.Y,
                    scrollX: raw.ScrollX,
                    scrollY: raw.ScrollY,
                    windowId: raw.WindowId
                ),
                EventKind.TouchMove => _eventPool.RentTouchMove(
                    x: raw.X,
                    y: raw.Y,
                    finger: (int)raw.TouchFinger,
                    pressure: raw.TouchPressure,
                    windowId: raw.WindowId
                ),
                _ => EventDecoder.Decode(e: raw, textBase: textBase),
            };
            if (evt is not null)
                buffer.Add(evt);
        }
    }

    /// <summary>
    ///     Measure text's bounding box. When <paramref name="fontFamily" /> names a registered face
    ///     (e.g. <c>"code"</c>, <c>"MaterialIcons"</c>) and an unbounded width is requested, the native
    ///     side shapes it accurately via HarfBuzz in that face; otherwise it falls back to a coarse
    ///     per-character estimate (used for wrapped/multi-line measurement and headless builds).
    /// </summary>
    public Size MeasureText(
        string text,
        float fontSize,
        float maxWidth = float.PositiveInfinity,
        FontWeight weight = FontWeight.Normal,
        FontStyle style = FontStyle.Normal,
        float letterSpacing = 0f,
        float wordSpacing = 0f,
        string? fontFamily = null)
    {
        if (string.IsNullOrEmpty(text)) return Size.Zero;
        fontFamily = FontFaces.Resolve(weight: weight, requested: fontFamily);

        // Stack-allocate the UTF-8 buffers for the common short-label case. MeasureText is called per
        // dynamic-text widget per frame (FPS counter, timers, coordinates, profiler) where the result
        // cache misses every frame — a per-call byte[] there is steady-state GC pressure.
        const int stackMax = 256;
        int textLen = Encoding.UTF8.GetByteCount(text);
        int familyLen = string.IsNullOrEmpty(fontFamily)
            ? 0
            : Encoding.UTF8.GetByteCount(fontFamily);
        var bytes = textLen <= stackMax ? stackalloc byte[textLen] : new byte[textLen];
        Encoding.UTF8.GetBytes(chars: text, bytes: bytes);
        var familyBytes = familyLen == 0
            ? default
            : familyLen <= stackMax
                ? stackalloc byte[familyLen]
                : new byte[familyLen];
        if (familyLen != 0) Encoding.UTF8.GetBytes(chars: fontFamily!, bytes: familyBytes);

        ZgSize result;
        fixed (byte* p = bytes)
        fixed (byte* fp = familyBytes)
        {
            result = NativeEngine.MeasureText(
                handle: _handle,
                text: p,
                textLen: (uint)textLen,
                fontSize: fontSize,
                maxWidth: float.IsPositiveInfinity(maxWidth) ? 0f : maxWidth,
                fontWeight: (ushort)weight,
                fontStyle: (byte)style,
                letterSpacing: letterSpacing,
                wordSpacing: wordSpacing,
                fontFamily: fp,
                fontFamilyLen: (uint)familyLen
            );
        }

        return new Size(width: result.Width, height: result.Height);
    }

    /// <summary>
    ///     Block until an SDL event arrives or <paramref name="timeoutMs" /> milliseconds elapse.
    ///     After returning, call <see cref="PollEvents" /> to drain the queue.
    ///     Lets the frame loop sleep instead of spinning when the UI is idle.
    /// </summary>
    public void WaitEvents(int timeoutMs = 16)
    {
        EnsureReady();
        NativeEngine.WaitEvents((uint)Math.Max(val1: 0, val2: timeoutMs)); // stateless — no handle
    }

    /// <summary>Read the current clipboard as a UTF-8 string. Returns empty if clipboard is empty.</summary>
    public string GetClipboard()
    {
        // Native writes up to capacity-1 bytes but returns the FULL clipboard length. A common stack
        // buffer covers the overwhelming majority of pastes; only genuinely large clipboard content
        // takes the second, heap-allocated pass — so long text is never silently truncated.
        const int stackCap = 8192;
        byte* stackBuf = stackalloc byte[stackCap];
        uint len = NativeEngine.GetClipboard(
            buf: stackBuf,
            capacity: stackCap
        ); // stateless — no handle
        if (len == 0) return string.Empty;
        if (len < stackCap) return Encoding.UTF8.GetString(bytes: stackBuf, byteCount: (int)len);

        byte[] heap = new byte[len + 1];
        fixed (byte* hp = heap)
        {
            uint written = NativeEngine.GetClipboard(buf: hp, capacity: len + 1);
            return Encoding.UTF8.GetString(
                bytes: hp,
                byteCount: (int)Math.Min(val1: written, val2: len)
            );
        }
    }

    /// <summary>Write <paramref name="text" /> to the system clipboard as UTF-8.</summary>
    public void SetClipboard(string text)
    {
        // Bounded like GetClipboard: the stack buffer covers typical copies; a large payload (a
        // whole-document copy) takes a heap array instead of an unbounded stackalloc.
        const int stackCap = 8192;
        int len = Encoding.UTF8.GetByteCount(text);
        var bytes = len < stackCap ? stackalloc byte[len + 1] : new byte[len + 1];
        Encoding.UTF8.GetBytes(chars: text, bytes: bytes);
        bytes[len] = 0;
        fixed (byte* p = bytes) NativeEngine.SetClipboard(p); // stateless — no handle
    }

    /// <summary>
    ///     Start a system drag-and-drop session carrying <paramref name="text" /> and/or
    ///     <paramref name="files" /> OUT of the app (app → OS: onto Finder, another app, …). Best-effort
    ///     and <b>macOS-only</b> — SDL3 exposes no portable drag-source API, so this is a no-op returning
    ///     <c>false</c> on other platforms. Even on macOS it only succeeds while a real pointer drag is in
    ///     progress (call it from a widget's drag handler), returning <c>false</c> otherwise so callers
    ///     can fall back. Files must be absolute paths.
    /// </summary>
    public bool BeginDragOut(string? text, IReadOnlyList<string>? files = null)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        string joined = files is { Count: > 0 }
            ? string.Join(separator: '\n', values: files)
            : string.Empty;
        return NativeEngine.MacDragBegin(text: text ?? string.Empty, filesNl: joined) != 0;
    }

    /// <summary>Enable SDL3 text-input so TEXT_INPUT events are generated. Call on text-field focus.</summary>
    public void StartTextInput() => NativeEngine.StartTextInput(_handle);

    /// <summary>Disable SDL3 text-input. Call when no text field is focused.</summary>
    public void StopTextInput() => NativeEngine.StopTextInput(_handle);

    /// <summary>Position the platform IME candidate window next to the active caret.</summary>
    public void SetTextInputArea(Rect area, int cursor = 0)
    {
        NativeEngine.SetTextInputArea(
            handle: _handle,
            x: (int)MathF.Round(area.X),
            y: (int)MathF.Round(area.Y),
            w: Math.Max(val1: 1, val2: (int)MathF.Round(area.Width)),
            h: Math.Max(val1: 1, val2: (int)MathF.Round(area.Height)),
            cursor: cursor
        );
    }

    /// <summary>Shut down the engine and release all GPU resources.</summary>
    public void Shutdown()
    {
        if (!_initialized) return;
        NativeEngine.Shutdown(_handle);
        _handle = 0;
        _initialized = false;
    }

    /// <summary>
    ///     Free a texture handle returned by any <c>LoadTexture*</c> call — both the decoded CPU
    ///     copy and the GPU texture. Safe to call from anywhere, including mid-layout: the engine
    ///     defers the actual free to the end of the current frame. Releasing <c>0</c>, an unknown
    ///     handle, or the same handle twice is a no-op.
    /// </summary>
    /// <remarks>
    ///     Texture handles are owned by the caller. Nothing else frees them, so an image-heavy UI
    ///     (a gallery, a reader) that never releases will grow until the GPU is exhausted.
    /// </remarks>
    public static void ReleaseTexture(ulong textureHandle)
    {
        // Deliberately tolerant rather than RequireInstance(): images are disposed during teardown,
        // when the engine may already be gone. There is nothing left to leak at that point.
        var engine = Instance;
        if (textureHandle == 0 || engine is null || engine._disposed) return;
        NativeEngine.ReleaseTexture(handle: engine._handle, imageHandle: textureHandle);
    }

    /// <summary>
    ///     Live texture accounting: handles outstanding, decoded bytes still held on the CPU (images
    ///     not yet painted — the copy is dropped once the GPU texture exists), and bytes resident on
    ///     the GPU. Drive a cache budget off <paramref name="gpuBytes" />; watch
    ///     <paramref name="count" /> to catch textures nobody released.
    /// </summary>
    public static void GetImageStats(out int count, out long cpuBytes, out long gpuBytes)
    {
        count = 0;
        cpuBytes = 0;
        gpuBytes = 0;
        var engine = Instance;
        if (engine is null || engine._disposed) return;

        NativeEngine.ImageStats(
            handle: engine._handle,
            outCount: out uint c,
            outCpuBytes: out ulong cpu,
            outGpuBytes: out ulong gpu
        );
        count = (int)c;
        cpuBytes = (long)cpu;
        gpuBytes = (long)gpu;
    }

    /// <summary>
    ///     Load a texture natively and return its cache handle. Thread-safe — decoding a large
    ///     image off the UI thread keeps the frame loop free; the GPU upload happens on the render
    ///     thread the first time the handle is painted. Release it with
    ///     <see cref="ReleaseTexture" />.
    /// </summary>
    public static ulong LoadTexture(string path, out uint outW, out uint outH)
    {
        var engine = RequireInstance();
        byte[] pathBytes = [.. Encoding.UTF8.GetBytes(path), 0];
        fixed (byte* p = pathBytes)
        {
            return NativeEngine.LoadTexture(
                handle: engine._handle,
                pathC: p,
                outW: out outW,
                outH: out outH
            );
        }
    }

    /// <summary>
    ///     Load a texture natively as an alpha mask and return its cache handle.
    /// </summary>
    public static ulong LoadTextureMask(string path, out uint outW, out uint outH)
    {
        var engine = RequireInstance();
        byte[] pathBytes = [.. Encoding.UTF8.GetBytes(path), 0];
        fixed (byte* p = pathBytes)
        {
            return NativeEngine.LoadTextureMask(
                handle: engine._handle,
                pathC: p,
                outW: out outW,
                outH: out outH
            );
        }
    }

    /// <summary>
    ///     Load a texture from memory and return its cache handle.
    /// </summary>
    public static ulong LoadTextureFromMemory(ReadOnlySpan<byte> data, out uint outW, out uint outH)
    {
        var engine = RequireInstance();
        fixed (byte* ptr = data)
        {
            return NativeEngine.LoadTextureFromMemory(
                handle: engine._handle,
                dataPtr: ptr,
                dataLen: (nuint)data.Length,
                outW: out outW,
                outH: out outH
            );
        }
    }

    /// <summary>
    ///     Register already-decoded RGBA8 pixels (<c>width × height × 4</c> bytes, row-major) as a
    ///     texture. For pixels the caller already has — procedural content, a video frame, a decode
    ///     done elsewhere — which would otherwise have to be re-encoded to PNG just to get past the
    ///     decoder. Thread-safe; release with <see cref="ReleaseTexture" />.
    /// </summary>
    public static ulong LoadTextureFromRgba(ReadOnlySpan<byte> rgba, uint width, uint height)
    {
        var engine = RequireInstance();
        fixed (byte* ptr = rgba)
        {
            return NativeEngine.LoadTextureFromRgba(
                handle: engine._handle,
                pixelsPtr: ptr,
                pixelsLen: (nuint)rgba.Length,
                width: width,
                height: height
            );
        }
    }

    /// <summary>
    ///     Rewrite an existing texture's pixels, keeping the handle, its GPU texture and its bind
    ///     group. For a source of frames — a video, a camera, a procedural surface — where
    ///     <see cref="LoadTextureFromRgba" /> plus <see cref="ReleaseTexture" /> would build and tear
    ///     down a GPU texture sixty times a second to show the same rectangle.
    ///     <para>
    ///         <paramref name="width" /> and <paramref name="height" /> must match the handle's: this
    ///         overwrites, it does not resize. Returns false for an unknown handle, a size mismatch or
    ///         a short buffer — a caller whose resolution changed should create a new handle.
    ///     </para>
    ///     <para>
    ///         Thread-safe. The texel upload happens at the top of the next frame, on the render
    ///         thread, so the pixels must be complete when this returns.
    ///     </para>
    /// </summary>
    public static bool UpdateTextureRgba(ulong textureHandle, ReadOnlySpan<byte> rgba, uint width,
        uint height)
    {
        var engine = Instance;
        if (engine is null || textureHandle == 0) return false;

        fixed (byte* ptr = rgba)
        {
            return NativeEngine.UpdateTextureRgba(
                handle: engine._handle,
                imageHandle: textureHandle,
                pixelsPtr: ptr,
                pixelsLen: (nuint)rgba.Length,
                width: width,
                height: height
            );
        }
    }

    /// <summary>
    ///     Load a texture from memory, box-downsampled so neither axis exceeds <paramref name="maxDim" />
    ///     (0 = no scaling). Bounds CPU + GPU memory for image-heavy UIs whose source images are much
    ///     larger
    ///     than they are displayed. Returns the cache handle.
    /// </summary>
    public static ulong LoadTextureFromMemoryScaled(ReadOnlySpan<byte> data, uint maxDim,
        out uint outW, out uint outH)
    {
        var engine = RequireInstance();
        fixed (byte* ptr = data)
        {
            return NativeEngine.LoadTextureFromMemoryScaled(
                handle: engine._handle,
                dataPtr: ptr,
                dataLen: (nuint)data.Length,
                maxDim: maxDim,
                outW: out outW,
                outH: out outH
            );
        }
    }

    // ── 3D Scene FFI ──────────────────────────────────────────────────────────────

    public void SceneClear()
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        NativeEngine.SceneClear(_handle);
    }

    public ulong SceneAddChildNode(ulong parentHandle, string name, byte kind)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        // Null-terminated UTF-8 on the stack (heap fallback for long names). Called per node during
        // World.Spawn / additive scene load, so the old `GetBytes(name + "\0")` was a transient string
        // + a byte[] per node.
        int len = Encoding.UTF8.GetByteCount(name);
        var buf = len < StackStringMax ? stackalloc byte[len + 1] : new byte[len + 1];
        Encoding.UTF8.GetBytes(chars: name, bytes: buf);
        buf[len] = 0;
        fixed (byte* namePtr = buf)
        {
            return NativeEngine.SceneAddChildNode(
                handle: _handle,
                parentHandle: parentHandle,
                namePtr: namePtr,
                kind: kind
            );
        }
    }

    /// <summary>
    ///     Upload a `.zmesh` geometry blob (engine vertex layout, produced by the Assimp importer
    ///     or read back from the mesh cache) to a node's mesh renderer.
    /// </summary>
    public void SceneSetMeshBlob(ulong nodeHandle, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        fixed (byte* ptr = data)
        {
            NativeEngine.SceneSetMeshBlob(
                handle: _handle,
                nodeHandle: nodeHandle,
                dataPtr: ptr,
                dataLen: (nuint)data.Length
            );
        }
    }

    /// <summary>
    ///     Import a model file of any Assimp-supported format (glTF/GLB, FBX, OBJ, DAE, …).
    ///     Native parses the file, writes per-mesh <c>.zmesh</c> caches and extracted textures into
    ///     <paramref name="cacheDir" /> (which must already exist), and returns a JSON manifest
    ///     describing the node tree, materials, lights and animations. Returns null on failure.
    /// </summary>
    public string? ModelImport(string path, string cacheDir)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        int pathLen = Encoding.UTF8.GetByteCount(path);
        int cacheLen = Encoding.UTF8.GetByteCount(cacheDir);
        var pathBuf = pathLen < StackStringMax
            ? stackalloc byte[pathLen + 1]
            : new byte[pathLen + 1];
        var cacheBuf =
            cacheLen < StackStringMax ? stackalloc byte[cacheLen + 1] : new byte[cacheLen + 1];
        Encoding.UTF8.GetBytes(chars: path, bytes: pathBuf);
        Encoding.UTF8.GetBytes(chars: cacheDir, bytes: cacheBuf);
        pathBuf[pathLen] = 0;
        cacheBuf[cacheLen] = 0;
        fixed (byte* pathPtr = pathBuf)
        fixed (byte* cachePtr = cacheBuf)
        {
            byte* result = NativeEngine.ModelImport(pathC: pathPtr, cacheDirC: cachePtr);
            if (result == null) return null;
            try
            {
                return Marshal.PtrToStringUTF8((IntPtr)result);
            }
            finally
            {
                NativeEngine.ModelFree(result);
            }
        }
    }

    public void SceneSetMeshPrimitive(ulong nodeHandle, byte primType)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshPrimitive(
            handle: _handle,
            nodeHandle: nodeHandle,
            primType: primType
        );
    }

    /// <summary>
    ///     Toggle a mesh node's visibility in the renderer. Unlike scaling to zero (which still issues
    ///     the draw), an invisible node is skipped entirely — a real draw-call cull. Drives the C# LOD /
    ///     distance-culling system.
    /// </summary>
    public void SceneSetNodeVisible(ulong nodeHandle, bool visible)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetNodeVisible(
            handle: _handle,
            nodeHandle: nodeHandle,
            visible: visible ? 1u : 0u
        );
    }

    /// <summary>Enable/disable native world-3D frustum culling (wgpu reference renderer; on by default).</summary>
    public void RenderSetFrustumCull(bool enabled)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        NativeEngine.RenderSetFrustumCull(handle: _handle, enabled: enabled ? 1u : 0u);
    }

    // ── Game controllers (SDL gamepad, up to 8 player slots) ─────────────────────

    /// <summary>Number of connected controllers (0-8); slots are packed from 0. Hotplug-aware.</summary>
    public int GamepadCount() => _disposed ? 0 : (int)NativeEngine.InputGamepadCount(_handle);

    /// <summary>True when the game controller in slot <paramref name="pad" /> is connected.</summary>
    public bool GamepadConnected(int pad = 0) => !_disposed &&
                                                 NativeEngine.InputGamepadConnected(
                                                     handle: _handle,
                                                     pad: (byte)pad
                                                 ) != 0;

    /// <summary>
    ///     Read a controller axis, normalised to [-1, 1] (triggers [0, 1]). See <c>GamepadAxis</c>
    ///     order.
    /// </summary>
    public float GamepadAxis(int pad, int axis) => _disposed
        ? 0f
        : NativeEngine.InputGamepadAxis(handle: _handle, pad: (byte)pad, axis: (byte)axis);

    /// <summary>True while a controller button is held. See <c>GamepadButton</c> order.</summary>
    public bool GamepadButton(int pad, int button) => !_disposed &&
                                                      NativeEngine.InputGamepadButton(
                                                          handle: _handle,
                                                          pad: (byte)pad,
                                                          button: (byte)button
                                                      ) != 0;

    // ── Audio (miniaudio software synth, lazily opened on first use) ──────────────
    // Waveform codes: 0 sine, 1 square, 2 triangle, 3 sawtooth, 4 noise.

    /// <summary>
    ///     Play a one-shot tone (UI click / blip / beep): frequency in Hz, duration in seconds,
    ///     volume [0,1].
    /// </summary>
    public void AudioBeep(float frequencyHz, float durationSeconds, float volume, int waveform)
    {
        if (!_disposed)
        {
            NativeEngine.AudioBeep(
                handle: _handle,
                freq: frequencyHz,
                duration: durationSeconds,
                volume: volume,
                waveform: (byte)waveform
            );
        }
    }

    /// <summary>
    ///     Set a sustained tone on a channel (held until changed). volume&lt;=0 or freq&lt;=0
    ///     silences it.
    /// </summary>
    public void AudioVoice(int channel, float frequencyHz, float volume, int waveform)
    {
        if (!_disposed)
        {
            NativeEngine.AudioVoice(
                handle: _handle,
                channel: (uint)Math.Max(val1: 0, val2: channel),
                freq: frequencyHz,
                volume: volume,
                waveform: (byte)waveform
            );
        }
    }

    /// <summary>Silence every voice (one-shots, sustained channels, and all handle sources).</summary>
    public void AudioStopAll()
    {
        if (!_disposed) NativeEngine.AudioStopAll(_handle);
    }

    // ── Spatial / surround audio (miniaudio engine) ──────────────────────────────

    /// <summary>Age + reap fire-and-forget one-shots. Call once per frame from the host loop.</summary>
    public void AudioUpdate(float dt)
    {
        if (!_disposed) NativeEngine.AudioUpdate(handle: _handle, dt: dt);
    }

    /// <summary>Set the spatial listener pose; every spatialised sound is panned + attenuated against it.</summary>
    public void AudioSetListener(Vec3 position, Vec3 forward, Vec3 up)
    {
        if (_disposed) return;
        NativeEngine.AudioSetListener(
            handle: _handle,
            px: position.X,
            py: position.Y,
            pz: position.Z,
            fx: forward.X,
            fy: forward.Y,
            fz: forward.Z,
            ux: up.X,
            uy: up.Y,
            uz: up.Z
        );
    }

    /// <summary>Master output volume [0,4]; 1 = unity.</summary>
    public void AudioSetMasterVolume(float volume)
    {
        if (!_disposed) NativeEngine.AudioSetMasterVolume(handle: _handle, volume: volume);
    }

    /// <summary>Positioned procedural one-shot (spatialised + attenuated). Waveform code 0..4.</summary>
    public void AudioBeep3D(Vec3 position, float frequencyHz, float durationSeconds, float volume,
        int waveform, float minDistance, float maxDistance, float rolloff)
    {
        if (_disposed) return;
        NativeEngine.AudioBeep3D(
            handle: _handle,
            px: position.X,
            py: position.Y,
            pz: position.Z,
            freq: frequencyHz,
            duration: durationSeconds,
            volume: volume,
            waveform: (byte)waveform,
            minDist: minDistance,
            maxDist: maxDistance,
            rolloff: rolloff
        );
    }

    /// <summary>
    ///     Create a sustained procedural-tone source (not started). Returns a handle id (0 =
    ///     failure).
    /// </summary>
    public uint AudioSoundCreateTone(float frequencyHz, int waveform)
    {
        return _disposed
            ? 0u
            : NativeEngine.AudioSoundCreateTone(
                handle: _handle,
                freq: frequencyHz,
                waveform: (byte)waveform
            );
    }

    /// <summary>
    ///     Create a source from a decoded/streamed audio file (not started). Returns a handle id (0 =
    ///     failure).
    /// </summary>
    public uint AudioSoundCreateFile(string path, bool streaming)
    {
        if (_disposed || string.IsNullOrEmpty(path)) return 0;
        byte[] pathBytes = [.. Encoding.UTF8.GetBytes(path), 0];
        fixed (byte* p = pathBytes)
        {
            return NativeEngine.AudioSoundCreateFile(
                handle: _handle,
                pathC: p,
                streaming: streaming ? 1u : 0u
            );
        }
    }

    /// <summary>
    ///     Create a source fed by <see cref="AudioStreamPush" /> rather than by a file (not
    ///     started). Returns a handle id (0 = failure).
    ///     <para>
    ///         A file source pulls; this one is pushed, which is the only shape a socket fits. What
    ///         comes back is otherwise an ordinary sound: the same volume, equalizer routing and
    ///         transport calls apply.
    ///     </para>
    /// </summary>
    public uint AudioStreamCreate() => _disposed ? 0u : NativeEngine.AudioStreamCreate(_handle);

    /// <summary>
    ///     Hand encoded bytes to a stream source. Returns how many were accepted — a short count
    ///     means its queue is full and the caller should stop reading until it drains, which is what
    ///     keeps a radio station from buffering into memory without bound.
    /// </summary>
    public int AudioStreamPush(uint id, ReadOnlySpan<byte> bytes)
    {
        if (_disposed || id == 0 || bytes.IsEmpty) return 0;
        fixed (byte* p = bytes)
        {
            return (int)NativeEngine.AudioStreamPush(
                handle: _handle,
                id: id,
                data: p,
                len: (uint)bytes.Length
            );
        }
    }

    /// <summary>
    ///     No more bytes are coming. What is already queued still plays out, and the sound reports
    ///     end-of-stream once it does — so a finished stream auto-advances like a finished file.
    /// </summary>
    public void AudioStreamFinish(uint id)
    {
        if (!_disposed) NativeEngine.AudioStreamFinish(handle: _handle, id: id);
    }

    public AudioStreamState AudioStreamStatus(uint id)
    {
        return _disposed
            ? AudioStreamState.Unsupported
            : (AudioStreamState)NativeEngine.AudioStreamState(handle: _handle, id: id);
    }

    /// <summary>
    ///     Decoded audio held ahead of the mixer, in seconds. What a "Buffering…" indicator shows,
    ///     and what tells a player it is safe to start.
    /// </summary>
    public float AudioStreamBuffered(uint id) =>
        _disposed ? 0f : NativeEngine.AudioStreamBuffered(handle: _handle, id: id);

    public void AudioSoundPlay(uint id)
    {
        if (!_disposed) NativeEngine.AudioSoundPlay(handle: _handle, id: id);
    }

    public void AudioSoundStop(uint id)
    {
        if (!_disposed) NativeEngine.AudioSoundStop(handle: _handle, id: id);
    }

    public void AudioSoundDestroy(uint id)
    {
        if (!_disposed) NativeEngine.AudioSoundDestroy(handle: _handle, id: id);
    }

    public void AudioSoundSetVolume(uint id, float volume)
    {
        if (!_disposed) NativeEngine.AudioSoundSetVolume(handle: _handle, id: id, volume: volume);
    }

    public void AudioSoundSetPitch(uint id, float pitch)
    {
        if (!_disposed) NativeEngine.AudioSoundSetPitch(handle: _handle, id: id, pitch: pitch);
    }

    public void AudioSoundSetLooping(uint id, bool looping)
    {
        if (!_disposed)
            NativeEngine.AudioSoundSetLooping(handle: _handle, id: id, looping: looping ? 1u : 0u);
    }

    public void AudioSoundSetSpatial(uint id, bool enabled)
    {
        if (!_disposed)
            NativeEngine.AudioSoundSetSpatial(handle: _handle, id: id, enabled: enabled ? 1u : 0u);
    }

    public void AudioSoundSetPosition(uint id, Vec3 position)
    {
        if (!_disposed)
        {
            NativeEngine.AudioSoundSetPosition(
                handle: _handle,
                id: id,
                x: position.X,
                y: position.Y,
                z: position.Z
            );
        }
    }

    public void AudioSoundSetVelocity(uint id, Vec3 velocity)
    {
        if (!_disposed)
        {
            NativeEngine.AudioSoundSetVelocity(
                handle: _handle,
                id: id,
                x: velocity.X,
                y: velocity.Y,
                z: velocity.Z
            );
        }
    }

    public void AudioSoundSetAttenuation(uint id, float minDistance, float maxDistance,
        float rolloff)
    {
        if (!_disposed)
        {
            NativeEngine.AudioSoundSetAttenuation(
                handle: _handle,
                id: id,
                minDist: minDistance,
                maxDist: maxDistance,
                rolloff: rolloff
            );
        }
    }

    public bool AudioSoundIsPlaying(uint id) =>
        !_disposed && NativeEngine.AudioSoundIsPlaying(handle: _handle, id: id) != 0;

    /// <summary>
    ///     Create a mixer bus (miniaudio sound group). Returns a bus id (0 = failure). Buses live
    ///     until the engine's audio state is torn down — there is deliberately no per-bus destroy.
    /// </summary>
    public uint AudioGroupCreate() => _disposed ? 0u : NativeEngine.AudioGroupCreate(_handle);

    public void AudioGroupSetVolume(uint groupId, float volume)
    {
        if (!_disposed)
            NativeEngine.AudioGroupSetVolume(handle: _handle, groupId: groupId, volume: volume);
    }

    public void AudioGroupSetPitch(uint groupId, float pitch)
    {
        if (!_disposed)
            NativeEngine.AudioGroupSetPitch(handle: _handle, groupId: groupId, pitch: pitch);
    }

    /// <summary>Route a sound through a bus (bus 0 = back to the master output).</summary>
    public void AudioSoundSetGroup(uint id, uint groupId)
    {
        if (!_disposed) NativeEngine.AudioSoundSetGroup(handle: _handle, id: id, groupId: groupId);
    }

    // ── Device rate (high-resolution playback) ────────────────────────────────────

    /// <summary>The output device's current sample rate in Hz; 0 when there is no audio device.</summary>
    public int AudioOutputRate() => _disposed ? 0 : (int)NativeEngine.AudioOutputRate(_handle);

    /// <summary>
    ///     Reopen the audio device at <paramref name="sampleRateHz" /> (0 = the device's preferred
    ///     rate), returning the rate actually achieved (0 = failure, sound now disabled).
    ///     <para>
    ///         The rate is fixed at device creation, so this rebuilds the engine:
    ///         <b>
    ///             every sound,
    ///             mixer bus and equalizer chain id becomes invalid
    ///         </b>
    ///         and must be recreated. Playing a
    ///         source at its own rate is the only way to avoid resampling it, which is the whole
    ///         point of high-resolution audio.
    ///     </para>
    /// </summary>
    public int AudioReopen(int sampleRateHz)
    {
        return _disposed
            ? 0
            : (int)NativeEngine.AudioReopen(
                handle: _handle,
                sampleRate: (uint)Math.Max(val1: 0, val2: sampleRateHz)
            );
    }

    // ── Transport (media playback: seek + position) ───────────────────────────────

    /// <summary>Seek a sound to an absolute position in seconds.</summary>
    public void AudioSoundSeek(uint id, float seconds)
    {
        if (!_disposed) NativeEngine.AudioSoundSeek(handle: _handle, id: id, seconds: seconds);
    }

    /// <summary>Playback cursor in seconds; -1 when the source cannot report one.</summary>
    public float AudioSoundCursor(uint id) =>
        _disposed ? -1f : NativeEngine.AudioSoundCursor(handle: _handle, id: id);

    /// <summary>Total length in seconds; -1 when unknown (procedural tones, unseekable streams).</summary>
    public float AudioSoundDuration(uint id) =>
        _disposed ? -1f : NativeEngine.AudioSoundDuration(handle: _handle, id: id);

    /// <summary>
    ///     The source decoded past its last frame — the auto-advance signal for a playlist. Unlike
    ///     <c>!AudioSoundIsPlaying</c> this stays false for a sound that was merely paused.
    /// </summary>
    public bool AudioSoundAtEnd(uint id) =>
        !_disposed && NativeEngine.AudioSoundAtEnd(handle: _handle, id: id) != 0;

    /// <summary>
    ///     Start a sound at an exact point on the audio clock, <paramref name="secondsFromNow" />
    ///     ahead of now — the primitive gapless playback is built on. Scheduling on the audio thread
    ///     is the only way to hit the boundary exactly; polling can never be tighter than a frame.
    /// </summary>
    public void AudioSoundScheduleStart(uint id, float secondsFromNow)
    {
        if (!_disposed)
        {
            NativeEngine.AudioSoundScheduleStart(
                handle: _handle,
                id: id,
                secondsFromNow: secondsFromNow
            );
        }
    }

    // ── Equalizer chains ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Create a chain of <paramref name="bandCount" /> biquad filters (max 16), flat until
    ///     configured, spliced between the sounds routed through it and the master output. Returns a
    ///     chain id (0 = failure).
    /// </summary>
    public uint AudioEqCreate(int bandCount) => _disposed
        ? 0u
        : NativeEngine.AudioEqCreate(
            handle: _handle,
            bandCount: (uint)Math.Max(val1: 1, val2: bandCount)
        );

    /// <summary>
    ///     Configure one band. Shelves take Q (converted to the RBJ slope inside the engine), matching
    ///     how AutoEq and every parametric EQ UI specify them. Re-tuning a band without changing its
    ///     <paramref name="kind" /> reconfigures the filter in place — no clicks, no graph churn.
    /// </summary>
    public void AudioEqSetBand(uint eqId, int index, AudioBandKind kind, float freqHz, float gainDb,
        float q)
    {
        if (!_disposed)
        {
            NativeEngine.AudioEqSetBand(
                handle: _handle,
                eqId: eqId,
                index: (uint)index,
                kind: (byte)kind,
                freqHz: freqHz,
                gainDb: gainDb,
                q: q
            );
        }
    }

    /// <summary>Bypass or engage the chain without losing its band settings (the A/B lever).</summary>
    public void AudioEqSetEnabled(uint eqId, bool enabled)
    {
        if (!_disposed)
            NativeEngine.AudioEqSetEnabled(handle: _handle, eqId: eqId, enabled: enabled ? 1u : 0u);
    }

    public void AudioEqDestroy(uint eqId)
    {
        if (!_disposed) NativeEngine.AudioEqDestroy(handle: _handle, eqId: eqId);
    }

    /// <summary>Route a sound through an equalizer chain (chain 0 = dry).</summary>
    public void AudioSoundSetEq(uint id, uint eqId)
    {
        if (!_disposed) NativeEngine.AudioSoundSetEq(handle: _handle, id: id, eqId: eqId);
    }

    // ── Offline decoding ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Decode a whole audio file to interleaved float samples at its native rate and channel count
    ///     — for callers that need the samples rather than playback (waveform overviews, loudness
    ///     analysis, sampler/IR loading). Needs no audio device. Returns an empty array on failure.
    ///     <para>Blocking and allocating: call it from a background thread, never an audio callback.</para>
    /// </summary>
    public float[] AudioDecodeFile(string path, out int channels, out int sampleRate)
    {
        channels = 0;
        sampleRate = 0;
        if (_disposed || string.IsNullOrEmpty(path)) return [];

        byte[] pathBytes = [.. Encoding.UTF8.GetBytes(path), 0];
        nuint buffer;
        uint nativeChannels;
        uint nativeRate;
        ulong frames;
        fixed (byte* p = pathBytes)
        {
            buffer = NativeEngine.AudioDecodeFile(
                handle: _handle,
                pathC: p,
                outChannels: out nativeChannels,
                outSampleRate: out nativeRate,
                outFrameCount: out frames
            );
        }

        if (buffer == 0) return [];
        try
        {
            // frames * channels can only overflow int for absurd inputs (a ~3 hour stereo file is
            // ~1e9 samples); refuse rather than truncate into a short read.
            ulong total = frames * nativeChannels;
            if (total == 0 || total > int.MaxValue) return [];

            float[] samples = new float[(int)total];
            new ReadOnlySpan<float>(pointer: (void*)buffer, length: (int)total).CopyTo(samples);
            channels = (int)nativeChannels;
            sampleRate = (int)nativeRate;
            return samples;
        }
        finally
        {
            NativeEngine.AudioDecodeFree(handle: _handle, frames: buffer);
        }
    }

    public void SceneSetLightProperties(ulong nodeHandle, byte kind, float r, float g, float b,
        float intensity,
        float range, float innerAngle, float outerAngle, bool castShadows)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetLightProperties(
            handle: _handle,
            nodeHandle: nodeHandle,
            kind: kind,
            r: r,
            g: g,
            b: b,
            intensity: intensity,
            range: range,
            innerAngle: innerAngle,
            outerAngle: outerAngle,
            castShadows: castShadows ? 1u : 0u
        );
    }

    public void SceneSetMeshColor(ulong nodeHandle, float r, float g, float b)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshColor(
            handle: _handle,
            nodeHandle: nodeHandle,
            r: r,
            g: g,
            b: b
        );
    }

    /// <summary>
    ///     Submit per-instance model matrices for a node's mesh (GPU instancing).
    ///     <paramref name="matrices" />
    ///     holds <paramref name="count" /> column-major 4×4 matrices (16 floats each); the node then draws
    ///     as
    ///     <paramref name="count" /> instances of its shared mesh+material in one instanced draw, ignoring
    ///     its
    ///     own transform. Pass <paramref name="count" /> = 0 to draw nothing for this node (an instanced
    ///     node
    ///     is never rendered as a single fallback draw — use this to empty a LOD bucket without a stray
    ///     mesh).
    /// </summary>
    public void SceneSetMeshInstances(ulong nodeHandle, ReadOnlySpan<float> matrices, uint count)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        if (count == 0)
        {
            NativeEngine.SceneSetMeshInstances(
                handle: _handle,
                nodeHandle: nodeHandle,
                matrices: null,
                count: 0
            );
            return;
        }

        fixed (float* ptr = matrices)
        {
            NativeEngine.SceneSetMeshInstances(
                handle: _handle,
                nodeHandle: nodeHandle,
                matrices: ptr,
                count: count
            );
        }
    }

    /// <summary>
    ///     Upload one VFX emitter node's particles for the native billboard render pass.
    ///     <paramref name="data" /> holds <paramref name="count" /> particles × 9 floats:
    ///     position.xyz, size, rotation, colour.rgba. <paramref name="blend" />: 0 = additive, 1 = alpha.
    ///     Count 0 clears the node's batch. The native pass is lazy + failure-isolated (no-op until used).
    /// </summary>
    public void ParticlesUpload(ulong nodeHandle, ReadOnlySpan<float> data, uint count, uint blend)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        if (count == 0)
        {
            NativeEngine.ParticlesClear(handle: _handle, nodeHandle: nodeHandle);
            return;
        }

        fixed (float* ptr = data)
        {
            NativeEngine.ParticlesUpload(
                handle: _handle,
                nodeHandle: nodeHandle,
                data: ptr,
                count: count,
                blend: blend
            );
        }
    }

    /// <summary>Clear one emitter node's uploaded particles (its node was removed / it stopped).</summary>
    public void ParticlesClear(ulong nodeHandle)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle != 0) NativeEngine.ParticlesClear(handle: _handle, nodeHandle: nodeHandle);
    }

    /// <summary>Drop all uploaded particle batches (play stop).</summary>
    public void ParticlesClearAll()
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        NativeEngine.ParticlesClearAll(_handle);
    }

    /// <summary>
    ///     Register/update one emitter for the native GPU compute simulation.
    ///     <paramref name="paramsData" />
    ///     is the 112-float kernel UBO (see <see cref="Zigote.Vfx.VfxGpuParams" />); the GPU spawns +
    ///     updates
    ///     <paramref name="capacity" /> particles and writes the billboard instance buffer.
    ///     <paramref name="blend" />: 0 = additive, 1 = alpha.
    /// </summary>
    public void ParticlesComputeEmit(ulong nodeHandle, ReadOnlySpan<float> paramsData,
        uint capacity, uint blend)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        fixed (float* ptr = paramsData)
        {
            NativeEngine.ParticlesComputeEmit(
                handle: _handle,
                nodeHandle: nodeHandle,
                paramValues: ptr,
                paramCount: (uint)paramsData.Length,
                capacity: capacity,
                blend: blend
            );
        }
    }

    /// <summary>Drop a GPU-compute emitter's buffers (its node was removed / it stopped).</summary>
    public void ParticlesComputeClear(ulong nodeHandle)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle != 0)
            NativeEngine.ParticlesComputeClear(handle: _handle, nodeHandle: nodeHandle);
    }

    // Immediate-mode frame model (see wgpu_sprites.zig): SpritesBegin once per frame with the
    // scene + overlay cameras, then SpritesDraw per pre-sorted batch of 16-float instances
    // (pos.xyz, rot, size.xy, uv0.xy, uv1.xy, rgba). Textures/shaders are u32 handles (0 = none).

    /// <summary>
    ///     Create a sprite texture from tightly-packed RGBA8 pixels. filter: 0 nearest / 1 linear;
    ///     srgb: 1 for color art, 0 for data textures (masks/LUTs); wrap: 0 clamp / 1 repeat.
    ///     Returns the texture handle, or 0 on failure (dimensions must be 1..8192).
    /// </summary>
    public uint SpritesTextureCreate(ReadOnlySpan<byte> rgba, uint width, uint height, uint filter,
        uint srgb,
        uint wrap)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (rgba.Length < width * height * 4) return 0;
        fixed (byte* ptr = rgba)
        {
            return NativeEngine.SpritesTextureCreate(
                handle: _handle,
                pixels: ptr,
                width: width,
                height: height,
                filter: filter,
                srgb: srgb,
                wrap: wrap
            );
        }
    }

    /// <summary>Create a sprite texture by decoding an image file (PNG/JPG/WebP/GIF).</summary>
    public uint SpritesTextureCreateFile(string path, uint filter, uint srgb, uint wrap,
        out uint outW, out uint outH)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        byte[] pathBytes = [.. Encoding.UTF8.GetBytes(path), 0];
        fixed (byte* p = pathBytes)
        {
            return NativeEngine.SpritesTextureCreateFile(
                handle: _handle,
                pathC: p,
                filter: filter,
                srgb: srgb,
                wrap: wrap,
                outW: out outW,
                outH: out outH
            );
        }
    }

    public void SpritesTextureDestroy(uint texture)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (texture != 0) NativeEngine.SpritesTextureDestroy(handle: _handle, texture: texture);
    }

    /// <summary>
    ///     Compile a custom sprite shader (WGSL; contract in sprite_shader_source.wgsl — vs_main/fs_main
    ///     over the sprite instance layout, premultiplied-alpha output). Returns 0 if rejected.
    /// </summary>
    public uint SpritesShaderCreate(string wgsl)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        byte[] bytes = Encoding.UTF8.GetBytes(wgsl);
        fixed (byte* p =
                   bytes)
        {
            return NativeEngine.SpritesShaderCreate(
                handle: _handle,
                wgslPtr: p,
                wgslLen: (uint)bytes.Length
            );
        }
    }

    /// <summary>
    ///     Start the sprite frame: column-major view-projections for the scene stage (world camera)
    ///     and the overlay stage (usually pixel-space ortho), plus the viewport size in pixels.
    /// </summary>
    public void SpritesBegin(ReadOnlySpan<float> sceneViewProj, ReadOnlySpan<float> overlayViewProj,
        float viewportW,
        float viewportH)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (sceneViewProj.Length < 16 || overlayViewProj.Length < 16) return;
        fixed (float* s = sceneViewProj)
        fixed (float* o = overlayViewProj)
        {
            NativeEngine.SpritesBegin(
                handle: _handle,
                sceneVp: s,
                overlayVp: o,
                viewportW: viewportW,
                viewportH: viewportH
            );
        }
    }

    /// <summary>
    ///     Append one pre-sorted sprite batch (count × 16 floats). texture2 feeds custom shaders'
    ///     secondary slot (0 = white); blend: 0 alpha / 1 additive / 2 opaque; stage: 0 scene / 1 overlay.
    /// </summary>
    public void SpritesDraw(uint texture, uint texture2, uint shader, uint blend, uint stage,
        ReadOnlySpan<float> materialParams, ReadOnlySpan<float> instances, uint count)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (count == 0 || texture == 0 || instances.Length < count * SpriteInstanceFloats) return;
        fixed (float* pp = materialParams)
        fixed (float* ip = instances)
        {
            NativeEngine.SpritesDraw(
                handle: _handle,
                texture: texture,
                texture2: texture2,
                shader: shader,
                blend: blend,
                stage: stage,
                paramValues: pp,
                paramCount: (uint)materialParams.Length,
                data: ip,
                count: count
            );
        }
    }

    public void SceneSetMeshRoughness(ulong nodeHandle, float metallic, float roughness)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshRoughness(
            handle: _handle,
            nodeHandle: nodeHandle,
            metallic: metallic,
            roughness: roughness
        );
    }

    /// <summary>
    ///     Set a camera node's projection: perspective vertical FOV (degrees) + near/far clip planes.
    ///     Applied by the renderer next frame. Callers driving this dynamically (physical camera) must
    ///     keep the published culling frustum in sync — see <c>PlaySession.PublishRenderView</c>.
    /// </summary>
    public void SceneSetCameraParams(ulong nodeHandle, float fovyDegrees, float near, float far)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetCameraParams(
            handle: _handle,
            nodeHandle: nodeHandle,
            fovyDegrees: fovyDegrees,
            near: near,
            far: far
        );
    }

    /// <summary>
    ///     Set extended-PBR surface params: clearcoat factor/roughness and specular scale
    ///     (KHR_materials_clearcoat / _specular / _ior).
    /// </summary>
    public void SceneSetMeshSurface(ulong nodeHandle, float clearcoat, float clearcoatRoughness,
        float specular)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshSurface(
            handle: _handle,
            nodeHandle: nodeHandle,
            clearcoat: clearcoat,
            clearcoatRoughness: clearcoatRoughness,
            specular: specular
        );
    }

    /// <summary>Set the mesh emissive colour (pre-scaled by KHR_materials_emissive_strength).</summary>
    public void SceneSetMeshEmissive(ulong nodeHandle, float r, float g, float b)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshEmissive(
            handle: _handle,
            nodeHandle: nodeHandle,
            r: r,
            g: g,
            b: b
        );
    }

    /// <summary>
    ///     Switch image-based lighting to an HDRI / equirectangular panorama (encoded image
    ///     bytes — PNG/JPEG/WebP; decoded, prefiltered and used for all reflections + ambient).
    /// </summary>
    public void SetEnvironmentHdri(byte[] imageData)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (imageData.Length == 0) return;
        fixed (byte* p = imageData)
        {
            NativeEngine.SetEnvironmentHdri(
                handle: _handle,
                dataPtr: p,
                dataLen: (nuint)imageData.Length
            );
        }
    }

    /// <summary>Revert image-based lighting to the built-in procedural studio environment.</summary>
    public void SetEnvironmentProcedural()
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        NativeEngine.SetEnvironmentProcedural(_handle);
    }

    /// <summary>
    ///     Define the reflection-probe box (EEVEE-style box-projected env reflection). World-space
    ///     centre and half-extents; reflections are parallax-corrected to the box walls.
    /// </summary>
    public void SetReflectionProbe(Vec3 center, Vec3 halfExtents)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        NativeEngine.SetReflectionProbe(
            handle: _handle,
            cx: center.X,
            cy: center.Y,
            cz: center.Z,
            ex: halfExtents.X,
            ey: halfExtents.Y,
            ez: halfExtents.Z
        );
    }

    /// <summary>Clear the reflection-probe box, reverting to the infinite-distance environment.</summary>
    public void ClearReflectionProbe()
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        NativeEngine.SetReflectionProbe(
            handle: _handle,
            cx: 0,
            cy: 0,
            cz: 0,
            ex: 0,
            ey: 0,
            ez: 0
        );
    }

    /// <summary>Snapshot per-frame engine render statistics for the debug overlay/profiler.</summary>
    public ZgEngineStats GetEngineStats()
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        NativeEngine.DebugGetEngineStats(handle: _handle, outStats: out var stats);
        return stats;
    }

    public void SceneSetMeshEffect(ulong nodeHandle, uint effect)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshEffect(handle: _handle, nodeHandle: nodeHandle, effect: effect);
    }

    /// <summary>
    ///     Set a mesh material's alpha mode (0=opaque, 1=mask, 2=blend, 3=glass) and the mask
    ///     cutoff threshold (used by mode 1; 0.5 is the glTF default).
    /// </summary>
    public void SceneSetMeshAlphaMode(ulong nodeHandle, uint mode, float cutoff = 0.5f)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshAlphaMode(
            handle: _handle,
            nodeHandle: nodeHandle,
            mode: mode,
            cutoff: cutoff
        );
    }

    /// <summary>Set whether the mesh material renders double-sided (no back-face culling).</summary>
    public void SceneSetMeshDoubleSided(ulong nodeHandle, bool doubleSided)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshDoubleSided(
            handle: _handle,
            nodeHandle: nodeHandle,
            doubleSided: doubleSided ? 1u : 0u
        );
    }

    /// <summary>
    ///     Set KHR_materials_ior / _transmission: <paramref name="ior" /> drives the dielectric F0
    ///     (((n−1)/(n+1))²) and the glass refraction bend; <paramref name="transmission" /> (0..1)
    ///     blends the lit surface toward the transmissive glass response.
    /// </summary>
    public void SceneSetMeshVolume(ulong nodeHandle, float ior, float transmission)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshVolume(
            handle: _handle,
            nodeHandle: nodeHandle,
            ior: ior,
            transmission: transmission
        );
    }

    /// <summary>
    ///     Mark the metallic-roughness map's R channel as glTF ORM baked occlusion (strength 0..1;
    ///     0 = the channel is ignored).
    /// </summary>
    public void SceneSetMeshOcclusionStrength(ulong nodeHandle, float strength)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshOcclusionStrength(
            handle: _handle,
            nodeHandle: nodeHandle,
            strength: strength
        );
    }

    public void SceneSetMeshTextureFile(ulong nodeHandle, byte* pathC)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshTextureFile(handle: _handle, nodeHandle: nodeHandle, pathC: pathC);
    }

    /// <summary>Set a mesh node's base-colour texture from a file path (convenience string helper).</summary>
    public void SceneSetMeshTexturePath(ulong nodeHandle, string path)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        byte[] bytes = Encoding.UTF8.GetBytes(path);
        Span<byte> buf = stackalloc byte[bytes.Length + 1];
        bytes.CopyTo(buf);
        buf[bytes.Length] = 0;
        fixed (byte* p =
                   buf)
            NativeEngine.SceneSetMeshTextureFile(handle: _handle, nodeHandle: nodeHandle, pathC: p);
    }

    public void SceneSetMeshMrTextureFile(ulong nodeHandle, byte* pathC)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshMrTextureFile(
            handle: _handle,
            nodeHandle: nodeHandle,
            pathC: pathC
        );
    }

    /// <summary>Set (or clear with null) the tangent-space normal map for a mesh node (linear).</summary>
    public void SceneSetMeshNormalTextureFile(ulong nodeHandle, byte* pathC)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshNormalTextureFile(
            handle: _handle,
            nodeHandle: nodeHandle,
            pathC: pathC
        );
    }

    /// <summary>Set (or clear with null) the emissive map for a mesh node (sRGB; × emissive factor).</summary>
    public void SceneSetMeshEmissiveTextureFile(ulong nodeHandle, byte* pathC)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshEmissiveTextureFile(
            handle: _handle,
            nodeHandle: nodeHandle,
            pathC: pathC
        );
    }

    public void SceneRemoveNode(ulong nodeHandle)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        NativeEngine.SceneRemoveNode(handle: _handle, nodeHandle: nodeHandle);
    }

    /// <summary>
    ///     Load many mesh textures at once. The native side reads the files, decodes them in
    ///     PARALLEL across a thread pool, then stores them — far faster than calling the
    ///     per-texture FFI in a loop when a model has many materials. The path pointers inside
    ///     each item must stay valid for the duration of this call (the caller owns/frees them).
    /// </summary>
    public void SceneLoadTexturesBatch(ZgTextureLoadItem[] items)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (items.Length == 0) return;
        fixed (ZgTextureLoadItem* ptr = items)
        {
            NativeEngine.SceneLoadTexturesBatch(
                handle: _handle,
                itemsPtr: ptr,
                count: (uint)items.Length
            );
        }
    }

    public void SceneUpdateNode(
        ulong nodeHandle,
        float x, float y, float z,
        float qx, float qy, float qz, float qw,
        float sx, float sy, float sz)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        NativeEngine.SceneUpdateNode(
            handle: _handle,
            nodeHandle: nodeHandle,
            x: x,
            y: y,
            z: z,
            qx: qx,
            qy: qy,
            qz: qz,
            qw: qw,
            sx: sx,
            sy: sy,
            sz: sz
        );
    }

    /// <summary>Tell the 3D renderer which node to highlight with a rim glow. Pass 0 to clear.</summary>
    public void SceneSetSelectedNode(ulong nodeHandle)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        NativeEngine.SceneSetSelectedNode(handle: _handle, nodeHandle: nodeHandle);
    }

    public ulong Render3D(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        return NativeEngine.Render3D(
            handle: _handle,
            width: ClampDim(width),
            height: ClampDim(height)
        );
    }

    // ── Render graph API ──────────────────────────────────────────────────────

    /// <summary>
    ///     Begin a new logical frame, informing the render graph of the 3D scene
    ///     viewport dimensions.  Pass <c>sceneW = 0, sceneH = 0</c> for a UI-only frame.
    /// </summary>
    public void BeginFrame(float deltaTime, uint sceneW = 0, uint sceneH = 0)
    {
        EnsureReady();
        NativeEngine.BeginFrame(
            handle: _handle,
            sceneW: sceneW,
            sceneH: sceneH,
            scale: Scale,
            deltaTime: deltaTime
        );
    }

    /// <summary>
    ///     Submit paint commands to be rendered by the next <see cref="RenderFrameV2" /> call.
    ///     This is the replacement for <see cref="RenderFrame" /> in the new render graph flow.
    /// </summary>
    public void SubmitPaintCommands(PaintList paint)
    {
        EnsureReady();
        _submitPaintCb ??= (ptr, count) =>
            NativeEngine.SubmitPaintCommands(handle: _handle, commands: ptr, count: count);
        paint.PinAndCall(_submitPaintCb);
    }

    /// <summary>
    ///     Submit overlay paint commands rendered on top of the main pass.
    /// </summary>
    public void SubmitOverlayCommands(PaintList overlay)
    {
        EnsureReady();
        _submitOverlayCb ??=
            (ptr, count) => NativeEngine.SubmitOverlayCommands(
                handle: _handle,
                commands: ptr,
                count: count
            );
        overlay.PinAndCall(_submitOverlayCb);
    }

    /// <summary>
    ///     Dev-tooling golden-image capture of the 2D paint path: render the currently-submitted main
    ///     UI paint list (the last <see cref="SubmitPaintCommands" />) into a fresh offscreen target and
    ///     dump it to a 24-bit BMP at <paramref name="path" />. The 2D counterpart of the
    ///     <c>ZIGOTE_SHOT</c> 3D capture — it gives the otherwise visually-untested 2D paint path a
    ///     headless regression seam (submit a known paint list, capture, diff against a golden with
    ///     <c>tools/bmpdiff.py</c>). Additive on the native side (reuses <c>renderToTexture</c> + the
    ///     BMP readback), so it never perturbs the live present path. Returns false on failure.
    /// </summary>
    public bool CaptureUiBmp(string path, uint width, uint height, float scale = 1f)
    {
        EnsureReady();
        int len = Encoding.UTF8.GetByteCount(path);
        var buf = len <= StackStringMax ? stackalloc byte[len] : new byte[len];
        Encoding.UTF8.GetBytes(chars: path, bytes: buf);
        fixed (byte* p = buf)
        {
            return NativeEngine.CaptureUiBmp(
                handle: _handle,
                pathPtr: p,
                pathLen: (nuint)len,
                width: ClampDim(width),
                height: ClampDim(height),
                scale: scale
            ) == ZgResult.Ok;
        }
    }

    /// <summary>
    ///     Declare the damaged sub-rectangles for the next <see cref="RenderFrameV2" /> call (absolute
    ///     logical-pixel screen rects). When non-empty, the UI-only render path repaints <em>only</em>
    ///     these regions into the persistent scene texture (<c>loadOp = load</c> + scissor) instead of a
    ///     full clear + redraw. An <b>empty</b> span means "no partial-repaint info" — native clears and
    ///     redraws the whole frame (the safe default). Must be called each frame after
    ///     <see cref="SubmitPaintCommands" /> and before <see cref="RenderFrameV2" />; the rects only take
    ///     effect for the pure-UI path (a re-rendered 3D scene always repaints the whole frame).
    /// </summary>
    public void SubmitFrameDamage(ReadOnlySpan<Rect> rects)
    {
        EnsureReady();
        if (rects.IsEmpty)
        {
            NativeEngine.SubmitFrameDamage(handle: _handle, rects: null, count: 0);
            return;
        }

        // Rect is a 4×f32 sequential value type (x, y, width, height); native reads 4 floats per rect.
        fixed (Rect* ptr =
                   rects)
        {
            NativeEngine.SubmitFrameDamage(
                handle: _handle,
                rects: (float*)ptr,
                count: (uint)rects.Length
            );
        }
    }

    /// <summary>
    ///     Execute the render graph and present the frame.
    ///     Must be called after <see cref="BeginFrame" /> and <see cref="SubmitPaintCommands" />.
    /// </summary>
    public void RenderFrameV2()
    {
        EnsureReady();
        var result = NativeEngine.RenderFrameV2(_handle);
        if (result != ZgResult.Ok)
            throw new InvalidOperationException("zigote_render_frame_v2 failed.");
    }

    /// <summary>End the frame and clear per-frame data.</summary>
    public void EndFrame()
    {
        EnsureReady();
        NativeEngine.EndFrame(_handle);
    }

    // ── Render texture API ────────────────────────────────────────────────────

    /// <summary>
    ///     Create a GPU render texture of the given pixel dimensions.
    ///     Returns an opaque RT handle (non-zero on success, 0 on failure).
    ///     The handle is also the image cache key for <see cref="PaintList.AddImage" />.
    ///     Dispose with <see cref="DestroyRenderTexture" /> when no longer needed.
    /// </summary>
    // wgpu panics on a zero-size texture/surface. Clamp every native-crossing pixel dimension to ≥1
    // so a collapsed / not-yet-laid-out viewport can never take the renderer down. The primary render
    // paths (ViewportPanel/GameViewport) already floor+clamp; this backstops the raw public entry points.
    private static uint ClampDim(uint d) => d < 1u ? 1u : d;

    public ulong CreateRenderTexture(uint width, uint height)
    {
        EnsureReady();
        return NativeEngine.RenderTextureCreate(
            handle: _handle,
            width: ClampDim(width),
            height: ClampDim(height)
        );
    }

    /// <summary>Destroy a render texture and release its GPU resources.</summary>
    public void DestroyRenderTexture(ulong rtHandle)
    {
        EnsureReady();
        if (rtHandle == 0) return;
        NativeEngine.RenderTextureDestroy(handle: _handle, rtHandle: rtHandle);
    }

    /// <summary>
    ///     Toggle swapchain vsync (for FPS testing). <c>true</c> = vsync on (fifo, capped to the
    ///     display refresh). <c>false</c> = uncapped (immediate/mailbox if supported).
    /// </summary>
    public void SetVsync(bool enabled)
    {
        EnsureReady();
        NativeEngine.SetVsync(handle: _handle, enabled: (byte)(enabled ? 1 : 0));
    }

    /// <summary>
    ///     Returns the image cache key for <paramref name="rtHandle" />.
    ///     Pass this to <see cref="PaintList.AddImage" /> as the <c>cacheKey</c> parameter.
    ///     (Currently the cache key equals the RT handle, but this API isolates that assumption.)
    /// </summary>
    public ulong GetRenderTextureCacheKey(ulong rtHandle)
    {
        EnsureReady();
        return NativeEngine.RenderTextureCacheKey(handle: _handle, rtHandle: rtHandle);
    }

    // ── Frame lifecycle split API ─────────────────────────────────────────────

    /// <summary>
    ///     Begin a new frame (stores parameters, resets the transient texture pool).
    ///     Equivalent to <see cref="BeginFrame" />; provided as the "split" entrypoint
    ///     paired with <see cref="FrameEnd" />.
    /// </summary>
    public void FrameBegin(float deltaTime, uint sceneW = 0, uint sceneH = 0)
    {
        EnsureReady();
        NativeEngine.FrameBegin(
            handle: _handle,
            sceneW: sceneW,
            sceneH: sceneH,
            scale: Scale,
            deltaTime: deltaTime
        );
    }

    /// <summary>
    ///     Execute the render graph, present the frame, and clear per-frame state.
    ///     Replaces the <see cref="RenderFrameV2" /> + <see cref="EndFrame" /> pair.
    /// </summary>
    public void FrameEnd()
    {
        EnsureReady();
        var result = NativeEngine.FrameEnd(_handle);
        if (result != ZgResult.Ok)
            throw new InvalidOperationException("zigote_frame_end failed.");
    }

    /// <summary>Update render graph feature flags.</summary>
    public void SetRenderSettings(RenderSettings settings)
    {
        EnsureReady();
        NativeEngine.SetRenderSettings(
            handle: _handle,
            enableGlass: settings.EnableGlassEffects ? (byte)1 : (byte)0,
            enableDebug: settings.EnableDebugOverlays ? (byte)1 : (byte)0
        );
    }

    /// <summary>Read the current 3D render settings (environment, studio lights, post, shadows).</summary>
    public ZgRenderSettings3D GetRenderSettings3D()
    {
        EnsureReady();
        NativeEngine.GetRenderSettings3D(handle: _handle, outSettings: out var s);
        return s;
    }

    /// <summary>Apply 3D render settings from the editor's Settings tab.</summary>
    public void SetRenderSettings3D(ZgRenderSettings3D settings)
    {
        EnsureReady();
        NativeEngine.SetRenderSettings3D(handle: _handle, settings: settings);
    }

    /// <summary>
    ///     Register a custom WGSL shader for use with <see cref="PaintList.AddShaderEffect" />.
    ///     The shader receives the backdrop texture as bind group 0 and 8 float params via vertex
    ///     attributes.
    /// </summary>
    public static bool RegisterShader(uint id, string wgsl)
    {
        var engine = RequireInstance();
        byte[] bytes = Encoding.UTF8.GetBytes(wgsl);
        fixed (byte* ptr = bytes)
        {
            return NativeEngine.RegisterShader(
                handle: engine._handle,
                id: id,
                wgslPtr: ptr,
                wgslLen: (nuint)bytes.Length
            ) == ZgResult.Ok;
        }
    }

    // ── Text layout handles ───────────────────────────────────────────────────

    /// <summary>
    ///     Pre-compute a text layout using HarfBuzz shaping on the Zig side.
    ///     The returned <see cref="TextLayout" /> caches glyph positions so subsequent
    ///     draw calls (via <see cref="PaintList.AddTextLayout" />) skip shaping.
    ///     Dispose the handle when the text or style changes, or when it is no longer needed.
    /// </summary>
    public TextLayout CreateTextLayout(
        string text,
        float fontSize,
        float maxWidth = 0f,
        FontWeight weight = FontWeight.Normal,
        FontStyle style = FontStyle.Normal,
        float lineHeight = 0f,
        float letterSpacing = 0f,
        float wordSpacing = 0f,
        string? fontFamily = null)
    {
        EnsureReady();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        // null/empty family → null pointer + 0 length, which the native side resolves to the default face.
        byte[]? familyBytes = string.IsNullOrEmpty(fontFamily)
            ? null
            : Encoding.UTF8.GetBytes(fontFamily);
        ulong layoutHandle;
        fixed (byte* p = bytes)
        fixed (byte* fp = familyBytes)
        {
            layoutHandle = NativeEngine.TextLayoutCreate(
                handle: _handle,
                textPtr: p,
                textLen: (nuint)bytes.Length,
                fontFamilyPtr: fp,
                fontFamilyLen: (nuint)(familyBytes?.Length ?? 0),
                fontSize: fontSize,
                fontWeight: (ushort)weight,
                fontStyle: (byte)style,
                lineHeight: lineHeight,
                letterSpacing: letterSpacing,
                wordSpacing: wordSpacing,
                maxWidth: maxWidth
            );
        }

        if (layoutHandle == 0)
            throw new InvalidOperationException("zigote_text_layout_create failed.");
        return new TextLayout(handle: layoutHandle, text: text);
    }

    // ── Emoji font support ───────────────────────────────────────────────────

    /// <summary>
    ///     Load a font face from a file path and register it under <paramref name="name" />.
    ///     Must be called before <see cref="AddEmojiFont" /> to make the font available.
    /// </summary>
    /// <returns>True on success.</returns>
    public bool LoadFont(string name, string path)
    {
        EnsureReady();
        int nameLen = Encoding.UTF8.GetByteCount(name);
        int pathLen = Encoding.UTF8.GetByteCount(path);
        var nameBuf = nameLen < StackStringMax
            ? stackalloc byte[nameLen + 1]
            : new byte[nameLen + 1];
        var pathBuf = pathLen < StackStringMax
            ? stackalloc byte[pathLen + 1]
            : new byte[pathLen + 1];
        Encoding.UTF8.GetBytes(chars: name, bytes: nameBuf);
        Encoding.UTF8.GetBytes(chars: path, bytes: pathBuf);
        nameBuf[nameLen] = 0;
        pathBuf[pathLen] = 0;
        fixed (byte* namePtr = nameBuf)
        fixed (byte* pathPtr = pathBuf)
        {
            return NativeEngine.LoadFont(handle: _handle, namePtr: namePtr, pathPtr: pathPtr) ==
                   ZgResult.Ok;
        }
    }

    /// <summary>
    ///     Register <paramref name="name" /> as the emoji font family for color glyph rendering.
    ///     The font must have been loaded via <see cref="LoadFont" /> or the initial font list.
    /// </summary>
    /// <returns>True on success.</returns>
    public bool AddEmojiFont(string name)
    {
        EnsureReady();
        int nameLen = Encoding.UTF8.GetByteCount(name);
        var nameBuf = nameLen < StackStringMax
            ? stackalloc byte[nameLen + 1]
            : new byte[nameLen + 1];
        Encoding.UTF8.GetBytes(chars: name, bytes: nameBuf);
        nameBuf[nameLen] = 0;
        fixed (byte* namePtr = nameBuf)
            return NativeEngine.AddEmojiFont(handle: _handle, namePtr: namePtr) == ZgResult.Ok;
    }

    /// <summary>
    ///     Register <paramref name="name" /> as a script-fallback family: any character the
    ///     requested face cannot draw is looked for in these, in registration order, before it is
    ///     given up on and rendered as a box. The font must have been loaded via
    ///     <see cref="LoadFont" /> or the initial font list.
    /// </summary>
    /// <remarks>
    ///     A bundled UI face covers the scripts its designer drew — Inter is Latin, Greek and
    ///     Cyrillic. Any app that displays text it did not author needs this, or Japanese, Korean,
    ///     Chinese, Arabic and Thai render as tofu.
    /// </remarks>
    /// <returns>True on success.</returns>
    public bool AddFallbackFont(string name)
    {
        EnsureReady();
        int nameLen = Encoding.UTF8.GetByteCount(name);
        var nameBuf = nameLen < StackStringMax
            ? stackalloc byte[nameLen + 1]
            : new byte[nameLen + 1];
        Encoding.UTF8.GetBytes(chars: name, bytes: nameBuf);
        nameBuf[nameLen] = 0;
        fixed (byte* namePtr = nameBuf)
            return NativeEngine.AddFallbackFont(handle: _handle, namePtr: namePtr) == ZgResult.Ok;
    }

    // ── Glyph atlas upload ────────────────────────────────────────────────────

    /// <summary>
    ///     Upload a grayscale (R8) glyph atlas from managed memory.
    ///     <paramref name="pixels" /> must be <c>width × height</c> bytes where each byte
    ///     is the alpha coverage of that texel (0 = transparent, 255 = opaque).
    ///     Returns a cache handle usable with <see cref="PaintList.AddGlyphRun" />.
    ///     Returns 0 on failure.
    /// </summary>
    public ulong UploadGlyphAtlas(ReadOnlySpan<byte> pixels, uint width, uint height)
    {
        EnsureReady();
        fixed (byte* p = pixels)
        {
            return NativeEngine.UploadGlyphAtlas(
                handle: _handle,
                pixelsPtr: p,
                pixelsLen: (nuint)pixels.Length,
                width: width,
                height: height
            );
        }
    }

    // ── Unsafe helpers ────────────────────────────────────────────────────────

    private uint PollEventsNative()
    {
        fixed (ZgEvent* ptr = _eventBuf)
        {
            return NativeEngine.PollEvents(
                handle: _handle,
                buf: ptr,
                capacity: (uint)_eventBuf.Length
            );
        }
    }

    /// Base of the out-of-band poll text buffer for the just-polled batch, as an
    /// <see cref="nint" />
    /// (so iterator callers can hold it across
    /// <c>yield</c>
    /// ). Must be read before the next poll.
    private nint PollTextBase() => (nint)NativeEngine.PollTextPtr(_handle);

    private void RefreshSize()
    {
        NativeEngine.GetSize(handle: _handle, outW: out uint w, outH: out uint h);
        PixelWidth = w;
        PixelHeight = h;
        Scale = NativeEngine.GetScale(_handle);
        DisplayRefreshHz = NativeEngine.GetRefreshHz(handle: _handle, windowId: 0);
    }

    private static void ValidateAbi()
    {
        NativeEngine.GetRendererAbiInfo(out var info);
        RendererAbiInfo.Validate(info);
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(condition: _disposed, instance: this);
        if (!_initialized)
            throw new InvalidOperationException("Call Initialize() before using ZigoteEngine.");
    }

    /// <summary>
    ///     Resolve the singleton for static FFI helpers, with the same readiness contract as the
    ///     instance methods. Throws a clear exception instead of a raw
    ///     <see cref="NullReferenceException" />
    ///     when no engine has been created or it is not yet initialized.
    /// </summary>
    private static ZigoteEngine RequireInstance()
    {
        var inst = Instance
                   ?? throw new InvalidOperationException(
                       "No ZigoteEngine instance exists. Construct and Initialize() one before calling static FFI helpers."
                   );
        inst.EnsureReady();
        return inst;
    }
}
