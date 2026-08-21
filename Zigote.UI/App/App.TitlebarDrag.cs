using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.UI.Semantics;
using Zigote.UI.Widgets;

namespace Zigote.UI.Host;

public partial class App
{
    /// <summary>
    ///     How long a hidden window's frame loop blocks between turns. Ten times a second is more
    ///     than enough for audio buffers, network pumps and a media-key round trip, and costs
    ///     essentially nothing next to a 60 Hz render loop.
    /// </summary>
    private const int HiddenFrameIntervalMs = 100;
    // ── Titlebar drag arbitration ─────────────────────────────────────────────
    // The native SDL hit-test asks the app, per pointer position, whether the point is a
    // draggable titlebar area. This is what lets real controls live in the titlebar band:
    // buttons/fields stay clickable, and the gaps between them move the window.

    // Concurrent: the SDL hit-test callback that reads/writes this runs per pointer motion on
    // whatever thread the platform chose — a plain Dictionary can be torn by a concurrent write.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool>
        InteractiveTypeCache = new();
    private static bool _dragProviderInstalled;

    /// <summary>
    ///     Per-frame damage logging to stderr (<c>ZIGOTE_DEBUG_DAMAGE=1</c>): each presented frame's
    ///     FULL/PARTIAL decision, damage rects, and per-layer dirty state. Diagnostic only.
    /// </summary>
    public static readonly bool DebugDamageLog =
        Environment.GetEnvironmentVariable("ZIGOTE_DEBUG_DAMAGE") == "1";

    /// <summary>
    ///     Per-event scroll tracing to stderr (<c>ZIGOTE_DEBUG_SCROLL=1</c>): raw vs dispatched wheel
    ///     deltas, orientation, hit target, and the CodeEditor's per-tick eased offsets. Diagnostic only.
    /// </summary>
    public static readonly bool DebugScrollLog =
        Environment.GetEnvironmentVariable("ZIGOTE_DEBUG_SCROLL") == "1";

    public bool ShouldQuit => Engine.ShouldQuit;

    public float DeltaTime { get; private set; }
    public float Time => (float)_clock.Elapsed.TotalSeconds;
    public bool ContinuousUpdate { get; set; }

    /// <summary>
    ///     Force the frame loop to render every frame (bypass the idle/event-wait gate), independent
    ///     of <see cref="ContinuousUpdate" />. The viewport FPS-test setting drives this so the
    ///     renderer runs continuously while measuring throughput. Defaults on when ZIGOTE_CONTINUOUS=1
    ///     so any UI app can be driven every frame for GPU profiling / memory sampling.
    /// </summary>
    public bool ForceContinuousRender { get; set; } =
        Environment.GetEnvironmentVariable("ZIGOTE_CONTINUOUS") == "1";

    /// <summary>
    ///     Master switch for sub-rectangle partial repaint (GPU-scissor damage regions). On by default;
    ///     exposed as the <c>render.partial_repaint</c> debug variable. When off, every frame is a full
    ///     clear + redraw (the pre-existing behaviour). Continuous/forced-render frames and
    ///     <c>ZIGOTE_SHOT</c> captures already full-clear regardless (they mark the whole frame dirty), so
    ///     this only affects idle partial-change frames such as a blinking caret or a value-drag.
    /// </summary>
    public bool PartialRepaintEnabled { get; set; } = true;

    /// <summary>
    ///     Software cap on the (continuous) render loop, in frames per second. 0 = "whatever the
    ///     monitor does" (see <see cref="FrameIntervalTicks" />). Paced at the end of each frame; to
    ///     exceed the display refresh, also turn <see cref="VSync" /> off.
    ///     <para>
    ///         <b>Negative = unpaced</b>: the pacer is skipped entirely and the loop runs as fast as the
    ///         work allows. Only useful with <see cref="VSync" /> off, and only for benchmarking — an
    ///         app that renders faster than the panel burns power for frames nobody sees.
    ///     </para>
    /// </summary>
    public int FrameRateLimit { get; set; }

