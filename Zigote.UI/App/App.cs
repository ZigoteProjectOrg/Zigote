using System.Diagnostics;
using System.Text;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.Core.Rendering;
using Zigote.UI.Debug;
using Zigote.UI.Licensing;
using Zigote.UI.Semantics;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Focus;
using MediaQueryData = Zigote.UI.Widgets.MediaQueryData;

namespace Zigote.UI.Host;

public partial class App : IDisposable
{
    // App-level shortcut action ids (see Keymap).
    public const string ActionToggleDevTools = "app.devtools.toggle";
    public const string ActionProfilerCapture = "app.profiler.capture";
    public const string ActionFocusNext = "app.focus.next";
    public const string ActionFocusPrev = "app.focus.prev";

    public const string ActionDismiss = "app.overlay.dismiss";

    // Safety margin (logical px) added around a precise damage region before it is sent to native:
    // covers focus rings, anti-aliased edges, and small (Z1) drop shadows so a partial repaint never
    // clips a widget's own decoration. Larger overflow is handled per-widget via Widget.DamageBounds.
    private const float DamageMargin = 24f;

    // Last cursor pushed to the OS. Static because SDL's active cursor is process-global (one for all
    // windows), so a single mirror de-dupes correctly no matter which window's App resolved it.
    private static MouseCursor _appliedCursor = MouseCursor.Default;

    // Reused ThemeProvider pushed around BOTH the root and overlay measure/layout so every widget —
    // including Material controls in raw-App hosts like the editor, which have no ThemeProvider of
    // their own — resolves ThemeProvider.Of to the live App.Theme instead of the Dark fallback. A
    // tree that injects its own ThemeProvider (ZigoteApp) still wins: nearest ancestor first.
    // Updated silently each layout — no per-frame allocation on the hot path.
    private readonly ThemeProvider _appThemeScope = new(ThemeData.Dark);

    // Delta-time tracking
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // DevTools seam (Shift+D). App no longer owns a concrete debug overlay — a devtools package
    // (Zigote.UI.DevTools) installs itself through OnToggleDevTools / FrameTick / continuous-frame
    // sources, so Zigote.UI stays free of any charts/panels dependency. Predicates registered here
    // keep the frame loop pumping while live metrics are on-screen (see WantsContinuousFrame()).
    private readonly List<Func<bool>> _continuousFrameSources = [];

    // Reused each frame so draining input events allocates nothing on an idle frame — unlike
    // PollEvents().ToList(), which allocated an enumerator + a list every frame.
    private readonly List<InputEvent> _events = [];
    private readonly List<(Widget Overlay, Widget? PrevFocus)> _focusRestore = [];

    private readonly PaintList _overlayPaint = new();

    // Last-submitted paint lists, diffed against re-walked lists on partial frames so unmarked
    // visual changes widen the damage instead of tearing (see PaintAndPresent).
    private readonly PaintSnapshot _rootSnapshot = new();
    private readonly PaintSnapshot _overlaySnapshot = new();

    // Overlay stack — dialogs, tooltips, snackbars painted on top of Root
    private readonly List<Widget> _overlays = [];
    private readonly PaintList _paint = new();

    // Overlays pushed this frame that still need their first focusable focused (after layout gives bounds),
    // and the focus to restore when each is popped (modal focus save/restore).
    private readonly List<Widget> _pendingAutoFocus = [];

    // Layer-granularity dirty tracking (root vs overlay) — the first increment of dirty-region
    // repaint. Replaces the old single _needsPaint flag so a clean layer's paint walk can be skipped
    // (see RepaintTracker). Both layers start dirty, so the first frame paints everything.
    private readonly RepaintTracker _repaint = new();

    // ── Secondary OS windows ──────────────────────────────────────────────────
    // A secondary window is a full App instance sharing the main app's engine: it owns its own
    // widget tree, overlays, focus/hover/capture, and paint lists, rendered through a UI-only
    // NativeWindow surface. The MAIN app pumps SDL events once per frame and routes them here by
    // window id; CreateWindow() opens one.
    private readonly List<App> _secondaryWindows = [];
    private readonly List<Snackbar> _snackbars = [];

    private Widget? _capturedWidget;
    private Widget? _hoveredWidget;
    private int _initialFramesToPaint = 10;

    // True while the native resize event-watch is driving a live-resize frame — prevents re-entry.
    private bool _inLiveResize;

    private long _lastPaintExplosionLogMs = long.MinValue;
    private long _lastTicks;
    private Offset _mousePos;
    private bool _needsLayout = true;

    // Widgets an off-thread caller asked to re-lay-out; drained on the UI thread each frame (the queue's
    // memory barrier also publishes the widget's own pending state). See InvalidateLayoutFromAnyThread.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Widget> _crossThreadInvalidations = new();

    // ── Viewport FPS controls (for performance testing) ───────────────────────
    private long _paceAnchorTicks;

    // Set true when a ResizeEvent arrives mid-frame so Frame() re-layouts before Paint
    private bool _pendingRelayout;
    private Widget? _rightCapturedWidget;

    private Widget? _root;
    private bool _semanticsDirty = true;

    private ThemeData _theme = ThemeData.Dark;
    private TooltipBubble? _tooltipOverlay;

    // Tooltip system
    private float _tooltipTimer;
    private bool _vsync = true;

    private App(App parent, string title, uint width, uint height)
    {
        ParentApp = parent;
        Title = title;
        Engine = parent.Engine;
        NativeWindow = Engine.CreateWindow(title, width, height);
        Theme = parent.Theme;
        _lastTicks = _clock.ElapsedTicks;
        // Secondary windows do not sample DebugStats or host a devtools panel — the devtools package
        // installs on the main app only, and a second sampler would double the frame-time
        // accumulation (halved FPS). Shift+D therefore targets the main window's panel.
        // AnimationController.RequestFrameAction stays owned by the main app (it fans out to every
        // window — see the main ctor); assigning it here would strand the main window's animations.
    }

    public App(string title, uint width = 960, uint height = 640,
        string? fontPath = null, string? fontName = null)
    {
        Active = this;
        Title = title;
        Engine = new ZigoteEngine();
        FontLicenses.EnsureRegistered();

        // Prefer bundled, cross-platform fonts in Fonts/ next to the executable so the UI
        // doesn't depend on system fonts. Inter is the default UI face; Iosevka is bundled
        // separately for code-oriented views in the editor. Falls back to caller path / default.
        var fontsDir = Path.Combine(AppContext.BaseDirectory, "Fonts");
        var bundledMain = Path.Combine(fontsDir, "Inter-Regular.ttf");
        var mainFont = fontPath ?? (File.Exists(bundledMain) ? bundledMain : null);

        // Backend override: ZIGOTE_BACKEND=wgpu|auto selects the GPU backend at startup
        // (the device is created once, so this is launch-time only). Defaults to Auto (→ wgpu).
        // A native backend that isn't built yet degrades to wgpu; query Engine.Caps after init.
        var backend =
            Environment.GetEnvironmentVariable("ZIGOTE_BACKEND")?.Trim().ToLowerInvariant() switch {
                "vulkan" => RenderBackend.Vulkan,
                "d3d12" => RenderBackend.D3D12,
                "wgpu" => RenderBackend.Wgpu,
                _ => RenderBackend.Auto,
            };
        Engine.Initialize(
            width,
            height,
            title,
            mainFont,
            fontName ?? "Inter",
            backend
        );

        // Native file dialogs parent to the focused OS window (macOS sheet / Windows owner) —
        // resolved at show time so a dialog opened from a secondary window (e.g. Settings)
        // sheets onto that window. Main window is the fallback while nothing is focused.
        FileDialog.DefaultParentWindow = Engine.MainWindowId;
        FileDialog.ParentWindowProvider = () => FocusedWindowId;

        // Iosevka (monospace) for code/text widgets — registered as a named font so code views
        // can request it; the general UI stays on Inter.
        var codeFont = Path.Combine(fontsDir, "Iosevka-Regular.ttc");
        if (File.Exists(codeFont)) Engine.LoadFont("code", codeFont);

        // Material Icons — monochrome UI line-icon face, registered under the family name the
        // Icons token class draws with (PaintList.AddText(..., fontFamily: Icons.Family)). Glyphs
        // live in the Private-Use area, so they never collide with text in the default face.
        var iconFont = Path.Combine(fontsDir, "MaterialIcons-Regular.ttf");
        if (File.Exists(iconFont)) Engine.LoadFont("MaterialIcons", iconFont);

        // Color-emoji atlas needs a CBDT/CBLC/sbix font. The bundled Noto Emoji is monochrome
        // (incompatible with the color path), so emoji are only enabled if a color font is
        // bundled as Fonts/NotoColorEmoji.ttf — keeping the build free of system-font reliance.
        var colorEmoji = Path.Combine(fontsDir, "NotoColorEmoji.ttf");
        if (File.Exists(colorEmoji) && Engine.LoadFont("emoji", colorEmoji))
            Engine.AddEmojiFont("emoji");
        _lastTicks = _clock.ElapsedTicks;

        // Every animation tick routes through RequestFrameAction. It must request a *relayout*, not
        // just a repaint: the transition widgets read the controller value during Measure/Layout, not
        // only Paint — AnimatedContainer (size in Measure), ScaleTransition/SlideTransition (child
        // scale/offset in Layout), AnimatedBuilder/TweenAnimationBuilder (rebuild the subtree in
        // Measure). A paint-only tick would re-render those with stale geometry, so the animation
        // appears frozen until the next discrete event (e.g. a scroll) forces a layout pass. Layout is
        // cheap during animation — the frame is already repainting the whole tree, and per-widget
        // Measure caching short-circuits the unchanged subtrees. (FadeTransition reads the value in
        // Paint, so it would work either way.)
        // Controllers are global (Ticker.AdvanceAll), so the tick fans out to every window — a
        // controller driving a secondary window's tree must wake that window, not just the main one.
        AnimationController.RequestFrameAction = RequestLayoutAllWindows;

        // The native SDL event-watch fires this during a modal window-resize drag (main thread), when
        // the frame loop is blocked inside SDL — relayout + present a live frame so the UI tracks the
        // window continuously. Subscribed only on the main app; it routes to secondary windows itself.
        Engine.OnLiveResize += LiveResizeTick;
    }

