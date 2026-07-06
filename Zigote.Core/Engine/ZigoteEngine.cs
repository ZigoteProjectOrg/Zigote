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

    // Log thunk — static so &LogThunk produces a stable function pointer across instances.
    // The delegate field keeps the managed action alive while native code holds the thunk.
    private static Action<int, string>? _logAction;

    // Managed array — pinned only during the native call, safe to iterate afterwards.
    private readonly ZgEvent[] _eventBuf = new ZgEvent[EventBufferSize];

    // Reuses MouseMove/Scroll event objects across polls (they flood faster than the frame rate);
    // reset at the top of every PollEventsInto. Used only by the allocation-free frame-loop drain.
    private readonly EventPool _eventPool = new();

    // Active backend capabilities, cached at Initialize() (see Caps).
    private RendererCaps _caps;
    private bool _disposed;
    private ulong _handle;
    private bool _initialized;
    private PaintList.PinCallback? _submitOverlayCb;

    // Cached submit delegates — the lambdas capture only `this` (to read the stable _handle), so a
    // single delegate instance is allocated lazily and reused, instead of one closure per frame.
    private PaintList.PinCallback? _submitPaintCb;

    public ZigoteEngine()
    {
        Instance = this;
    }

    public static ZigoteEngine? Instance { get; private set; }

    /// <summary>Opaque engine handle passed to all native FFI calls.</summary>
    public ulong Handle => _handle;

    public bool ShouldQuit { get; private set; }

    /// <summary>Current surface width in physical pixels.</summary>
    public uint PixelWidth { get; private set; }

    /// <summary>Current surface height in physical pixels.</summary>
    public uint PixelHeight { get; private set; }

    /// <summary>HiDPI scale factor (e.g. 2.0 on Retina displays).</summary>
    public float Scale { get; private set; } = 1f;

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }

    /// <summary>Request the run loop to exit (e.g. from a Quit menu item).</summary>
    public void Quit()
    {
        ShouldQuit = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LogThunk(int level, byte* msg)
    {
        _logAction?.Invoke(level, Marshal.PtrToStringUTF8((nint)msg) ?? "");
    }

    /// <summary>
    ///     Raised (on the main thread) from the native resize event-watch while the user drags a window
    ///     edge — during that modal drag the OS blocks the normal frame loop, so the host uses this to
    ///     relayout + paint + present a live frame. The argument is the SDL window id that changed size.
    /// </summary>
    public event Action<uint>? OnLiveResize;

    // Static thunk so &LiveResizeThunk is a stable function pointer; routes to the live instance.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LiveResizeThunk(uint windowId, uint width, uint height)
    {
        Instance?.HandleLiveResize(windowId, width, height);
    }

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
        RenderBackend backend = RenderBackend.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] titleBytes = [.. Encoding.UTF8.GetBytes(title), 0];
        byte[]? fpBytes = fontPath is not null ? [.. Encoding.UTF8.GetBytes(fontPath), 0] : null;
        byte[]? fnBytes = fontName is not null ? [.. Encoding.UTF8.GetBytes(fontName), 0] : null;

        _logAction = (level, msg) =>
        {
            var prefix =
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
                out _handle,
                width,
                height,
                tp,
                fp,
                fn,
                (uint)backend
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
        NativeEngine.SetResizeRenderCallback(_handle, &LiveResizeThunk);
    }

    /// <summary>Does this event belong to the main window (id matches, or unknown/global)?</summary>
    public bool IsMainWindowEvent(InputEvent evt)
    {
        return evt.WindowId == 0 || evt.WindowId == MainWindowId;
    }

    /// <summary>Screen position of the main window's top-left corner (logical desktop coords).</summary>
    public (int X, int Y) MainWindowPosition()
    {
        EnsureReady();
        NativeEngine.MainWindowPosition(_handle, out var x, out var y);
        return (x, y);
    }

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
                _handle,
                width,
                height,
                tp,
                out window
            );
        }

        if (result != ZgResult.Ok || window == 0)
            throw new InvalidOperationException(
                "zigote_window_create failed. Check stderr for details."
            );
        return new NativeWindow(this, window, NativeEngine.WindowId(_handle, window));
    }

    private RendererCaps QueryRendererCaps()
    {
        NativeEngine.GetRendererCaps(_handle, out var caps);
        return RendererCaps.From(caps);
    }

    /// <summary>
    ///     Poll SDL3 events and return them as typed <see cref="InputEvent" /> instances.
    ///     Call once per frame before building the widget tree.
    /// </summary>
    public IEnumerable<InputEvent> PollEvents()
    {
        EnsureReady();
        var count = PollEventsNative();
        var textBase = PollTextBase();

        for (uint i = 0; i < count; i++)
        {
            ref readonly var raw = ref _eventBuf[i];

            // A secondary window's resize must not clobber the main window's cached size.
            if ((EventKind)raw.Kind == EventKind.Resize &&
                (raw.WindowId == 0 || raw.WindowId == MainWindowId))
                RefreshSize();
            if ((EventKind)raw.Kind == EventKind.Quit)
                ShouldQuit = true;

            var evt = EventDecoder.Decode(raw, textBase);
            if (evt is not null)
                yield return evt;
        }
    }

    /// <summary>
    ///     Allocation-free variant of <see cref="PollEvents" /> for the frame loop: drains the SDL3
    ///     queue into <paramref name="buffer" /> (cleared first) instead of returning an iterator. The
    ///     loop reuses a single buffer, so a frame with no events allocates nothing on this path —
    ///     unlike <c>PollEvents().ToList()</c>, which allocates an enumerator and a list every frame.
    ///     (Decoded <see cref="InputEvent" /> instances are still allocated per event, i.e. only at
    ///     input rate.)
    /// </summary>
    public void PollEventsInto(List<InputEvent> buffer)
    {
        buffer.Clear();
        _eventPool.Reset();
        EnsureReady();
        var count = PollEventsNative();
        var textBase = PollTextBase();

        for (uint i = 0; i < count; i++)
        {
            ref readonly var raw = ref _eventBuf[i];

            // A secondary window's resize must not clobber the main window's cached size.
            if ((EventKind)raw.Kind == EventKind.Resize &&
                (raw.WindowId == 0 || raw.WindowId == MainWindowId))
                RefreshSize();
            if ((EventKind)raw.Kind == EventKind.Quit)
                ShouldQuit = true;

            // The two flooding event kinds are rented from the per-poll pool (no allocation once warm);
            // everything else fires at human rates and takes the plain allocating decode path.
            var evt = (EventKind)raw.Kind switch {
                EventKind.MouseMove => _eventPool.RentMouseMove(raw.X, raw.Y, raw.WindowId),
                EventKind.Scroll => _eventPool.RentScroll(
                    raw.X,
                    raw.Y,
                    raw.ScrollX,
                    raw.ScrollY,
                    raw.WindowId
                ),
                _ => EventDecoder.Decode(raw, textBase),
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
        string? fontFamily = null)
    {
        if (string.IsNullOrEmpty(text)) return Size.Zero;

        // Stack-allocate the UTF-8 buffers for the common short-label case. MeasureText is called per
        // dynamic-text widget per frame (FPS counter, timers, coordinates, profiler) where the result
        // cache misses every frame — a per-call byte[] there is steady-state GC pressure.
        const int stackMax = 256;
        var textLen = Encoding.UTF8.GetByteCount(text);
        var familyLen = string.IsNullOrEmpty(fontFamily)
            ? 0
            : Encoding.UTF8.GetByteCount(fontFamily);
        var bytes = textLen <= stackMax ? stackalloc byte[textLen] : new byte[textLen];
        Encoding.UTF8.GetBytes(text, bytes);
        var familyBytes = familyLen == 0
            ? default
            : familyLen <= stackMax
                ? stackalloc byte[familyLen]
                : new byte[familyLen];
        if (familyLen != 0) Encoding.UTF8.GetBytes(fontFamily!, familyBytes);

        ZgSize result;
        fixed (byte* p = bytes)
        fixed (byte* fp = familyBytes)
        {
            result = NativeEngine.MeasureText(
                _handle,
                p,
                (uint)textLen,
                fontSize,
                float.IsPositiveInfinity(maxWidth) ? 0f : maxWidth,
                (ushort)weight,
                (byte)style,
                fp,
                (uint)familyLen
            );
        }

        return new Size(result.Width, result.Height);
    }

    /// <summary>
    ///     Block until an SDL event arrives or <paramref name="timeoutMs" /> milliseconds elapse.
    ///     After returning, call <see cref="PollEvents" /> to drain the queue.
    ///     Lets the frame loop sleep instead of spinning when the UI is idle.
    /// </summary>
    public void WaitEvents(int timeoutMs = 16)
    {
        EnsureReady();
        NativeEngine.WaitEvents((uint)Math.Max(0, timeoutMs)); // stateless — no handle
    }

    /// <summary>Read the current clipboard as a UTF-8 string. Returns empty if clipboard is empty.</summary>
    public string GetClipboard()
    {
        // Native writes up to capacity-1 bytes but returns the FULL clipboard length. A common stack
        // buffer covers the overwhelming majority of pastes; only genuinely large clipboard content
        // takes the second, heap-allocated pass — so long text is never silently truncated.
        const int stackCap = 8192;
        var stackBuf = stackalloc byte[stackCap];
        var len = NativeEngine.GetClipboard(stackBuf, stackCap); // stateless — no handle
        if (len == 0) return string.Empty;
        if (len < stackCap) return Encoding.UTF8.GetString(stackBuf, (int)len);

        var heap = new byte[len + 1];
        fixed (byte* hp = heap)
        {
            var written = NativeEngine.GetClipboard(hp, len + 1);
            return Encoding.UTF8.GetString(hp, (int)Math.Min(written, len));
        }
    }

    /// <summary>Write <paramref name="text" /> to the system clipboard as UTF-8.</summary>
    public void SetClipboard(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var buf = stackalloc byte[bytes.Length + 1];
        for (var i = 0; i < bytes.Length; i++) buf[i] = bytes[i];
        buf[bytes.Length] = 0;
        NativeEngine.SetClipboard(buf); // stateless — no handle
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
        var joined = files is { Count: > 0 } ? string.Join('\n', files) : string.Empty;
        return NativeEngine.MacDragBegin(text ?? string.Empty, joined) != 0;
    }

    /// <summary>Enable SDL3 text-input so TEXT_INPUT events are generated. Call on text-field focus.</summary>
    public void StartTextInput()
    {
        NativeEngine.StartTextInput(_handle);
    }

    /// <summary>Disable SDL3 text-input. Call when no text field is focused.</summary>
    public void StopTextInput()
    {
        NativeEngine.StopTextInput(_handle);
    }

    /// <summary>Position the platform IME candidate window next to the active caret.</summary>
    public void SetTextInputArea(Rect area, int cursor = 0)
    {
        NativeEngine.SetTextInputArea(
            _handle,
            (int)MathF.Round(area.X),
            (int)MathF.Round(area.Y),
            Math.Max(1, (int)MathF.Round(area.Width)),
            Math.Max(1, (int)MathF.Round(area.Height)),
            cursor
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
    ///     Load a texture natively and return its cache handle.
    /// </summary>
    public static ulong LoadTexture(string path, out uint outW, out uint outH)
    {
        var engine = RequireInstance();
        byte[] pathBytes = [.. Encoding.UTF8.GetBytes(path), 0];
        fixed (byte* p = pathBytes)
        {
            return NativeEngine.LoadTexture(
                engine._handle,
                p,
                out outW,
                out outH
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
                engine._handle,
                p,
                out outW,
                out outH
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
                engine._handle,
                ptr,
                (nuint)data.Length,
                out outW,
                out outH
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
                engine._handle,
                ptr,
                (nuint)data.Length,
                maxDim,
                out outW,
                out outH
            );
        }
    }

    // ── 3D Scene FFI ──────────────────────────────────────────────────────────────

    public void SceneClear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeEngine.SceneClear(_handle);
    }

    public ulong SceneAddChildNode(ulong parentHandle, string name, byte kind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Null-terminated UTF-8 on the stack (heap fallback for long names). Called per node during
        // World.Spawn / additive scene load, so the old `GetBytes(name + "\0")` was a transient string
        // + a byte[] per node.
        var len = Encoding.UTF8.GetByteCount(name);
        var buf = len < StackStringMax ? stackalloc byte[len + 1] : new byte[len + 1];
        Encoding.UTF8.GetBytes(name, buf);
        buf[len] = 0;
        fixed (byte* namePtr = buf)
        {
            return NativeEngine.SceneAddChildNode(
                _handle,
                parentHandle,
                namePtr,
                kind
            );
        }
    }

    /// <summary>
    ///     Upload a `.zmesh` geometry blob (engine vertex layout, produced by the Assimp importer
    ///     or read back from the mesh cache) to a node's mesh renderer.
    /// </summary>
    public void SceneSetMeshBlob(ulong nodeHandle, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        fixed (byte* ptr = data)
        {
            NativeEngine.SceneSetMeshBlob(
                _handle,
                nodeHandle,
                ptr,
                (nuint)data.Length
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        var pathLen = Encoding.UTF8.GetByteCount(path);
        var cacheLen = Encoding.UTF8.GetByteCount(cacheDir);
        var pathBuf = pathLen < StackStringMax
            ? stackalloc byte[pathLen + 1]
            : new byte[pathLen + 1];
        var cacheBuf =
            cacheLen < StackStringMax ? stackalloc byte[cacheLen + 1] : new byte[cacheLen + 1];
        Encoding.UTF8.GetBytes(path, pathBuf);
        Encoding.UTF8.GetBytes(cacheDir, cacheBuf);
        pathBuf[pathLen] = 0;
        cacheBuf[cacheLen] = 0;
        fixed (byte* pathPtr = pathBuf)
        fixed (byte* cachePtr = cacheBuf)
        {
            var result = NativeEngine.ModelImport(pathPtr, cachePtr);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshPrimitive(_handle, nodeHandle, primType);
    }

    /// <summary>
    ///     Toggle a mesh node's visibility in the renderer. Unlike scaling to zero (which still issues
    ///     the draw), an invisible node is skipped entirely — a real draw-call cull. Drives the C# LOD /
    ///     distance-culling system.
    /// </summary>
    public void SceneSetNodeVisible(ulong nodeHandle, bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetNodeVisible(_handle, nodeHandle, visible ? 1u : 0u);
    }

    /// <summary>Enable/disable native world-3D frustum culling (wgpu reference renderer; on by default).</summary>
    public void RenderSetFrustumCull(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeEngine.RenderSetFrustumCull(_handle, enabled ? 1u : 0u);
    }

    // ── Game controller (SDL gamepad) ─────────────────────────────────────────

    /// <summary>True when a game controller is connected.</summary>
    public bool GamepadConnected()
    {
        return !_disposed && NativeEngine.InputGamepadConnected(_handle) != 0;
    }

    /// <summary>
    ///     Read a controller axis, normalised to [-1, 1] (triggers [0, 1]). See <c>GamepadAxis</c>
    ///     order.
    /// </summary>
    public float GamepadAxis(int axis)
    {
        return _disposed ? 0f : NativeEngine.InputGamepadAxis(_handle, (byte)axis);
    }

    /// <summary>True while a controller button is held. See <c>GamepadButton</c> order.</summary>
    public bool GamepadButton(int button)
    {
        return !_disposed && NativeEngine.InputGamepadButton(_handle, (byte)button) != 0;
    }

    // ── Audio (miniaudio software synth, lazily opened on first use) ──────────────
    // Waveform codes: 0 sine, 1 square, 2 triangle, 3 sawtooth, 4 noise.

    /// <summary>
    ///     Play a one-shot tone (UI click / blip / beep): frequency in Hz, duration in seconds,
    ///     volume [0,1].
    /// </summary>
    public void AudioBeep(float frequencyHz, float durationSeconds, float volume, int waveform)
    {
        if (!_disposed)
            NativeEngine.AudioBeep(
                _handle,
                frequencyHz,
                durationSeconds,
                volume,
                (byte)waveform
            );
    }

    /// <summary>
    ///     Set a sustained tone on a channel (held until changed). volume&lt;=0 or freq&lt;=0
    ///     silences it.
    /// </summary>
    public void AudioVoice(int channel, float frequencyHz, float volume, int waveform)
    {
        if (!_disposed)
            NativeEngine.AudioVoice(
                _handle,
                (uint)Math.Max(0, channel),
                frequencyHz,
                volume,
                (byte)waveform
            );
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
        if (!_disposed) NativeEngine.AudioUpdate(_handle, dt);
    }

    /// <summary>Set the spatial listener pose; every spatialised sound is panned + attenuated against it.</summary>
    public void AudioSetListener(Vec3 position, Vec3 forward, Vec3 up)
    {
        if (_disposed) return;
        NativeEngine.AudioSetListener(
            _handle,
            position.X,
            position.Y,
            position.Z,
            forward.X,
            forward.Y,
            forward.Z,
            up.X,
            up.Y,
            up.Z
        );
    }

    /// <summary>Master output volume [0,4]; 1 = unity.</summary>
    public void AudioSetMasterVolume(float volume)
    {
        if (!_disposed) NativeEngine.AudioSetMasterVolume(_handle, volume);
    }

    /// <summary>Positioned procedural one-shot (spatialised + attenuated). Waveform code 0..4.</summary>
    public void AudioBeep3D(Vec3 position, float frequencyHz, float durationSeconds, float volume,
        int waveform, float minDistance, float maxDistance, float rolloff)
    {
        if (_disposed) return;
        NativeEngine.AudioBeep3D(
            _handle,
            position.X,
            position.Y,
            position.Z,
            frequencyHz,
            durationSeconds,
            volume,
            (byte)waveform,
            minDistance,
            maxDistance,
            rolloff
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
            : NativeEngine.AudioSoundCreateTone(_handle, frequencyHz, (byte)waveform);
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
            return NativeEngine.AudioSoundCreateFile(_handle, p, streaming ? 1u : 0u);
        }
    }

    public void AudioSoundPlay(uint id)
    {
        if (!_disposed) NativeEngine.AudioSoundPlay(_handle, id);
    }

    public void AudioSoundStop(uint id)
    {
        if (!_disposed) NativeEngine.AudioSoundStop(_handle, id);
    }

    public void AudioSoundDestroy(uint id)
    {
        if (!_disposed) NativeEngine.AudioSoundDestroy(_handle, id);
    }

    public void AudioSoundSetVolume(uint id, float volume)
    {
        if (!_disposed) NativeEngine.AudioSoundSetVolume(_handle, id, volume);
    }

    public void AudioSoundSetPitch(uint id, float pitch)
    {
        if (!_disposed) NativeEngine.AudioSoundSetPitch(_handle, id, pitch);
    }

    public void AudioSoundSetLooping(uint id, bool looping)
    {
        if (!_disposed) NativeEngine.AudioSoundSetLooping(_handle, id, looping ? 1u : 0u);
    }

    public void AudioSoundSetSpatial(uint id, bool enabled)
    {
        if (!_disposed) NativeEngine.AudioSoundSetSpatial(_handle, id, enabled ? 1u : 0u);
    }

    public void AudioSoundSetPosition(uint id, Vec3 position)
    {
        if (!_disposed)
            NativeEngine.AudioSoundSetPosition(
                _handle,
                id,
                position.X,
                position.Y,
                position.Z
            );
    }

    public void AudioSoundSetVelocity(uint id, Vec3 velocity)
    {
        if (!_disposed)
            NativeEngine.AudioSoundSetVelocity(
                _handle,
                id,
                velocity.X,
                velocity.Y,
                velocity.Z
            );
    }

    public void AudioSoundSetAttenuation(uint id, float minDistance, float maxDistance,
        float rolloff)
    {
        if (!_disposed)
            NativeEngine.AudioSoundSetAttenuation(
                _handle,
                id,
                minDistance,
                maxDistance,
                rolloff
            );
    }

    public bool AudioSoundIsPlaying(uint id)
    {
        return !_disposed && NativeEngine.AudioSoundIsPlaying(_handle, id) != 0;
    }

    /// <summary>
    ///     Create a mixer bus (miniaudio sound group). Returns a bus id (0 = failure). Buses live
    ///     until the engine's audio state is torn down — there is deliberately no per-bus destroy.
    /// </summary>
    public uint AudioGroupCreate()
    {
        return _disposed ? 0u : NativeEngine.AudioGroupCreate(_handle);
    }

    public void AudioGroupSetVolume(uint groupId, float volume)
    {
        if (!_disposed) NativeEngine.AudioGroupSetVolume(_handle, groupId, volume);
    }

    public void AudioGroupSetPitch(uint groupId, float pitch)
    {
        if (!_disposed) NativeEngine.AudioGroupSetPitch(_handle, groupId, pitch);
    }

    /// <summary>Route a sound through a bus (bus 0 = back to the master output).</summary>
    public void AudioSoundSetGroup(uint id, uint groupId)
    {
        if (!_disposed) NativeEngine.AudioSoundSetGroup(_handle, id, groupId);
    }

    public void SceneSetLightProperties(ulong nodeHandle, byte kind, float r, float g, float b,
        float intensity,
        float range, float innerAngle, float outerAngle, bool castShadows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetLightProperties(
            _handle,
            nodeHandle,
            kind,
            r,
            g,
            b,
            intensity,
            range,
            innerAngle,
            outerAngle,
            castShadows ? 1u : 0u
        );
    }

    public void SceneSetMeshColor(ulong nodeHandle, float r, float g, float b)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshColor(
            _handle,
            nodeHandle,
            r,
            g,
            b
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        if (count == 0)
        {
            NativeEngine.SceneSetMeshInstances(
                _handle,
                nodeHandle,
                null,
                0
            );
            return;
        }

        fixed (float* ptr = matrices)
        {
            NativeEngine.SceneSetMeshInstances(
                _handle,
                nodeHandle,
                ptr,
                count
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        if (count == 0)
        {
            NativeEngine.ParticlesClear(_handle, nodeHandle);
            return;
        }

        fixed (float* ptr = data)
        {
            NativeEngine.ParticlesUpload(
                _handle,
                nodeHandle,
                ptr,
                count,
                blend
            );
        }
    }

    /// <summary>Clear one emitter node's uploaded particles (its node was removed / it stopped).</summary>
    public void ParticlesClear(ulong nodeHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle != 0) NativeEngine.ParticlesClear(_handle, nodeHandle);
    }

    /// <summary>Drop all uploaded particle batches (play stop).</summary>
    public void ParticlesClearAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        fixed (float* ptr = paramsData)
        {
            NativeEngine.ParticlesComputeEmit(
                _handle,
                nodeHandle,
                ptr,
                (uint)paramsData.Length,
                capacity,
                blend
            );
        }
    }

    /// <summary>Drop a GPU-compute emitter's buffers (its node was removed / it stopped).</summary>
    public void ParticlesComputeClear(ulong nodeHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle != 0) NativeEngine.ParticlesComputeClear(_handle, nodeHandle);
    }

    // ── 2D sprite renderer FFI ───────────────────────────────────────────────────
    // Immediate-mode frame model (see wgpu_sprites.zig): SpritesBegin once per frame with the
    // scene + overlay cameras, then SpritesDraw per pre-sorted batch of 14-float instances
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (rgba.Length < width * height * 4) return 0;
        fixed (byte* ptr = rgba)
        {
            return NativeEngine.SpritesTextureCreate(
                _handle,
                ptr,
                width,
                height,
                filter,
                srgb,
                wrap
            );
        }
    }

    /// <summary>Create a sprite texture by decoding an image file (PNG/JPG/WebP/GIF).</summary>
    public uint SpritesTextureCreateFile(string path, uint filter, uint srgb, uint wrap,
        out uint outW, out uint outH)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] pathBytes = [.. Encoding.UTF8.GetBytes(path), 0];
        fixed (byte* p = pathBytes)
        {
            return NativeEngine.SpritesTextureCreateFile(
                _handle,
                p,
                filter,
                srgb,
                wrap,
                out outW,
                out outH
            );
        }
    }

    public void SpritesTextureDestroy(uint texture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (texture != 0) NativeEngine.SpritesTextureDestroy(_handle, texture);
    }

    /// <summary>
    ///     Compile a custom sprite shader (WGSL; contract in sprite_shader_source.wgsl — vs_main/fs_main
    ///     over the sprite instance layout, premultiplied-alpha output). Returns 0 if rejected.
    /// </summary>
    public uint SpritesShaderCreate(string wgsl)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = Encoding.UTF8.GetBytes(wgsl);
        fixed (byte* p = bytes)
        {
            return NativeEngine.SpritesShaderCreate(_handle, p, (uint)bytes.Length);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sceneViewProj.Length < 16 || overlayViewProj.Length < 16) return;
        fixed (float* s = sceneViewProj)
        fixed (float* o = overlayViewProj)
        {
            NativeEngine.SpritesBegin(
                _handle,
                s,
                o,
                viewportW,
                viewportH
            );
        }
    }

    /// <summary>
    ///     Append one pre-sorted sprite batch (count × 14 floats). texture2 feeds custom shaders'
    ///     secondary slot (0 = white); blend: 0 alpha / 1 additive / 2 opaque; stage: 0 scene / 1 overlay.
    /// </summary>
    public void SpritesDraw(uint texture, uint texture2, uint shader, uint blend, uint stage,
        ReadOnlySpan<float> materialParams, ReadOnlySpan<float> instances, uint count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count == 0 || texture == 0 || instances.Length < count * 14) return;
        fixed (float* pp = materialParams)
        fixed (float* ip = instances)
        {
            NativeEngine.SpritesDraw(
                _handle,
                texture,
                texture2,
                shader,
                blend,
                stage,
                pp,
                (uint)materialParams.Length,
                ip,
                count
            );
        }
    }

    public void SceneSetMeshRoughness(ulong nodeHandle, float metallic, float roughness)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshRoughness(
            _handle,
            nodeHandle,
            metallic,
            roughness
        );
    }

    /// <summary>
    ///     Set a camera node's projection: perspective vertical FOV (degrees) + near/far clip planes.
    ///     Applied by the renderer next frame. Callers driving this dynamically (physical camera) must
    ///     keep the published culling frustum in sync — see <c>PlaySession.PublishRenderView</c>.
    /// </summary>
    public void SceneSetCameraParams(ulong nodeHandle, float fovyDegrees, float near, float far)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetCameraParams(
            _handle,
            nodeHandle,
            fovyDegrees,
            near,
            far
        );
    }

    /// <summary>
    ///     Set extended-PBR surface params: clearcoat factor/roughness and specular scale
    ///     (KHR_materials_clearcoat / _specular / _ior).
    /// </summary>
    public void SceneSetMeshSurface(ulong nodeHandle, float clearcoat, float clearcoatRoughness,
        float specular)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshSurface(
            _handle,
            nodeHandle,
            clearcoat,
            clearcoatRoughness,
            specular
        );
    }

    /// <summary>Set the mesh emissive colour (pre-scaled by KHR_materials_emissive_strength).</summary>
    public void SceneSetMeshEmissive(ulong nodeHandle, float r, float g, float b)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshEmissive(
            _handle,
            nodeHandle,
            r,
            g,
            b
        );
    }

    /// <summary>
    ///     Switch image-based lighting to an HDRI / equirectangular panorama (encoded image
    ///     bytes — PNG/JPEG/WebP; decoded, prefiltered and used for all reflections + ambient).
    /// </summary>
    public void SetEnvironmentHdri(byte[] imageData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (imageData.Length == 0) return;
        fixed (byte* p = imageData)
        {
            NativeEngine.SetEnvironmentHdri(_handle, p, (nuint)imageData.Length);
        }
    }

    /// <summary>Revert image-based lighting to the built-in procedural studio environment.</summary>
    public void SetEnvironmentProcedural()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeEngine.SetEnvironmentProcedural(_handle);
    }

    /// <summary>
    ///     Define the reflection-probe box (EEVEE-style box-projected env reflection). World-space
    ///     centre and half-extents; reflections are parallax-corrected to the box walls.
    /// </summary>
    public void SetReflectionProbe(Vec3 center, Vec3 halfExtents)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeEngine.SetReflectionProbe(
            _handle,
            center.X,
            center.Y,
            center.Z,
            halfExtents.X,
            halfExtents.Y,
            halfExtents.Z
        );
    }

    /// <summary>Clear the reflection-probe box, reverting to the infinite-distance environment.</summary>
    public void ClearReflectionProbe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeEngine.SetReflectionProbe(
            _handle,
            0,
            0,
            0,
            0,
            0,
            0
        );
    }

    /// <summary>Snapshot per-frame engine render statistics for the debug overlay/profiler.</summary>
    public ZgEngineStats GetEngineStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeEngine.DebugGetEngineStats(_handle, out var stats);
        return stats;
    }

    public void SceneSetMeshEffect(ulong nodeHandle, uint effect)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshEffect(_handle, nodeHandle, effect);
    }

    /// <summary>
    ///     Set a mesh material's alpha mode (0=opaque, 1=mask, 2=blend, 3=glass) and the mask
    ///     cutoff threshold (used by mode 1; 0.5 is the glTF default).
    /// </summary>
    public void SceneSetMeshAlphaMode(ulong nodeHandle, uint mode, float cutoff = 0.5f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshAlphaMode(
            _handle,
            nodeHandle,
            mode,
            cutoff
        );
    }

    /// <summary>Set whether the mesh material renders double-sided (no back-face culling).</summary>
    public void SceneSetMeshDoubleSided(ulong nodeHandle, bool doubleSided)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshDoubleSided(_handle, nodeHandle, doubleSided ? 1u : 0u);
    }

    /// <summary>
    ///     Set KHR_materials_ior / _transmission: <paramref name="ior" /> drives the dielectric F0
    ///     (((n−1)/(n+1))²) and the glass refraction bend; <paramref name="transmission" /> (0..1)
    ///     blends the lit surface toward the transmissive glass response.
    /// </summary>
    public void SceneSetMeshVolume(ulong nodeHandle, float ior, float transmission)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshVolume(
            _handle,
            nodeHandle,
            ior,
            transmission
        );
    }

    /// <summary>
    ///     Mark the metallic-roughness map's R channel as glTF ORM baked occlusion (strength 0..1;
    ///     0 = the channel is ignored).
    /// </summary>
    public void SceneSetMeshOcclusionStrength(ulong nodeHandle, float strength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshOcclusionStrength(_handle, nodeHandle, strength);
    }

    public void SceneSetMeshTextureFile(ulong nodeHandle, byte* pathC)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshTextureFile(_handle, nodeHandle, pathC);
    }

    /// <summary>Set a mesh node's base-colour texture from a file path (convenience string helper).</summary>
    public void SceneSetMeshTexturePath(ulong nodeHandle, string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        var bytes = Encoding.UTF8.GetBytes(path);
        Span<byte> buf = stackalloc byte[bytes.Length + 1];
        bytes.CopyTo(buf);
        buf[bytes.Length] = 0;
        fixed (byte* p = buf)
        {
            NativeEngine.SceneSetMeshTextureFile(_handle, nodeHandle, p);
        }
    }

    public void SceneSetMeshMrTextureFile(ulong nodeHandle, byte* pathC)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshMrTextureFile(_handle, nodeHandle, pathC);
    }

    /// <summary>Set (or clear with null) the tangent-space normal map for a mesh node (linear).</summary>
    public void SceneSetMeshNormalTextureFile(ulong nodeHandle, byte* pathC)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshNormalTextureFile(_handle, nodeHandle, pathC);
    }

    /// <summary>Set (or clear with null) the emissive map for a mesh node (sRGB; × emissive factor).</summary>
    public void SceneSetMeshEmissiveTextureFile(ulong nodeHandle, byte* pathC)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nodeHandle == 0) return;
        NativeEngine.SceneSetMeshEmissiveTextureFile(_handle, nodeHandle, pathC);
    }

    public void SceneRemoveNode(ulong nodeHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeEngine.SceneRemoveNode(_handle, nodeHandle);
    }

    /// <summary>
    ///     Load many mesh textures at once. The native side reads the files, decodes them in
    ///     PARALLEL across a thread pool, then stores them — far faster than calling the
    ///     per-texture FFI in a loop when a model has many materials. The path pointers inside
    ///     each item must stay valid for the duration of this call (the caller owns/frees them).
    /// </summary>
    public void SceneLoadTexturesBatch(ZgTextureLoadItem[] items)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (items.Length == 0) return;
        fixed (ZgTextureLoadItem* ptr = items)
        {
            NativeEngine.SceneLoadTexturesBatch(_handle, ptr, (uint)items.Length);
        }
    }

    public void SceneUpdateNode(
        ulong nodeHandle,
        float x, float y, float z,
        float qx, float qy, float qz, float qw,
        float sx, float sy, float sz)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeEngine.SceneUpdateNode(
            _handle,
            nodeHandle,
            x,
            y,
            z,
            qx,
            qy,
            qz,
            qw,
            sx,
            sy,
            sz
        );
    }

    /// <summary>Tell the 3D renderer which node to highlight with a rim glow. Pass 0 to clear.</summary>
    public void SceneSetSelectedNode(ulong nodeHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeEngine.SceneSetSelectedNode(_handle, nodeHandle);
    }

    public ulong Render3D(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeEngine.Render3D(_handle, ClampDim(width), ClampDim(height));
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
            _handle,
            sceneW,
            sceneH,
            Scale,
            deltaTime
        );
    }

    /// <summary>
    ///     Submit paint commands to be rendered by the next <see cref="RenderFrameV2" /> call.
    ///     This is the replacement for <see cref="RenderFrame" /> in the new render graph flow.
    /// </summary>
    public void SubmitPaintCommands(PaintList paint)
    {
        EnsureReady();
        _submitPaintCb ??= (ptr, count) => NativeEngine.SubmitPaintCommands(_handle, ptr, count);
        paint.PinAndCall(_submitPaintCb);
    }

    /// <summary>
    ///     Submit overlay paint commands rendered on top of the main pass.
    /// </summary>
    public void SubmitOverlayCommands(PaintList overlay)
    {
        EnsureReady();
        _submitOverlayCb ??=
            (ptr, count) => NativeEngine.SubmitOverlayCommands(_handle, ptr, count);
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
        var len = Encoding.UTF8.GetByteCount(path);
        var buf = len <= StackStringMax ? stackalloc byte[len] : new byte[len];
        Encoding.UTF8.GetBytes(path, buf);
        fixed (byte* p = buf)
        {
            return NativeEngine.CaptureUiBmp(
                _handle,
                p,
                (nuint)len,
                ClampDim(width),
                ClampDim(height),
                scale
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
            NativeEngine.SubmitFrameDamage(_handle, null, 0);
            return;
        }

        // Rect is a 4×f32 sequential value type (x, y, width, height); native reads 4 floats per rect.
        fixed (Rect* ptr = rects)
        {
            NativeEngine.SubmitFrameDamage(_handle, (float*)ptr, (uint)rects.Length);
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
    private static uint ClampDim(uint d)
    {
        return d < 1u ? 1u : d;
    }

    public ulong CreateRenderTexture(uint width, uint height)
    {
        EnsureReady();
        return NativeEngine.RenderTextureCreate(_handle, ClampDim(width), ClampDim(height));
    }

    /// <summary>Destroy a render texture and release its GPU resources.</summary>
    public void DestroyRenderTexture(ulong rtHandle)
    {
        EnsureReady();
        if (rtHandle == 0) return;
        NativeEngine.RenderTextureDestroy(_handle, rtHandle);
    }

    /// <summary>
    ///     Toggle swapchain vsync (for FPS testing). <c>true</c> = vsync on (fifo, capped to the
    ///     display refresh). <c>false</c> = uncapped (immediate/mailbox if supported).
    /// </summary>
    public void SetVsync(bool enabled)
    {
        EnsureReady();
        NativeEngine.SetVsync(_handle, (byte)(enabled ? 1 : 0));
    }

    /// <summary>
    ///     Returns the image cache key for <paramref name="rtHandle" />.
    ///     Pass this to <see cref="PaintList.AddImage" /> as the <c>cacheKey</c> parameter.
    ///     (Currently the cache key equals the RT handle, but this API isolates that assumption.)
    /// </summary>
    public ulong GetRenderTextureCacheKey(ulong rtHandle)
    {
        EnsureReady();
        return NativeEngine.RenderTextureCacheKey(_handle, rtHandle);
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
            _handle,
            sceneW,
            sceneH,
            Scale,
            deltaTime
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
            _handle,
            settings.EnableGlassEffects ? (byte)1 : (byte)0,
            settings.EnableDebugOverlays ? (byte)1 : (byte)0
        );
    }

    /// <summary>Read the current 3D render settings (environment, studio lights, post, shadows).</summary>
    public ZgRenderSettings3D GetRenderSettings3D()
    {
        EnsureReady();
        NativeEngine.GetRenderSettings3D(_handle, out var s);
        return s;
    }

    /// <summary>Apply 3D render settings from the editor's Settings tab.</summary>
    public void SetRenderSettings3D(ZgRenderSettings3D settings)
    {
        EnsureReady();
        NativeEngine.SetRenderSettings3D(_handle, settings);
    }

    /// <summary>
    ///     Register a custom WGSL shader for use with <see cref="PaintList.AddShaderEffect" />.
    ///     The shader receives the backdrop texture as bind group 0 and 8 float params via vertex
    ///     attributes.
    /// </summary>
    public static bool RegisterShader(uint id, string wgsl)
    {
        var engine = RequireInstance();
        var bytes = Encoding.UTF8.GetBytes(wgsl);
        fixed (byte* ptr = bytes)
        {
            return NativeEngine.RegisterShader(
                engine._handle,
                id,
                ptr,
                (nuint)bytes.Length
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
        var bytes = Encoding.UTF8.GetBytes(text);
        // null/empty family → null pointer + 0 length, which the native side resolves to the default face.
        var familyBytes = string.IsNullOrEmpty(fontFamily)
            ? null
            : Encoding.UTF8.GetBytes(fontFamily);
        ulong layoutHandle;
        fixed (byte* p = bytes)
        fixed (byte* fp = familyBytes)
        {
            layoutHandle = NativeEngine.TextLayoutCreate(
                _handle,
                p,
                (nuint)bytes.Length,
                fp,
                (nuint)(familyBytes?.Length ?? 0),
                fontSize,
                (ushort)weight,
                (byte)style,
                lineHeight,
                letterSpacing,
                wordSpacing,
                maxWidth
            );
        }

        if (layoutHandle == 0)
            throw new InvalidOperationException("zigote_text_layout_create failed.");
        return new TextLayout(layoutHandle, text);
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
        var nameLen = Encoding.UTF8.GetByteCount(name);
        var pathLen = Encoding.UTF8.GetByteCount(path);
        var nameBuf = nameLen < StackStringMax
            ? stackalloc byte[nameLen + 1]
            : new byte[nameLen + 1];
        var pathBuf = pathLen < StackStringMax
            ? stackalloc byte[pathLen + 1]
            : new byte[pathLen + 1];
        Encoding.UTF8.GetBytes(name, nameBuf);
        Encoding.UTF8.GetBytes(path, pathBuf);
        nameBuf[nameLen] = 0;
        pathBuf[pathLen] = 0;
        fixed (byte* namePtr = nameBuf)
        fixed (byte* pathPtr = pathBuf)
        {
            return NativeEngine.LoadFont(_handle, namePtr, pathPtr) == ZgResult.Ok;
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
        var nameLen = Encoding.UTF8.GetByteCount(name);
        var nameBuf = nameLen < StackStringMax
            ? stackalloc byte[nameLen + 1]
            : new byte[nameLen + 1];
        Encoding.UTF8.GetBytes(name, nameBuf);
        nameBuf[nameLen] = 0;
        fixed (byte* namePtr = nameBuf)
        {
            return NativeEngine.AddEmojiFont(_handle, namePtr) == ZgResult.Ok;
        }
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
                _handle,
                p,
                (nuint)pixels.Length,
                width,
                height
            );
        }
    }

    // ── Unsafe helpers ────────────────────────────────────────────────────────

    private uint PollEventsNative()
    {
        fixed (ZgEvent* ptr = _eventBuf)
        {
            return NativeEngine.PollEvents(_handle, ptr, (uint)_eventBuf.Length);
        }
    }

    /// Base of the out-of-band poll text buffer for the just-polled batch, as an
    /// <see cref="nint" />
    /// (so iterator callers can hold it across
    /// <c>yield</c>
    /// ). Must be read before the next poll.
    private nint PollTextBase()
    {
        return (nint)NativeEngine.PollTextPtr(_handle);
    }

    private void RefreshSize()
    {
        NativeEngine.GetSize(_handle, out var w, out var h);
        PixelWidth = w;
        PixelHeight = h;
        Scale = NativeEngine.GetScale(_handle);
    }

    private static void ValidateAbi()
    {
        NativeEngine.GetRendererAbiInfo(out var info);
        RendererAbiInfo.Validate(info);
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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