    /// <summary>
    ///     Refresh rate of the monitor this app's window is currently on, in Hz. Falls back to 60 when
    ///     the platform reports nothing. Tracked across monitor moves — on a mixed 60 Hz + 144 Hz
    ///     desktop, dragging the window between panels changes this.
    /// </summary>
    public float DisplayRefreshHz => Engine.DisplayRefreshHz > 0 ? Engine.DisplayRefreshHz : 60f;

    /// <summary>
    ///     The frame budget every host loop paces against, in <see cref="Stopwatch" /> ticks — the
    ///     single place the app's target frame rate is decided.
    ///     <para>
    ///         It is the display's refresh interval, lengthened by <see cref="FrameRateLimit" /> when
    ///         one is set. Whichever is SLOWER wins: an explicit cap can only ever slow the loop down,
    ///         never push it past the panel (frames the monitor can't show cost power for nothing, and
    ///         with vsync on the present would block anyway). So a game asking for 30 gets 30 on a
    ///         144 Hz screen, and one asking for 240 still gets 144.
    ///     </para>
    /// </summary>
    public long FrameIntervalTicks => ComputeFrameIntervalTicks(
        displayHz: DisplayRefreshHz,
        frameRateLimit: FrameRateLimit
    );

    /// <summary>
    ///     Whether the OS window has keyboard focus (SDL focus gained/lost — tracked from
    ///     <see cref="WindowFocusEvent" />; starts true since SDL only reports transitions). Hosts read
    ///     it to throttle their frame loop while the app is in the background.
    /// </summary>
    public bool WindowFocused { get; private set; } = true;

    /// <summary>
    ///     Honor the host's "natural scroll" OS setting (<see cref="ScrollOrientation" />). The native
    ///     layer normalizes every wheel event to a canonical orientation; when this is on, App re-applies
    ///     the host's flipped/natural preference (queried via
    ///     <see cref="ZigoteEngine.GetScrollOrientation" />)
    ///     so a user with natural scrolling gets natural scrolling. On by default (native-app behavior).
    /// </summary>
    public bool HonorHostScrollOrientation { get; set; } = true;

    /// <summary>Swapchain vsync. Setting it reconfigures the native present mode (wgpu).</summary>
    public bool VSync
    {
        get => _vsync;
        set
        {
            if (_vsync == value) return;
            _vsync = value;
            Engine.SetVsync(value);
        }
    }

    /// <summary>Current modifier-key state. Updated on every KeyEvent so widgets can read it.</summary>
    public Modifiers CurrentModifiers { get; private set; }

    /// <summary>
    ///     True while dispatching an OS auto-repeat key-down (held key) rather than the initial press.
    ///     Widgets can read this inside <see cref="Widget.OnKey" /> to ignore repeats for one-shot
    ///     actions.
    /// </summary>
    public bool CurrentKeyRepeat { get; private set; }

    /// <summary>
    ///     App-level shortcut bindings (DevTools, profiler, focus traversal, escape). Pre-populated with
    ///     defaults; rebind/persist via the <see cref="Keymap" /> API. Action ids are the <c>Action*</c>
    ///     constants on <see cref="App" />.
    /// </summary>
    public Keymap Keymap { get; } = CreateDefaultKeymap();

    /// <summary>
    ///     App-defined keyboard shortcuts: <see cref="Keymap.Bind" /> a chord to an action id of your
    ///     own and handle it here. Fired for the initial press (never an auto-repeat) of any chord
    ///     that resolves to an id the App does not own itself, before focus traversal and Escape;
    ///     return true to consume it. A chord carrying Ctrl/Cmd/Alt is a command and fires whatever
    ///     holds focus; an unmodified one (Space, F9) is withheld while an <see cref="ITextInputClient" />
    ///     is focused, so it reaches the editor as typing.
    /// </summary>
    public Func<string, bool>? OnShortcut { get; set; }

    /// <summary>
    ///     Menu-bar accelerators: (chord, action) pairs fired for the initial press of a chord no
    ///     <see cref="Keymap" /> action and no <see cref="OnShortcut" /> handler claimed. Owned by the
    ///     menu model — <c>MenuAccelerators.Install</c> rewrites it wholesale — and only used where the
    ///     OS does not dispatch key equivalents itself (everywhere but the macOS <c>NSMenu</c> bar).
    /// </summary>
    public List<(KeyChord Chord, Action Run)> Accelerators { get; } = [];