    /// <summary>The main app this secondary window hangs off; null on the main app itself.</summary>
    public App? ParentApp { get; }

    /// <summary>The native OS window backing a secondary app; null on the main app.</summary>
    public NativeWindow? NativeWindow { get; }

    /// <summary>SDL window id of this app's OS window (for <see cref="InputEvent.WindowId" /> routing).</summary>
    public uint WindowId => NativeWindow?.Id ?? Engine.MainWindowId;

    /// <summary>True while this window exists on screen (always true for the main app).</summary>
    public bool IsOpen => NativeWindow?.IsAlive ?? true;

    /// <summary>
    ///     Whether ANY of the app's OS windows (main or secondary) has keyboard focus. Hosts should
    ///     throttle their frame loop on this, not <see cref="WindowFocused" /> — typing into a
    ///     focused secondary window must not run at background rates.
    /// </summary>
    public bool AnyWindowFocused
    {
        get
        {
            if (WindowFocused) return true;
            for (var i = 0; i < _secondaryWindows.Count; i++)
                if (_secondaryWindows[i].WindowFocused)
                    return true;
            return false;
        }
    }

    /// <summary>
    ///     SDL id of the OS window that currently has keyboard focus (main or secondary); the
    ///     main window when none does. Native file dialogs parent here so a dialog opened from
    ///     e.g. the Settings window sheets onto that window, not the main one.
    /// </summary>
    public uint FocusedWindowId
    {
        get
        {
            if (WindowFocused) return WindowId;
            for (var i = 0; i < _secondaryWindows.Count; i++)
                if (_secondaryWindows[i].WindowFocused)
                    return _secondaryWindows[i].WindowId;
            return WindowId;
        }
    }

    /// <summary>The currently active UiApp. Non-null while the app is running.</summary>
    public static App? Active { get; private set; }

    public ZigoteEngine Engine { get; }

    /// <summary>
    ///     The app-level theme, ambient for the whole window (root + overlays) via the layout-time
    ///     theme scope. Reassigning re-measures the tree so controls that read the theme in Measure
    ///     restyle on the next frame.
    /// </summary>
    public ThemeData Theme
    {
        get => _theme;
        set
        {
            if (ReferenceEquals(_theme, value)) return;
            _theme = value;
            BuildContext.Current.BumpGeneration();
            RequestLayout();
        }
    }

    /// <summary>This window's title (also shown by the in-app titlebar when chrome is active).</summary>
    public string Title { get; }

    /// <summary>
    ///     Chrome applied to this OS window (see <see cref="ApplyWindowChrome" />). Non-System
    ///     windows compose a <see cref="WindowChromeHost" /> titlebar strip above the root.
    /// </summary>
    public WindowChromeStyle ChromeStyle { get; private set; }

    public Widget? Root
    {
        get => _root;
        set
        {
            var effective = WrapWithChrome(value);
            if (_root == effective) return;
            _root?.Detach();
            _root = effective;
            _root?.Attach(this, null);
            RequestLayout();
        }
    }

    /// <summary>
    ///     Apply an in-app window chrome to this window and every current/future secondary
    ///     window: MacUnified keeps the native traffic lights over the app-drawn titlebar strip;
    ///     AdwaitaCsd draws GNOME-style buttons on a borderless window; System restores the OS
    ///     decorations. A style the native layer refuses (e.g. MacUnified off-macOS) degrades to
    ///     System for that window.
    /// </summary>
    public void ApplyWindowChrome(WindowChromeStyle style)
    {
        var effective = style;
        if (effective != WindowChromeStyle.System && !Engine.WindowChromeSet(WindowId, effective))
            effective = WindowChromeStyle.System;
        if (effective == WindowChromeStyle.System && ChromeStyle != WindowChromeStyle.System)
            Engine.WindowChromeSet(WindowId, WindowChromeStyle.System);

        if (ChromeStyle != effective)
        {
            ChromeStyle = effective;
            // Re-wrap the current root under the new chrome (the setter early-outs on identical
            // references, so detach explicitly first).
            var user = _root is WindowChromeHost host ? host.Content : _root;
            if (user is not null)
            {
                _root?.Detach();
                _root = null;
                Root = user;
            }
        }

        // Cascade the REQUESTED style — each window degrades independently.
        for (var i = 0; i < _secondaryWindows.Count; i++)
            _secondaryWindows[i].ApplyWindowChrome(style);
        RequestPaint();
    }

    private Widget? WrapWithChrome(Widget? userRoot)
    {
        if (userRoot is null || ChromeStyle == WindowChromeStyle.System ||
            userRoot is WindowChromeHost) return userRoot;
        return new WindowChromeHost(this, userRoot);
    }

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
    ///     Per-frame damage logging to stderr (<c>ZIGOTE_DEBUG_DAMAGE=1</c>): each presented frame's
    ///     FULL/PARTIAL decision, damage rects, and per-layer dirty state. Diagnostic only.
    /// </summary>
    public static readonly bool DebugDamageLog =
        Environment.GetEnvironmentVariable("ZIGOTE_DEBUG_DAMAGE") == "1";

    /// <summary>
    ///     Software cap on the (continuous) render loop, in frames per second. 0 = no cap. Paced at
    ///     the end of each frame; to exceed the display refresh, also turn <see cref="VSync" /> off.
    /// </summary>
    public int FrameRateLimit { get; set; }

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

    // ── DevTools seams ────────────────────────────────────────────────────────
    // The devtools package (Zigote.UI.DevTools) is an opt-in host include; it plugs into these
    // hooks so Zigote.UI never depends on it. Nulls until installed → Shift+D and the toggle
    // methods are harmless no-ops in a host that ships no devtools.

    /// <summary>Invoked by Shift+D / <see cref="ToggleDebugPanel" /> to open/close the devtools panel.</summary>
    public Action? OnToggleDevTools { get; set; }

    /// <summary>Invoked by <see cref="ToggleCompactStats" /> to open/close the compact stats block.</summary>
    public Action? OnToggleDevCompact { get; set; }