    /// <summary>
    ///     Optional platform accessibility bridge. When assigned, the app rebuilds the
    ///     <see cref="SemanticsNode" /> tree after any layout/focus change and pushes it here so a native
    ///     screen reader can read the UI. <c>null</c> (the default) keeps the tree available for the
    ///     in-engine Semantics inspector + tests without touching the OS. See
    ///     <see cref="ISemanticsBridge" />.
    /// </summary>
    public ISemanticsBridge? SemanticsBridge { get; set; }

    /// <summary>The most recently built accessibility tree (refreshed by <see cref="BuildSemantics" />).</summary>
    public SemanticsNode? SemanticsRoot { get; private set; }

    /// <summary>The widget that currently holds keyboard focus (null when nothing is focused).</summary>
    public Widget? FocusedWidget { get; private set; }

    /// <summary>
    ///     Whether the focus ring should be painted for the focused widget — the :focus-visible
    ///     policy: keyboard-driven focus (Tab / arrow traversal, overlay auto-focus) shows the
    ///     ring; pointer-driven focus hides it (a click already shows what was targeted).
    ///     <see cref="Theme.FocusRing.AddFocusRing" /> consults this, so every control inherits
    ///     the behavior.
    /// </summary>
    public bool FocusRingVisible { get; private set; } = true;

    internal int DebugOverlayCount => _overlays.Count;

    /// <summary>
    ///     Frames in which the root widget layer was actually re-walked (partial-repaint
    ///     diagnostics/tests).
    /// </summary>
    public long RootRepaintCount => _repaint.RootPaints;

    /// <summary>
    ///     Frames in which the overlay layer was actually re-walked (partial-repaint
    ///     diagnostics/tests).
    /// </summary>
    public long OverlayRepaintCount => _repaint.OverlayPaints;

    /// <summary>
    ///     Refresh ambient context and run one Measure+Layout pass over the root and overlays.
    ///     Cheap when nothing changed — per-widget Measure caching short-circuits unchanged
    ///     subtrees. Clears <see cref="_needsLayout" /> and <see cref="_pendingRelayout" />.
    /// </summary>
    /// <summary>This window's logical width (secondary windows have their own surface).</summary>
    public float HostLogicalWidth => NativeWindow?.LogicalWidth ?? Engine.LogicalWidth;

    /// <summary>This window's logical height.</summary>
    public float HostLogicalHeight => NativeWindow?.LogicalHeight ?? Engine.LogicalHeight;

    /// <summary>This window's HiDPI scale.</summary>
    public float HostScale => NativeWindow?.Scale ?? Engine.Scale;

    private static unsafe void EnsureDragHitProvider(ZigoteEngine engine)
    {
        if (_dragProviderInstalled) return;
        _dragProviderInstalled = true;
        engine.WindowChromeSetHitProvider(
            (nint)(delegate* unmanaged[Cdecl]<uint, float, float, int>)&DragHitTrampoline
        );
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DragHitTrampoline(uint windowId, float x, float y)
    {
        var main = MainApp;
        if (main is null) return -1;
        var app = main.WindowId == windowId ? main : null;
        if (app is null)
        {
            for (int i = 0; i < main._secondaryWindows.Count; i++)
            {
                if (main._secondaryWindows[i].WindowId == windowId)
                {
                    app = main._secondaryWindows[i];
                    break;
                }
            }
        }

        return app?.DragHitTest(x: x, y: y) ?? -1;
    }

    /// <summary>
    ///     Chromed windows (MacUnified especially) may have roots that don't cover the whole
    ///     window — e.g. content padded below the traffic-light band — which would violate the
    ///     renderer's opaque-full-screen-root contract and expose the renderer's debug clear
    ///     color. Guarantee the contract by painting the window background under the root.
    /// </summary>
    private void PaintChromeBackdrop()
    {
        if (ChromeStyle == WindowChromeStyle.System) return;
        _paint.AddRect(
            bounds: new Rect(
                x: 0f,
                y: 0f,
                width: HostLogicalWidth,
                height: HostLogicalHeight
            ),
            color: Theme.Background
        );
    }

    /// <summary>1 = draggable titlebar point, 0 = interactive content, -1 = no opinion.</summary>
    internal int DragHitTest(float x, float y)
    {
        if (ChromeStyle == WindowChromeStyle.System) return -1;
        var point = new Offset(x: x, y: y);

        // An open overlay owns the pointer — never hijack its clicks into a window drag.
        for (int i = _overlays.Count - 1; i >= 0; i--)
        {
            if (_overlays[i].HitTest(point) is not null)
                return 0;
        }

        if (ChromeStyle == WindowChromeStyle.AdwaitaCsd)
        {
            if (_root is WindowChromeHost host)
            {
                return host.Bar.Bounds.Contains(px: x, py: y) && host.Bar.IsDragPoint(point)
                    ? 1
                    : 0;
            }

            // Strip suppressed: the app's registered headerbars are the titlebar — gaps drag,
            // interactive controls inside them stay clickable.
            for (int i = 0; i < CsdDragSurfaces.Count; i++)
            {
                var surface = CsdDragSurfaces[i];
                if (!surface.Bounds.Contains(px: x, py: y)) continue;
                for (var w = surface.HitTest(point); w is not null && w != surface; w = w.Parent)
                {
                    if (IsInteractive(w))
                        return 0;
                }

                return 1;
            }

            return 0;
        }

        // MacUnified: the top band drags wherever nothing interactive claims the point.
        if (y >= TitleBarDragHeight) return 0;
        var deepest = _root?.HitTest(point);
        for (var w = deepest; w is not null && w != _root; w = w.Parent)
        {
            if (IsInteractive(w))
                return 0;
        }

        return 1;
    }

    /// <summary>
    ///     Does this widget react to the pointer? Focusable, or overrides a pointer virtual.
    ///     Reflection once per concrete type (cached) — called from the native hit-test on
    ///     pointer moves, so it must be cheap.
    /// </summary>
    private static bool IsInteractive(Widget w)
    {
        if (w.Focusable) return true;
        var type = w.GetType();
        if (InteractiveTypeCache.TryGetValue(key: type, value: out bool known)) return known;
        bool interactive =
            Overrides(type: type, name: nameof(Widget.OnPointerDown)) ||
            Overrides(type: type, name: nameof(Widget.OnScroll)) ||
            Overrides(type: type, name: nameof(Widget.OnRightClick));
        InteractiveTypeCache[type] = interactive;
        return interactive;

        static bool Overrides(Type type, string name) =>
            type.GetMethod(name)?.DeclaringType != typeof(Widget);
    }

    /// <summary>
    ///     The <see cref="FrameIntervalTicks" /> arithmetic, split out so it can be exercised without
    ///     an OS window. <paramref name="displayHz" /> &lt;= 0 falls back to 60.
    /// </summary>
    internal static long ComputeFrameIntervalTicks(float displayHz, int frameRateLimit)
    {
        if (displayHz <= 0) displayHz = 60f;
        // double, not float: Stopwatch.Frequency is 1e9 on Linux, and float has ~7 digits — a float
        // divide rounds the interval by a tick, which is enough to fail an exact comparison.
        long ticks = (long)(Stopwatch.Frequency / (double)displayHz);
        // Slower of the two: an explicit cap throttles below the panel, never above it.
        if (frameRateLimit > 0)
            ticks = Math.Max(val1: ticks, val2: Stopwatch.Frequency / frameRateLimit);
        return Math.Max(val1: 1, val2: ticks);
    }

    /// <summary>
    ///     Whether an app shortcut fires or yields to whatever holds focus: a Ctrl/Cmd/Alt chord is a
    ///     command and always fires; an unmodified one loses to a focused text editor, where it is
    ///     typing. Shift alone stays "typing" — Shift+Space is still a space.
    /// </summary>
    internal static bool ShortcutOutranksFocus(Modifiers modifiers, Widget? focused)
    {
        return modifiers.HasCommand() || modifiers.HasFlag(Modifiers.Alt) ||
               focused is not ITextInputClient;
    }
}