    public void Dispose()
    {
        // A secondary window doesn't own the engine — just close its OS window.
        if (ParentApp is not null)
        {
            Close();
            GC.SuppressFinalize(this);
            return;
        }

        for (var i = _secondaryWindows.Count - 1; i >= 0; i--) _secondaryWindows[i].Close();
        if (Active == this) Active = null;
        Engine.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Flip the focus-ring policy on a focus-source change and repaint the old ring away
    ///     (RequestFocus repaints on widget change, but the source can change for the SAME widget —
    ///     e.g. clicking the control that Tab had focused).
    /// </summary>
    private void SetFocusRingVisible(bool visible)
    {
        if (FocusRingVisible == visible) return;
        FocusRingVisible = visible;
        FocusedWidget?.MarkNeedsPaint();
        RequestPaint();
    }

    /// <summary>
    ///     Raised when the user clicks the titlebar ✕ of this secondary window, right before the
    ///     window closes itself. (On the main app the ✕ quits the application instead.)
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>Raised on the MAIN app when the OS switches light/dark appearance.</summary>
    public event Action<SystemTheme>? SystemThemeChanged;

    /// <summary>
    ///     Open a secondary UI-only OS window and return the <see cref="App" /> that hosts its
    ///     widget tree. Assign <see cref="Root" /> and it renders/receives input like the main
    ///     window; call <see cref="Close" /> (or let the user hit ✕) to destroy it.
    /// </summary>
    public App CreateWindow(string title, uint width = 720, uint height = 520)
    {
        if (ParentApp is not null) return ParentApp.CreateWindow(title, width, height);
        var win = new App(
            this,
            title,
            width,
            height
        );
        _secondaryWindows.Add(win);
        // New windows inherit the app-wide chrome (Settings, dialogs, torn-out panels…).
        if (ChromeStyle != WindowChromeStyle.System) win.ApplyWindowChrome(ChromeStyle);
        return win;
    }

    /// <summary>
    ///     Programmatic titlebar-✕: behaves exactly like the OS close button — quit request for
    ///     the main window; <see cref="CloseRequested" /> then destroy for a secondary one. The
    ///     in-app chrome's close button routes here so window owners keep their close semantics.
    /// </summary>
    public void RequestClose()
    {
        if (ParentApp is null)
        {
            RequestQuit();
            return;
        }

        CloseRequested?.Invoke();
        Close();
    }

    /// <summary>
    ///     Close this window: on a secondary window, detach its tree and destroy the OS window (the
    ///     App is dead afterwards); on the main app, request application quit.
    /// </summary>
    public void Close()
    {
        if (ParentApp is null)
        {
            RequestQuit();
            return;
        }

        ParentApp._secondaryWindows.Remove(this);
        ClearOverlays();
        Root = null;
        if (Active == this) Active = ParentApp;
        NativeWindow!.Dispose();
    }

    private void RequestLayoutAllWindows()
    {
        RequestLayout();
        for (var i = 0; i < _secondaryWindows.Count; i++)
            _secondaryWindows[i].RequestLayout();
    }

    private static Keymap CreateDefaultKeymap()
    {
        var km = new Keymap();
        km.Bind(ActionToggleDevTools, new KeyChord(KeyCode.D, Modifiers.Shift));
        km.Bind(ActionProfilerCapture, new KeyChord(KeyCode.F7));
        km.Bind(ActionFocusNext, new KeyChord(KeyCode.Tab));
        km.Bind(ActionFocusPrev, new KeyChord(KeyCode.Tab, Modifiers.Shift));
        km.Bind(ActionDismiss, new KeyChord(KeyCode.Escape));
        return km;
    }

    /// <summary>Request the application to exit after the current frame.</summary>
    public void RequestQuit()
    {
        Engine.Quit();
    }

    /// <summary>
    ///     Swap a registered font family's face at runtime (e.g. re-point <c>"Inter"</c> or
    ///     <c>"code"</c> at a different .ttf/.ttc): the native side re-registers the face under the
    ///     same family name and drops its shaped-run caches + glyph atlas; this invalidates the C#
    ///     measure cache and relayouts every window. Widgets holding cached native text layouts
    ///     (e.g. <c>CodeEditor</c>) must additionally drop those themselves.
    /// </summary>
    public bool SetFontFace(string family, string path)
    {
        if (!File.Exists(path)) return false;
        if (!Engine.LoadFont(family, path)) return false;
        TextMeasure.Invalidate();
        BuildContext.Current.BumpGeneration();
        RequestLayoutAllWindows();
        return true;
    }

    /// <summary>
    ///     Drop every text cache — native (shaped runs, glyph atlases on all windows) and managed
    ///     (measure cache) — and relayout every window. Call after a wholesale text sizing change
    ///     (e.g. a live UI font-scale switch), which must re-shape the world the same way a font
    ///     face swap does.
    /// </summary>
    public void ResetTextRendering()
    {
        Engine.ResetTextCaches();
        TextMeasure.Invalidate();
        BuildContext.Current.BumpGeneration();
        RequestLayoutAllWindows();
    }

    public void RequestLayout()
    {
        _needsLayout = true;
        _semanticsDirty = true;
        _repaint.MarkAll();
    }

    /// <summary>
    ///     Thread-safe: request that <paramref name="widget" /> be re-laid-out on the UI thread. For an
    ///     off-thread caller (a timer/async completion setting a signal that a <c>Watch</c>/reactive bind
    ///     reads) — walking the widget's <c>Parent</c> chain off-thread would race the UI thread's tree
    ///     mutation, so the actual <c>MarkNeedsLayout</c> is deferred to the next frame on the UI thread.
    ///     Without it, only the App-level layout flag is set and cached ancestors (StatelessWidget) skip
    ///     re-measuring the subtree, so a deep reactive bind never reconciles.
    /// </summary>
    public void InvalidateLayoutFromAnyThread(Widget widget)
    {
        _crossThreadInvalidations.Enqueue(widget);
        RequestLayout(); // sets _needsLayout + marks repaint; WaitEvents' 16 ms timeout picks it up
    }

    // Drained at the top of each frame on the UI thread, before layout — see InvalidateLayoutFromAnyThread.
    private void DrainCrossThreadInvalidations()
    {
        while (_crossThreadInvalidations.TryDequeue(out var w))
            if (w.Owner is not null) // still attached
                w.MarkNeedsLayout();
    }

    public void RequestPaint()
    {
        // A caller asked to repaint but named no widget, so we don't know the layer or region —
        // conservatively mark both layers with a full-frame clear. Widget-scoped repaints go through
        // RequestPaintFor instead and stay sub-rectangle.
        _repaint.MarkAll();
    }

    /// <summary>
    ///     Repaint request scoped to a single widget: damages just that widget's paint region (its
    ///     <see cref="Widget.DamageBounds" /> plus a safety margin) in its own layer, rather than forcing
    ///     a full-frame clear. This is the precise counterpart of <see cref="RequestPaint" /> — a control's
    ///     own <see cref="Widget.MarkNeedsPaint" /> (hover glow, slider thumb during a drag, blinking caret)
    ///     routes here so a self-repaint on a mouse-move / idle frame stays a sub-rectangle blit instead of
    ///     re-clearing the whole scene texture. Safely degrades to a full repaint when the region is unknown
    ///     (an unlaid-out widget), <see cref="PartialRepaintEnabled" /> is off, or the widget lives outside
    ///     the Root/overlay layers. UI-thread only, exactly like <see cref="Widget.MarkNeedsLayout" />.
    /// </summary>
    public void RequestPaintFor(Widget widget) => MarkPaintFor(widget);

    /// <summary>
    ///     Keep the frame loop pumping (both layers repainted) for at least <paramref name="count" />
    ///     more frames. Unlike <see cref="RequestPaint" /> this survives being called during the paint
    ///     walk (the per-layer dirty flags are cleared right after painting), so widgets whose output
    ///     needs trailing frames to settle after a change — the 3D viewport's temporal passes
    ///     (TAA/SSGI/auto-exposure) — can self-schedule the next frame from inside <c>Paint</c>.
    /// </summary>
    public void RequestExtraFrames(int count)
    {
        _initialFramesToPaint = Math.Max(_initialFramesToPaint, count);
    }

    /// <summary>
    ///     Mark the paint layer that <paramref name="w" /> belongs to (root vs overlay) by walking to
    ///     its top-most ancestor, recording <paramref name="w" />'s paint region as precise damage so —
    ///     when partial repaint is enabled and nothing else forced a full frame — native repaints only
    ///     that region. Falls back to a full-frame repaint for a detached/unknown widget, never skipping
    ///     a layer that might have changed.
    ///     <para>
    ///         Sources that stay sub-rectangle: the caret blink (the per-frame mark below while a text
    ///         field is focused), a value-drag control (Slider/Switch/NumberInput moving its thumb from
    ///         <c>OnPointerMove</c>), and hover styling crossing between widgets — each routes its own
    ///         <see cref="Widget.MarkNeedsPaint" /> through <see cref="RequestPaintFor" /> so only that
    ///         widget's region is damaged. A frame that also relays out or handles a discrete (non-move)
    ///         event still full-clears (see the <c>MarkAll</c> after <c>LayoutTree</c> in <c>Frame</c>),
    ///         which is why partial damage is only ever seen on mouse-move / idle frames.
    ///     </para>
    /// </summary>
    private void MarkPaintFor(Widget? w)
    {
        if (w is null)
        {
            _repaint.MarkAll();
            return;
        }

        var top = w;
        while (top.Parent is not null) top = top.Parent;

        // Damage = the widget's paint region (bounds + any paint-overflow) plus a safety margin. When
        // partial repaint is disabled, treat the mark as an unknown region so the frame full-clears.
        var region = PartialRepaintEnabled ? w.DamageBounds.Inflate(DamageMargin) : Rect.Zero;
        if (DebugDamageLog)
            Console.Error.WriteLine(System.FormattableString.Invariant(
                $"[dmg] mark {w.GetType().Name} bounds=({w.Bounds.X:F1},{w.Bounds.Y:F1} {w.Bounds.Width:F1}x{w.Bounds.Height:F1}) dmg=({region.X:F1},{region.Y:F1} {region.Width:F1}x{region.Height:F1})"));
        if (top == Root) _repaint.AddDamageRoot(region);
        else if (_overlays.Contains(top)) _repaint.AddDamageOverlay(region);
        else _repaint.MarkAll();
    }

    // ── Overlay management ────────────────────────────────────────────────────

    public void PushOverlay(Widget overlay)
    {
        _overlays.Add(overlay);
        overlay.Attach(this, null);
        // Auto-focus the first focusable inside a newly-pushed overlay (modal forms/dialogs) once it has
        // been laid out — deferred to Frame so Bounds are valid for the visibility check.
        _pendingAutoFocus.Add(overlay);
        RequestLayout();
    }

    public void PopOverlay(Widget overlay)
    {
        if (!_overlays.Remove(overlay)) return;

        _pendingAutoFocus.Remove(overlay);

        // If focus lived inside this overlay, drop it before detaching (Parent links go away on Detach).
        var focusedInside = FocusedWidget != null && IsDescendant(FocusedWidget, overlay);

        overlay.Detach();
        RequestLayout();

        // Restore the focus that was active before this overlay auto-focused (if it's still in the tree).
        var restored = false;
        for (var i = _focusRestore.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_focusRestore[i].Overlay, overlay))
            {
                var prev = _focusRestore[i].PrevFocus;
                _focusRestore.RemoveAt(i);
                RequestFocus(prev is { Owner: not null } ? prev : null);
                restored = true;
                break;
            }

        if (!restored && focusedInside) ClearFocus();
    }

    private static bool IsDescendant(Widget? node, Widget ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node.Parent;
        }

        return false;
    }

    public void ClearOverlays()
    {
        foreach (var overlay in _overlays) overlay.Detach();
        _overlays.Clear();
        // Drop bookkeeping that referenced the now-detached overlays, otherwise their subtrees (and the
        // saved prior-focus widgets) stay pinned in these lists after a ClearOverlays that bypasses
        // PopOverlay's per-overlay cleanup.
        _pendingAutoFocus.Clear();
        _focusRestore.Clear();
        _snackbars.Clear();
        _tooltipOverlay = null;
        _tooltipTimer = 0f;
        RequestLayout();
    }

    /// <summary>
    ///     Drop app-level references (focus, hover, pointer capture) to <paramref name="w" /> when it
    ///     leaves the tree. Called from <see cref="Widget.Detach" /> for every detached widget so a
    ///     removed control can never remain the focused/hovered/captured target. Idempotent and
    ///     allocation-free (reference compares); the single matching slot, if any, is cleared.
    /// </summary>
    internal void NotifyDetached(Widget w)
    {
        if (ReferenceEquals(FocusedWidget, w)) RequestFocus(null);
        if (ReferenceEquals(_hoveredWidget, w))
        {
            _hoveredWidget = null;
            HideTooltip();
        }

        if (ReferenceEquals(_capturedWidget, w)) _capturedWidget = null;
        if (ReferenceEquals(_rightCapturedWidget, w)) _rightCapturedWidget = null;
    }

    // ── Snackbar ──────────────────────────────────────────────────────────────

    public void ShowSnackbar(string message, float duration = 3f,
        string? actionLabel = null, Action? onAction = null)
    {
        var snack = new Snackbar(
            this,
            message,
            duration,
            actionLabel,
            onAction
        );
        _snackbars.Add(snack);
        PushOverlay(snack);
    }

    // ── Frame loop ────────────────────────────────────────────────────────────

    public void Frame()
    {
        // Compute dt
        var now = _clock.ElapsedTicks;
        DeltaTime = (float)(now - _lastTicks) / Stopwatch.Frequency;
        _lastTicks = now;

        if (Root is null)
        {
            Engine.PollEventsInto(
                _events
            ); // drain so the queue can't back up; reuse buffer (no alloc)
            // Secondary windows stay live even while the main window has no tree yet.
            RouteEventsToSecondaryWindows();
            PumpSecondaryWindows();
            FileDialog.Pump();
            return;
        }

        // Apply a pending hot-reload (Edit & Continue) before deciding whether to idle, so an edit
        // wakes and rebuilds the tree on the next frame. The metadata-update handler only flips a flag
        // (possibly off-thread); the actual tree mutation happens here, on the UI thread.
        if (HotReload.HasPendingReload)
            ApplyHotReload();

        // Yield to the OS when nothing needs rendering. This is the main idle path —
        // without this the C# loop spins at thousands of iterations/sec burning CPU.
        // WaitEvents blocks until an SDL event arrives or 16 ms passes (~60 fps cap).
        // Animations that called RequestPaint() set _needsPaint, and structural changes
        // set _needsLayout, so we only block when there is genuinely nothing to do.
        // Only block on the OS when there is genuinely nothing to do — no pending paint/layout, no
        // continuous mode, and no animation running. A running ticker (any AnimationController) MUST
        // keep the loop pumping every frame, otherwise the animation freezes until the next input
        // event wakes us. WaitEvents still has a 16 ms timeout, but gating on Ticker.AnyActive makes
        // smooth animation independent of that timeout.
        // The debug menu (or compact stats overlay) must keep the loop pumping so its live metrics never
        // freeze — including when the window is in the background. Without this, an open panel only set
        // _needsPaint *after* this gate, so the loop still blocked here on the 16 ms event wait (capping
        // the rate well below 60 and stalling entirely on an unfocused window).
        if (!_repaint.AnyDirty && !_needsLayout && !ContinuousUpdate && !ForceContinuousRender &&
            !Ticker.AnyActive &&
            !WantsContinuousFrame() && !HotReload.HasPendingReload &&
            !AnySecondaryWindowWantsFrame())
            using (Profiler.Scope("WaitEvents"))
            {
                Engine.WaitEvents();
            }

        Engine.PollEventsInto(_events);
        // Move events belonging to secondary OS windows out of the main batch and into each
        // window's own buffer — they are dispatched in that window's SecondaryFrame below.
        RouteEventsToSecondaryWindows();
        // Start queued / complete finished native file dialogs. Runs every frame regardless of
        // repaint activity — WaitEvents' 16 ms timeout above bounds the polling latency.
        FileDialog.Pump();
        var events = _events;

        // Advance vsync-driven tickers. Each playing AnimationController calls
        // RequestFrameAction inside Tick(), which requests a relayout (see ctor) so animations that
        // change size/position/rebuilt content are not painted with last frame's stale geometry.
        Ticker.AdvanceAll(DeltaTime);

        // Service the audio engine each frame: ages + reaps fire-and-forget one-shots (UI clicks /
        // positioned pings) so a held oscillator one-shot is silenced after its duration. Cheap no-op
        // until audio is opened (lazy on first sound).
        Engine.AudioUpdate(DeltaTime);

        // Keep rendering while anything is animating (covers controllers whose Tick this frame did
        // not happen to land a visible change yet, and guarantees the next frame is not skipped). An
        // animation can drive either layer, so mark both.
        if (Ticker.AnyActive) _repaint.MarkAll();

        if (_initialFramesToPaint > 0)
        {
            _repaint.MarkAll();
            _initialFramesToPaint--;
        }

        // Sample the shared diagnostics stats once per frame (main window only — a second sampler
        // doubles the frame-time accumulation). Runs unconditionally so an always-on FPS badge and the
        // charts' history rings stay live even before any devtools panel is opened.
        DebugStats.Sample(DeltaTime);

        // Drive per-frame devtools refresh. While anything wants continuous frames (an open panel or
        // compact stats) force an overlay repaint so live metrics keep advancing — the WaitEvents gate
        // above already keeps the loop pumping. This only marks the overlay layer, so the (often large)
        // root tree is NOT re-walked.
        if (WantsContinuousFrame()) _repaint.MarkOverlay();
        FrameTick?.Invoke(DeltaTime);

        // Advance tooltip timer
        AdvanceTooltip(DeltaTime);

        // Advance and prune finished snackbars (transient — repaint while any is showing). Snackbars
        // are overlays, so only the overlay layer needs re-walking.
        if (_snackbars.Count > 0) _repaint.MarkOverlay();
        for (var i = _snackbars.Count - 1; i >= 0; i--)
        {
            var s = _snackbars[i];
            s.Tick(DeltaTime);
            if (s.IsDone)
            {
                PopOverlay(s);
                _snackbars.RemoveAt(i);
            }
        }

        // A tooltip pending its hover delay repaints the overlay only until the bubble is up — the
        // show/hide frames are marked by PushOverlay/PopOverlay, so a settled bubble idles instead of
        // producing full-damage frames forever. A blinking caret lives in whichever layer its field is
        // in; a read-only / caret-less text client (e.g. the docked read-only CodeEditor) opts out of
        // the per-frame caret repaint via WantsCaretBlink, so a focused viewer idles too.
        if (_tooltipTimer > 0 && _tooltipOverlay is null) _repaint.MarkOverlay();
        if (FocusedWidget is ITextInputClient { WantsCaretBlink: true })
            MarkPaintFor(FocusedWidget);

        // Apply off-thread layout requests (a timer/async signal change a reactive bind reads) on the UI
        // thread, BEFORE layout — so the Parent-chain walk is race-free and the marked ancestors don't
        // cache-skip re-measuring the affected subtree.
        DrainCrossThreadInvalidations();

        // Bring layout current BEFORE dispatching events so HitTest sees valid Bounds.
        // Layout runs only when structure/size actually changed (MarkNeedsLayout) or the
        // window resized — never merely because input arrived. This is the key fix that
        // stops every mouse-move from re-laying-out the whole tree.
        if (_needsLayout || _pendingRelayout)
            LayoutTree();

        // Dispatch events. Handlers may MarkNeedsPaint (visual state) or MarkNeedsLayout
        // (size/structure). Plain pointer-moves are cheap: they relayout nothing and only
        // repaint on hover transitions or while a widget is captured (drag) — see the
        // MouseMove branch of DispatchEvent.
        _pendingRelayout = false;
        var discrete = false;
        foreach (var evt in events)
        {
            DispatchEvent(evt);
            if (evt is not MouseMoveEvent) discrete = true;
        }

        // A handler may have changed structure/size, or a discrete (non-move) interaction
        // occurred — bring layout current once before painting. Per-widget Measure caching
        // makes the common case (nothing actually changed) close to free, and discrete
        // events fire at human rates, so this is cheap. Mouse-moves never reach here.
        if (_needsLayout || _pendingRelayout || discrete)
        {
            LayoutTree();
            _repaint.MarkAll();
        }

        // Auto-focus the first control inside any overlay pushed this frame, now that it is laid out.
        ProcessPendingAutoFocus();

        // Secondary windows run BEFORE the main window's dirty gate: their routed events must be
        // dispatched (and their surfaces rendered) even on frames where the main window is idle —
        // clicking inside the settings window while the editor sits still is exactly that case.
        PumpSecondaryWindows();

        if (!_repaint.AnyDirty && !ContinuousUpdate && !ForceContinuousRender)
            return;

        // Continuous modes re-walk both layers every frame (renderer-throughput / live test).
        if (ContinuousUpdate || ForceContinuousRender) _repaint.MarkAll();

        PaintAndPresent();

        // Push the accessibility tree to a platform bridge after layout settled, only when it changed.
        if (SemanticsBridge != null && _semanticsDirty)
        {
            SemanticsBridge.Update(BuildSemantics());
            _semanticsDirty = false;
        }

        PaceFrame();
    }

    /// <summary>
    ///     A runaway paint path (e.g. a mark stroking megapixel-long dashed segments) can emit enough
    ///     geometry that the native shape vertex buffer exceeds wgpu's maximum buffer size — an
    ///     uncatchable native abort. Log a per-kind breakdown BEFORE the frame reaches the GPU so the
    ///     terminal names the culprit even when the process dies on the very next call.
    /// </summary>
    private void WarnIfPaintExplosion(PaintList list, string layer)
    {
        const int threshold = 100_000;
        if (list.Count <= threshold) return;
        var now = Environment.TickCount64;
        if (now - _lastPaintExplosionLogMs < 5000) return;
        _lastPaintExplosionLogMs = now;

        Span<int> counts = stackalloc int[32];
        var commands = list.DebugCommands;
        for (var i = 0; i < commands.Count; i++)
        {
            var kind = commands[i].Kind;
            if (kind < counts.Length) counts[kind]++;
        }

        var sb = new StringBuilder(256);
        sb.Append("PAINT EXPLOSION: ").Append(layer).Append(" layer emitted ")
            .Append(list.Count).Append(" commands this frame —");
        for (var k = 0; k < counts.Length; k++)
        {
            if (counts[k] == 0) continue;
            sb.Append(' ').Append((PaintCommandKind)k).Append('=')
                .Append(counts[k]);
        }

        var message = sb.ToString();
        DebugLog.Error(message, "paint");
        Console.Error.WriteLine(message);
    }

    /// <summary>
    ///     Paint the dirty layer(s) and submit + present the frame. Shared by <see cref="Frame" /> and
    ///     the live-resize path. Partial repaint: re-emit only the layer(s) that changed — the clean
    ///     layer keeps last frame's command buffer, which is still re-submitted, so the GPU always
    ///     receives the full frame while a clean layer's paint walk is skipped. Root and overlays stay
    ///     in separate lists so they composite correctly (overlays above the 3D viewport in Pass 2).
    /// </summary>
    private void PaintAndPresent()
    {
        if (Root is null) return;

        var rootWalked = _repaint.RootDirty;
        var overlayWalked = _repaint.OverlayDirty;
        using (Profiler.Scope("UI.Paint"))
        {
            if (_repaint.RootDirty)
            {
                _paint.Clear();
                Root.Paint(_paint);
                _repaint.RootPainted();
            }

            if (_repaint.OverlayDirty)
            {
                _overlayPaint.Clear();
                foreach (var ov in _overlays) ov.Paint(_overlayPaint);
                _repaint.OverlayPainted();
            }
        }

        // Partial-frame consistency: a re-walked list reflects CURRENT widget state, but replay only
        // touches the damage rects — any op that changed without marking damage this frame (a missed
        // MarkNeedsPaint, an overlay moved by a plain property write) would repaint torn: new inside
        // the rects it overlaps, stale outside. Diff each re-walked list against what was last
        // submitted and widen the damage to cover every changed op; unboundable changes degrade the
        // frame to a full repaint. Runs only on partial frames — full frames are consistent by
        // construction, and clean layers re-submit their previous (on-screen) list verbatim.
        if (PartialRepaintEnabled && !ContinuousUpdate && !ForceContinuousRender)
        {
            if (rootWalked && _repaint.DamageCount > 0)
                WidenDamageFromPaintDiff(_rootSnapshot, _paint, isOverlay: false);
            if (overlayWalked && _repaint.DamageCount > 0)
                WidenDamageFromPaintDiff(_overlaySnapshot, _overlayPaint, isOverlay: true);
            if (rootWalked) _rootSnapshot.Capture(_paint);
            if (overlayWalked) _overlaySnapshot.Capture(_overlayPaint);
        }

        DebugStats.UiPaintCommands = _paint.Count;
        DebugStats.OverlayPaintCommands = _overlayPaint.Count;
        WarnIfPaintExplosion(_paint, "root");
        WarnIfPaintExplosion(_overlayPaint, "overlay");

        using (Profiler.Scope("Render.Submit"))
        {
            using (Profiler.Scope("Render.Begin"))
            {
                Engine.BeginFrame(DeltaTime);
            }

            using (Profiler.Scope("Render.Submit2D"))
            {
                Engine.SubmitPaintCommands(_paint);
            }

            if (_overlayPaint.Count > 0)
                using (Profiler.Scope("Render.SubmitOverlay"))
                {
                    Engine.SubmitOverlayCommands(_overlayPaint);
                }

            // Sub-rectangle partial repaint: hand native the precise damaged regions for this frame. When
            // the whole frame is dirty (any non-precise change, continuous mode, ZIGOTE_SHOT) this span is
            // empty, which native treats as a full clear — byte-identical to the pre-existing path.
            Engine.SubmitFrameDamage(_repaint.Damage);

            if (DebugDamageLog)
            {
                if (_repaint.Damage.IsEmpty)
                {
                    Console.Error.WriteLine(
                        $"[dmg] FULL root={_repaint.RootDirty} overlay={_repaint.OverlayDirty} rootOps={_paint.Count} ovOps={_overlayPaint.Count}");
                }
                else
                {
                    var sb = new System.Text.StringBuilder("[dmg] PARTIAL ");
                    foreach (var r in _repaint.Damage)
                        sb.Append(System.FormattableString.Invariant($"({r.X:F1},{r.Y:F1} {r.Width:F1}x{r.Height:F1}) "));
                    sb.Append(System.FormattableString.Invariant(
                        $"root={_repaint.RootDirty} overlay={_repaint.OverlayDirty} rootOps={_paint.Count} ovOps={_overlayPaint.Count}"));
                    Console.Error.WriteLine(sb.ToString());
                }
            }

            // The bulk of a continuous editor/game frame: the full 3D scene render (shadow → G-buffer →
            // SSAO → SSR → bloom → tonemap → TAA → overlay composite) plus the swapchain present. When
            // these dominate, the cost is the scene, not the open debug panel.
            using (Profiler.Scope("Render.GPU"))
            {
                Engine.RenderFrameV2();
            }

            using (Profiler.Scope("Render.Present"))
            {
                Engine.EndFrame();
            }

            // Damage is consumed — clear it so the next frame starts fresh (and only becomes full again
            // if something marks it so). Kept inside the render block so a skipped/early-returned frame
            // (nothing dirty) leaves last frame's already-empty damage untouched.
            _repaint.ResetDamage();
        }
    }

    /// <summary>
    ///     Add the bounds of every command that changed between <paramref name="snapshot" /> (what is
    ///     on screen) and <paramref name="current" /> (what will be replayed) to this frame's damage;
    ///     unboundable changes force a full repaint. See the call site in <see cref="PaintAndPresent" />.
    /// </summary>
    private void WidenDamageFromPaintDiff(PaintSnapshot snapshot, PaintList current, bool isOverlay)
    {
        Span<Rect> changed = stackalloc Rect[PaintSnapshot.MaxChangedRects];
        switch (snapshot.Diff(current, changed, out var count))
        {
            case PaintDiffResult.Identical:
                return;
            case PaintDiffResult.Bounded:
                for (var i = 0; i < count; i++)
                    _repaint.AddDamageBoundsOnly(changed[i].Inflate(DamageMargin));
                if (DebugDamageLog)
                    Console.Error.WriteLine(System.FormattableString.Invariant(
                        $"[dmg] diff {(isOverlay ? "overlay" : "root")} widened by {count} rect(s), first=({changed[0].X:F1},{changed[0].Y:F1} {changed[0].Width:F1}x{changed[0].Height:F1})"));
                return;
            case PaintDiffResult.Unbounded:
                _repaint.ForceFullDamage();
                if (DebugDamageLog)
                    Console.Error.WriteLine($"[dmg] diff {(isOverlay ? "overlay" : "root")} UNBOUNDED -> full");
                return;
        }
    }

    // ── Live window resize (SDL modal-loop bridge) ────────────────────────────

    /// <summary>
    ///     Invoked (on the main thread) from the native SDL event-watch while the user drags a window
    ///     edge. During that modal drag the OS blocks the normal frame loop, so this is the one place we
    ///     can relayout + paint + present a live frame — the UI tracks the window continuously instead of
    ///     snapping to the new size only on release. We are nested inside the blocked poll/wait call, so a
    ///     render here must not re-enter (guarded); events are not pumped, only layout + paint + present.
    /// </summary>
    private void LiveResizeTick(uint windowId)
    {
        if (_inLiveResize) return;
        _inLiveResize = true;
        try
        {
            if (windowId == 0 || windowId == Engine.MainWindowId)
            {
                if (Root is null) return;
                _pendingRelayout = true;
                LayoutTree();
                _repaint.MarkAll();
                PaintAndPresent();
            }
            else
            {
                for (var i = 0; i < _secondaryWindows.Count; i++)
                    if (_secondaryWindows[i].WindowId == windowId)
                    {
                        _secondaryWindows[i].LiveResizeSecondary();
                        break;
                    }
            }
        }
        finally
        {
            _inLiveResize = false;
        }
    }

    /// <summary>Live-resize relayout + present for a secondary OS window (main-window counterpart above).</summary>
    private void LiveResizeSecondary()
    {
        if (NativeWindow is not { IsAlive: true } || Root is null) return;
        NativeWindow.RefreshSize();

        var prevActive = Active;
        Active = this;
        try
        {
            _pendingRelayout = true;
            LayoutTree();
            _repaint.MarkAll();
            SecondaryPaintAndPresent();
        }
        finally
        {
            Active = prevActive;
        }
    }

    // ── Secondary-window frame plumbing ───────────────────────────────────────

    private void RouteEventsToSecondaryWindows()
    {
        if (_secondaryWindows.Count == 0) return;
        for (var i = 0; i < _secondaryWindows.Count; i++) _secondaryWindows[i]._events.Clear();

        var mainId = Engine.MainWindowId;
        var write = 0;
        for (var read = 0; read < _events.Count; read++)
        {
            var evt = _events[read];
            App? target = null;
            if (evt.WindowId != 0 && evt.WindowId != mainId)
                for (var i = 0; i < _secondaryWindows.Count; i++)
                    if (_secondaryWindows[i].WindowId == evt.WindowId)
                    {
                        target = _secondaryWindows[i];
                        break;
                    }

            if (target is not null) target._events.Add(evt);
            else _events[write++] = evt; // main-window or global event — keep in the main batch
        }

        _events.RemoveRange(write, _events.Count - write);
    }

    private bool AnySecondaryWindowWantsFrame()
    {
        for (var i = 0; i < _secondaryWindows.Count; i++)
        {
            var w = _secondaryWindows[i];
            if (w._repaint.AnyDirty || w._needsLayout || w._pendingRelayout) return true;
            if (w.WantsContinuousFrame()) return true;
            if (w.FocusedWidget is ITextInputClient { WantsCaretBlink: true }) return true;
        }

        return false;
    }

    private void PumpSecondaryWindows()
    {
        // Reverse: a window may Close() itself out of the list mid-frame (✕ handling).
        for (var i = _secondaryWindows.Count - 1; i >= 0; i--)
            _secondaryWindows[i].SecondaryFrame(DeltaTime);
    }

    /// <summary>
    ///     Per-frame processing for a secondary window, driven from the main app's
    ///     <see cref="Frame" />: dispatch this window's routed events, layout, paint, and present
    ///     through its <see cref="NativeWindow" />. Global work (SDL poll, ticker advance, audio) is
    ///     the main frame's job and must not repeat here.
    /// </summary>
    private void SecondaryFrame(float dt)
    {
        if (NativeWindow is not { IsAlive: true } || Root is null)
        {
            _events.Clear();
            return;
        }

        DeltaTime = dt;

        // Ambient App.Active follows the window being processed so widget code that resolves the
        // app statically (focus requests, Time reads, popup hosts) lands on this window.
        var prevActive = Active;
        Active = this;
        try
        {
            if (WantsContinuousFrame()) _repaint.MarkOverlay();
            FrameTick?.Invoke(DeltaTime);

            AdvanceTooltip(DeltaTime);

            if (_snackbars.Count > 0) _repaint.MarkOverlay();
            for (var i = _snackbars.Count - 1; i >= 0; i--)
            {
                var s = _snackbars[i];
                s.Tick(DeltaTime);
                if (s.IsDone)
                {
                    PopOverlay(s);
                    _snackbars.RemoveAt(i);
                }
            }

            if (_tooltipTimer > 0 && _tooltipOverlay is null) _repaint.MarkOverlay();
            if (FocusedWidget is ITextInputClient { WantsCaretBlink: true })
                MarkPaintFor(FocusedWidget);

            if (_needsLayout || _pendingRelayout)
                LayoutTree();

            _pendingRelayout = false;
            var discrete = false;
            foreach (var evt in _events)
            {
                DispatchEvent(evt);
                if (evt is not MouseMoveEvent) discrete = true;
            }

            _events.Clear();

            // The ✕ handler may have closed the window mid-dispatch — its tree is gone.
            if (NativeWindow is not { IsAlive: true } || Root is null) return;

            if (_needsLayout || _pendingRelayout || discrete)
            {
                LayoutTree();
                _repaint.MarkAll();
            }

            ProcessPendingAutoFocus();

            if (!_repaint.AnyDirty && !ContinuousUpdate) return;
            if (ContinuousUpdate) _repaint.MarkAll();

            SecondaryPaintAndPresent();
        }
        finally
        {
            Active = prevActive;
        }
    }

    /// <summary>Paint the dirty layer(s) and present through this secondary window's own native target.</summary>
    private void SecondaryPaintAndPresent()
    {
        if (NativeWindow is null || Root is null) return;

        if (_repaint.RootDirty)
        {
            _paint.Clear();
            Root.Paint(_paint);
            _repaint.RootPainted();
        }

        if (_repaint.OverlayDirty)
        {
            _overlayPaint.Clear();
            foreach (var ov in _overlays) ov.Paint(_overlayPaint);
            _repaint.OverlayPainted();
        }

        NativeWindow.SubmitPaint(_paint);
        NativeWindow.SubmitOverlay(_overlayPaint);
        NativeWindow.Render();
        _repaint.ResetDamage();
    }

    private void ProcessPendingAutoFocus()
    {
        if (_pendingAutoFocus.Count == 0) return;
        foreach (var ov in _pendingAutoFocus)
        {
            if (ov is INoAutoFocus || !_overlays.Contains(ov) ||
                !FocusTraversal.HasFocusable(ov)) continue;
            _focusRestore.Add((ov, FocusedWidget));
            FocusFirstIn(ov);
        }

        _pendingAutoFocus.Clear();
    }

    /// <summary>
    ///     Software frame-rate limiter for testing: when <see cref="FrameRateLimit" /> &gt; 0, sleep
    ///     (then briefly spin for accuracy) so the continuous render loop holds the target FPS. Resyncs
    ///     instead of bursting when a heavy frame falls behind, so the cap never overshoots afterwards.
    /// </summary>
    private void PaceFrame()
    {
        if (FrameRateLimit <= 0)
        {
            _paceAnchorTicks = 0;
            return;
        }

        var interval = Stopwatch.Frequency / FrameRateLimit;
        var now = _clock.ElapsedTicks;
        if (_paceAnchorTicks == 0) _paceAnchorTicks = now;
        _paceAnchorTicks += interval;
        if (_paceAnchorTicks < now)
        {
            _paceAnchorTicks = now; // fell behind — resync rather than catch up in a burst
            return;
        }

        var oneMs = Stopwatch.Frequency / 1000;
        while (true)
        {
            var remaining = _paceAnchorTicks - _clock.ElapsedTicks;
            if (remaining <= 0) break;
            if (remaining > 2 * oneMs) Thread.Sleep(1);
            else Thread.SpinWait(64);
        }
    }

    /// <summary>
    ///     Apply a pending hot-reload: re-run every <c>Build()</c> in the live tree (root + overlays) so
    ///     edited widget code takes effect, then force a relayout. Widget instances and their
    ///     <see cref="WidgetState" /> are preserved — only <c>Build</c> re-runs. UI thread only.
    /// </summary>
    private void ApplyHotReload()
    {
        if (!HotReload.TryTakePending(out var types)) return;

        if (Root is not null) HotReload.MarkSubtreeForRebuild(Root);
        foreach (var ov in _overlays) HotReload.MarkSubtreeForRebuild(ov);
        foreach (var win in _secondaryWindows)
        {
            if (win.Root is not null) HotReload.MarkSubtreeForRebuild(win.Root);
            foreach (var ov in win._overlays) HotReload.MarkSubtreeForRebuild(ov);
            win.RequestLayout();
        }

        // Bump the build generation so even Measure-cached subtrees that did not depend on an inherited
        // widget re-measure against the freshly built children.
        BuildContext.Current.BumpGeneration();
        RequestLayout();
        HotReload.RaiseReloaded(types);
    }

    private void LayoutTree()
    {
        if (Root is null) return;

        BuildContext.Current.Reset();
        // Refresh ambient context (MediaQuery) before measuring — every widget can read it.
        BuildContext.Current.MediaQuery = new MediaQueryData(
            HostLogicalWidth,
            HostLogicalHeight,
            HostScale
        );

        var c = Constraints.Tight(HostLogicalWidth, HostLogicalHeight);

        // The app theme scope wraps the WHOLE pass — root and overlays — so ThemeProvider.Of
        // resolves the live App.Theme everywhere (see _appThemeScope). The scope instance is reused
        // (updated silently) to keep the layout pass allocation-free.
        _appThemeScope.SetDataSilently(Theme);
        var ctx = BuildContext.Current;
        ctx.Push(_appThemeScope);
        try
        {
            Root.Measure(c);
            Root.Layout(Offset.Zero);

            foreach (var ov in _overlays)
            {
                ov.Measure(c);
                ov.Layout(Offset.Zero);
            }
        }
        finally
        {
            ctx.Pop(_appThemeScope);
        }

        _needsLayout = false;
        _pendingRelayout = false;
    }

    // ── Focus ─────────────────────────────────────────────────────────────────

    public void RequestFocus(Widget? widget)
    {
        if (FocusedWidget == widget) return;
        FocusedWidget?.Focused = false;
        if (FocusedWidget is ITextInputClient) StopHostTextInput();

        FocusedWidget = widget;

        FocusedWidget?.Focused = true;
        if (FocusedWidget is ITextInputClient) StartHostTextInput();

        _semanticsDirty = true;
        if (SemanticsBridge != null)
            SemanticsBridge.FocusChanged(
                widget is null
                    ? null
                    : SemanticsRoot?.Flatten()
                        .FirstOrDefault(n => ReferenceEquals(n.Source, widget))
            );
    }

    public void ClearFocus()
    {
        RequestFocus(null);
    }

    // SDL text input is per OS window: a focused text field in a secondary window must engage the
    // IME on THAT window or composed text never arrives (SDL routes it by keyboard focus).
    private void StartHostTextInput()
    {
        if (NativeWindow is not null) NativeWindow.StartTextInput();
        else Engine.StartTextInput();
    }

    private void StopHostTextInput()
    {
        if (NativeWindow is not null) NativeWindow.StopTextInput();
        else Engine.StopTextInput();
    }

    // ── Accessibility ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Build (and cache as <see cref="SemanticsRoot" />) the current accessibility tree from the root
    ///     widget + overlays. Pure — safe to call from a debug panel each frame or from a headless test.
    /// </summary>
    public SemanticsNode BuildSemantics()
    {
        var screen = new Size(HostLogicalWidth, HostLogicalHeight);
        SemanticsRoot = SemanticsBuilder.Build(Root, _overlays, screen);
        return SemanticsRoot;
    }

    /// <summary>
    ///     Fired once per frame (after tickers advance, before layout) with the frame delta. The
    ///     devtools package subscribes to refresh its live panels/charts on the UI thread.
    /// </summary>
    public event Action<float>? FrameTick;

    /// <summary>
    ///     Register a predicate that, while true, keeps the frame loop pumping every frame (so live
    ///     metrics never freeze — even when the window is unfocused). The devtools overlay registers
    ///     one that returns true while its panel or compact stats are visible.
    /// </summary>
    public void AddContinuousFrameSource(Func<bool> wants)
    {
        _continuousFrameSources.Add(wants);
    }

    internal bool WantsContinuousFrame()
    {
        for (var i = 0; i < _continuousFrameSources.Count; i++)
            if (_continuousFrameSources[i]())
                return true;
        return false;
    }

    /// <summary>Toggle the full devtools panel (no-op until a devtools package is installed).</summary>
    public void ToggleDebugPanel()
    {
        OnToggleDevTools?.Invoke();
    }

    /// <summary>Toggle the compact always-on stats block (no-op until a devtools package is installed).</summary>
    public void ToggleCompactStats()
    {
        OnToggleDevCompact?.Invoke();
    }

    /// <summary>
    ///     Focusables in reading (tree) order within the active focus scope. The scope is the topmost
    ///     modal overlay that contains a focusable (so a dialog traps Tab), else the nearest enclosing
    ///     <see cref="FocusScope" /> of the current focus, else the whole root tree. Zero-area (collapsed
    ///     / off-screen) focusables are skipped so Tab never lands on something invisible.
    /// </summary>
    private List<Widget> GetFocusableWidgets()
    {
        var scope = ActiveFocusScope();
        return scope != null ? FocusTraversal.Focusables(scope) : [];
    }

    private Widget? ActiveFocusScope()
    {
        for (var i = _overlays.Count - 1; i >= 0; i--)
        {
            var ov = _overlays[i];
            if (FocusTraversal.HasFocusable(ov)) return ov;
        }

        if (FocusedWidget != null)
        {
            var node = FocusedWidget.Parent;
            while (node != null)
            {
                if (node is FocusScope { Trap: true }) return node;
                node = node.Parent;
            }
        }

        return Root;
    }

    private void MoveFocusByTab(bool backwards)
    {
        var next = FocusTraversal.NextInTab(GetFocusableWidgets(), FocusedWidget, backwards);
        if (next != null)
        {
            SetFocusRingVisible(true);
            RequestFocus(next);
        }
    }

    /// <summary>
    ///     Geometric arrow-key focus move within the active scope. Only invoked when the focused widget
    ///     does not consume arrows itself (<see cref="Widget.HandlesDirectionalKeys" />).
    /// </summary>
    private bool MoveFocusDirectional(float dirX, float dirY)
    {
        if (FocusedWidget is null) return false;
        var best = FocusTraversal.Directional(
            GetFocusableWidgets(),
            FocusedWidget,
            dirX,
            dirY
        );
        if (best is null) return false;
        SetFocusRingVisible(true);
        RequestFocus(best);
        return true;
    }

    /// <summary>
    ///     Esc: dismiss the top-most dismissable overlay; if none, clear focus. Returns true if
    ///     handled.
    /// </summary>
    private bool HandleEscape()
    {
        for (var i = _overlays.Count - 1; i >= 0; i--)
            if (_overlays[i] is IDismissableOverlay d && d.RequestDismiss())
                return true;

        // A window whose ROOT is dismissable closes on Esc — this is how dialogs hosted as
        // separate OS windows (e.g. the file browser) get the same Esc-cancels behavior that
        // overlay dialogs get from the stack above. Look through the chrome wrapper: the
        // dismissable widget is the app's actual root, not the titlebar host.
        var rootTarget = Root is WindowChromeHost chromeHost ? chromeHost.Content : Root;
        if (rootTarget is IDismissableOverlay rootDismiss && rootDismiss.RequestDismiss())
            return true;

        if (FocusedWidget != null)
        {
            ClearFocus();
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Move focus to the first focusable inside <paramref name="scope" /> (used for overlay
    ///     auto-focus).
    /// </summary>
    private void FocusFirstIn(Widget scope)
    {
        var list = FocusTraversal.Focusables(scope);
        if (list.Count > 0)
        {
            SetFocusRingVisible(true);
            RequestFocus(list[0]);
        }
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────

    private void AdvanceTooltip(float dt)
    {
        string? text = null;
        var w = _hoveredWidget;
        while (w != null)
        {
            if (w.TooltipText != null)
            {
                text = w.TooltipText;
                break;
            }

            w = w.Parent;
        }

        if (text is null)
        {
            HideTooltip();
            return;
        }

        // The timer accrues only while no bubble is shown (it gates the per-frame overlay mark in
        // Frame); once up, the bubble just tracks the pointer — it repositions on the next relayout,
        // and those frames are already marked by whatever caused the relayout.
        if (_tooltipOverlay is null)
        {
            _tooltipTimer += dt;
            if (_tooltipTimer > 0.7f)
            {
                _tooltipOverlay = new TooltipBubble(text, _mousePos, Theme);
                PushOverlay(_tooltipOverlay);
            }
        }
        else
        {
            _tooltipOverlay.Position = _mousePos;
        }
    }

    private void HideTooltip()
    {
        // The timer may be accruing with no bubble shown yet — always clear it so the per-frame
        // overlay mark in Frame stops.
        _tooltipTimer = 0f;
        if (_tooltipOverlay is null) return;
        PopOverlay(_tooltipOverlay);
        _tooltipOverlay = null;
    }

    // ── Event dispatch ────────────────────────────────────────────────────────

    private void DispatchEvent(InputEvent evt)
    {
        switch (evt)
        {
            case WindowFocusEvent wf:
                // App/host state only — never routed to widgets (widget focus is unrelated). Hosts read
                // WindowFocused to throttle background frame rates; repaint once on regain so anything
                // that went stale while throttled refreshes immediately.
                WindowFocused = wf.Focused;
                if (wf.Focused) _repaint.MarkAll();
                break;

            case MouseMoveEvent m:
                _mousePos = new Offset(m.X, m.Y);
                if (_capturedWidget is not null)
                {
                    // Drag in progress — the captured widget (e.g. a Slider) updates its
                    // visuals from the pointer position, so repaint its layer (dragging a root
                    // control therefore doesn't re-walk the overlay layer, and vice-versa).
                    _capturedWidget.OnPointerMove(_mousePos);
                    _rightCapturedWidget?.OnPointerMove(_mousePos);
                    MarkPaintFor(_capturedWidget);
                    if (_rightCapturedWidget is not null) MarkPaintFor(_rightCapturedWidget);
                }
                else if (_rightCapturedWidget is not null)
                {
                    _rightCapturedWidget.OnPointerMove(_mousePos);
                    MarkPaintFor(_rightCapturedWidget);
                }
                else
                {
                    var hit = HitTestAll(_mousePos);
                    if (hit != _hoveredWidget)
                    {
                        // Hover transition — repaint just the widget losing and the widget gaining
                        // hover styling, each in its own layer, rather than the whole frame. Controls
                        // that recolour on hover also call MarkNeedsPaint themselves (→ the same
                        // RequestPaintFor path); damaging both here additionally covers widgets that
                        // rely on the app to repaint them across a crossing. A null side (moving to/from
                        // empty space) contributes no region; MarkPaintFor degrades to a full clear if
                        // partial repaint is off or a widget's region is unknown.
                        var exited = _hoveredWidget;
                        exited?.OnPointerExit();
                        HideTooltip();
                        _hoveredWidget = hit;
                        hit?.OnPointerEnter();
                        if (exited is not null) MarkPaintFor(exited);
                        if (hit is not null) MarkPaintFor(hit);
                    }

                    hit?.OnPointerMove(_mousePos);
                }

                ResolveAndApplyCursor();
                break;

            case MouseDownEvent { Button: MouseButton.Left } d:
            {
                var point = new Offset(d.X, d.Y);
                var hit = HitTestAll(point);
                _capturedWidget = hit;

                SetFocusRingVisible(false);
                if (hit is { Focusable: true })
                    RequestFocus(hit);
                else if (hit is not null && hit != FocusedWidget)
                    ClearFocus();

                hit?.OnPointerDown(point);
                // A press may start a drag (e.g. a split divider) whose cursor differs from hover.
                ResolveAndApplyCursor();
                break;
            }

            case MouseUpEvent { Button: MouseButton.Left } u:
            {
                var point = new Offset(u.X, u.Y);
                _capturedWidget?.OnPointerUp(point);
                _capturedWidget = null;
                // Drag ended — re-resolve from whatever is now under the pointer.
                ResolveAndApplyCursor();
                break;
            }

            case MouseDownEvent { Button: MouseButton.Right } r:
            {
                var point = new Offset(r.X, r.Y);
                var hit = HitTestAll(point);
                _rightCapturedWidget = hit;
                hit?.OnRightClick(point);
                break;
            }

            case MouseUpEvent { Button: MouseButton.Right } ru:
            {
                var point = new Offset(ru.X, ru.Y);
                _rightCapturedWidget?.OnRightPointerUp(point);
                _rightCapturedWidget = null;
                break;
            }

            case ScrollEvent s:
            {
                var dx = s.ScrollX;
                var dy = s.ScrollY;
                // Native normalizes flipped/natural wheel events to a canonical orientation; re-apply the
                // host's natural-scroll preference so it isn't silently discarded.
                if (HonorHostScrollOrientation &&
                    Engine.GetScrollOrientation() == ScrollOrientation.Flipped)
                {
                    dx = -dx;
                    dy = -dy;
                }

                var target = HitTestAll(_mousePos);
                target?.OnScroll(dx, dy);
                break;
            }

            case KeyEvent k:
                CurrentModifiers = k.Modifiers;
                CurrentKeyRepeat = k.Repeat;

                if (k.Down)
                {
                    var action = Keymap.Resolve(k.Key, k.Modifiers);

                    // Global toggles — fire once per physical press (ignore OS auto-repeat), any focus.
                    if (!k.Repeat && action == ActionToggleDevTools)
                    {
                        OnToggleDevTools?.Invoke();
                        break;
                    }

                    if (!k.Repeat && action == ActionProfilerCapture)
                    {
                        Profiler.Capture(120, "profile_capture.json");
                        break;
                    }

                    // Focus-scoped shortcuts — skipped while a keyboard-trap widget (e.g. the devtools
                    // console field) holds focus so it keeps Tab (command auto-complete) and Esc for itself.
                    if (FocusedWidget is not IKeyboardTrap)
                    {
                        if (action == ActionFocusNext)
                        {
                            MoveFocusByTab(false);
                            break;
                        }

                        if (action == ActionFocusPrev)
                        {
                            MoveFocusByTab(true);
                            break;
                        }

                        if (action == ActionDismiss && HandleEscape()) break;

                        // Directional focus traversal — only when the focused widget doesn't use arrows
                        // itself (a button/checkbox/tab moves focus; a text field / slider keeps them).
                        if (FocusedWidget is { HandlesDirectionalKeys: false })
                        {
                            var handled = k.Key switch {
                                KeyCode.Right => MoveFocusDirectional(1f, 0f),
                                KeyCode.Left => MoveFocusDirectional(-1f, 0f),
                                KeyCode.Down => MoveFocusDirectional(0f, 1f),
                                KeyCode.Up => MoveFocusDirectional(0f, -1f),
                                _ => false,
                            };
                            if (handled) break;
                        }
                    }
                }

                FocusedWidget?.OnKey(
                    k.KeyChar,
                    k.Scancode,
                    k.Down,
                    k.Modifiers
                );
                break;

            case TextInputEvent ti:
                if (FocusedWidget is ITextInputClient && !string.IsNullOrEmpty(ti.Text))
                    UiFeedback.Type?.Invoke();
                FocusedWidget?.OnTextInput(ti.Text);
                break;

            case TextCompositionEvent composition:
                FocusedWidget?.OnTextComposition(
                    composition.Text,
                    composition.SelectionStart,
                    composition.SelectionLength
                );
                break;

            case ResizeEvent:
                // Secondary windows track their own surface size (the engine's cached size is the
                // main window's); refresh before the relayout so MediaQuery sees the new bounds.
                NativeWindow?.RefreshSize();
                _pendingRelayout = true;
                break;

            case WindowCloseEvent:
                // Titlebar ✕ — quit for the main window, close for a secondary one. Events are
                // routed per window, so this instance is always the one being closed.
                if (ParentApp is null)
                {
                    RequestQuit();
                }
                else
                {
                    CloseRequested?.Invoke();
                    Close();
                }

                break;

            case SystemThemeEvent themeEvt:
                SystemThemeChanged?.Invoke(themeEvt.Theme);
                break;

            case DropBeginEvent:
            case DropFileEvent:
            case DropTextEvent:
            case DropPositionEvent:
            case DropCompleteEvent:
                HandleExternalDropEvent(evt);
                break;
        }
    }

    // Hit-test overlays (top-most first) then root
    private Widget? HitTestAll(Offset point)
    {
        Widget.CurrentScrollParent = null;
        for (var i = _overlays.Count - 1; i >= 0; i--)
        {
            var hit = _overlays[i].HitTest(point);
            if (hit is not null)
            {
                hit.ScrollParent = Widget.CurrentScrollParent;
                return hit;
            }
        }

        Widget.CurrentScrollParent = null;
        var rootHit = Root?.HitTest(point);
        rootHit?.ScrollParent = Widget.CurrentScrollParent;
        return rootHit;
    }

    /// <summary>
    ///     Pick the mouse cursor for the current pointer position and push it to the OS if it changed.
    ///     The widget capturing a drag wins (so e.g. a split divider keeps its resize cursor even if the
    ///     pointer strays off the divider mid-drag); otherwise the hovered widget. We walk up the parent
    ///     chain until a widget returns a non-null <see cref="Widget.GetCursor" />, defaulting to the
    ///     arrow. Called from the pointer dispatch so it tracks hover + drag transitions.
    /// </summary>
    private void ResolveAndApplyCursor()
    {
        var target = _capturedWidget ?? _hoveredWidget;
        var cursor = MouseCursor.Default;
        for (var w = target; w is not null; w = w.Parent)
        {
            var c = w.GetCursor(_mousePos);
            if (c is not null)
            {
                cursor = c.Value;
                break;
            }
        }

        if (cursor == _appliedCursor) return;
        _appliedCursor = cursor;
        Engine.SetCursor(cursor);
    }
}