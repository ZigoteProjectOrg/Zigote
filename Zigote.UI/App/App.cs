using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Diagnostics;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.Core.Platform;
using Zigote.Core.Rendering;
using Zigote.Core.State;
using Zigote.UI.Debug;
using Zigote.UI.Licensing;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Focus;

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

    /// <summary>
    ///     Width of the macOS close/minimize/zoom cluster plus its margin — where the leading edge
    ///     of a titlebar's content belongs on macOS. Exposed as a constant so an app that draws the
    ///     lights itself (client-side decorations) can size its cluster to the same band the OS
    ///     would have used, instead of guessing a second number.
    /// </summary>
    public const float MacTrafficLightInset = 78f;

    /// <summary>Longest edge a capture may reach, in pixels — under every backend's texture limit.</summary>
    private const float MaxCapturePixels = 8192f;

    // Last cursor pushed to the OS. Static because SDL's active cursor is process-global (one for all
    // windows), so a single mirror de-dupes correctly no matter which window's App resolved it.
    private static MouseCursor _appliedCursor = MouseCursor.Default;

    // Reused ThemeProvider pushed around BOTH the root and overlay measure/layout so every widget —
    // including Material controls in raw-App hosts like the editor, which have no ThemeProvider of
    // their own — resolves ThemeProvider.Of to the live App.Theme instead of the Dark fallback. A
    // tree that injects its own ThemeProvider (ZigoteApp) still wins: nearest ancestor first.
    // Updated silently each layout — no per-frame allocation on the hot path.
    private readonly ThemeProvider _appThemeScope = new(ThemeData.Dark);

    // Scratch list for CaptureUi: root + overlay composited into the single list the capture renders.
    private readonly PaintList _capturePaint = new();

    // Delta-time tracking
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // DevTools seam (Shift+D). App no longer owns a concrete debug overlay — a devtools package
    // (Zigote.UI.DevTools) installs itself through OnToggleDevTools / FrameTick / continuous-frame
    // sources, so Zigote.UI stays free of any charts/panels dependency. Predicates registered here
    // keep the frame loop pumping while live metrics are on-screen (see WantsContinuousFrame()).
    private readonly List<Func<bool>> _continuousFrameSources = [];

    // Widgets an off-thread caller asked to re-lay-out; drained on the UI thread each frame (the queue's
    // memory barrier also publishes the widget's own pending state). See InvalidateLayoutFromAnyThread.
    private readonly ConcurrentQueue<Widget>
        _crossThreadInvalidations = new();

    // Reused each frame so draining input events allocates nothing on an idle frame — unlike
    // PollEvents().ToList(), which allocated an enumerator + a list every frame.
    private readonly List<InputEvent> _events = [];
    private readonly List<(Widget Overlay, Widget? PrevFocus)> _focusRestore = [];

    // Synthetic input from the inspect socket, joined to the real event batch each frame. A list,
    // not a queue: only ever touched on the UI thread (InspectServer injects via Post).
    private readonly List<InputEvent> _injected = [];

    private readonly PaintList _overlayPaint = new();
    private readonly PaintSnapshot _overlaySnapshot = new();

    // Overlay stack — dialogs, tooltips, snackbars painted on top of Root
    private readonly List<Widget> _overlays = [];
    private readonly PaintList _paint = new();

    // Overlays pushed this frame that still need their first focusable focused (after layout gives bounds),
    // and the focus to restore when each is popped (modal focus save/restore).
    private readonly List<Widget> _pendingAutoFocus = [];

    private readonly ConcurrentQueue<Action> _posted = new();

    // Lazily-created root Background scope (see the Background property). Nullable so an app that
    // never touches it pays nothing — the frame loop skips RunFrame and the idle gate skips FrameIdle.
    private Zigote.Core.Threading.Background? _background;

    // Like _posted, but drained at the END of the frame — after event dispatch, layout and paint.
    private readonly ConcurrentQueue<Action>
        _postedAfterFrame = new();

    // Layer-granularity dirty tracking (root vs overlay) — the first increment of dirty-region
    // repaint. Replaces the old single _needsPaint flag so a clean layer's paint walk can be skipped
    // (see RepaintTracker). Both layers start dirty, so the first frame paints everything.
    private readonly RepaintTracker _repaint = new();

    // Last-submitted paint lists, diffed against re-walked lists on partial frames so unmarked
    // visual changes widen the damage instead of tearing (see PaintAndPresent).
    private readonly PaintSnapshot _rootSnapshot = new();

    // ── Secondary OS windows ──────────────────────────────────────────────────
    // A secondary window is a full App instance sharing the main app's engine: it owns its own
    // widget tree, overlays, focus/hover/capture, and paint lists, rendered through a UI-only
    // NativeWindow surface. The MAIN app pumps SDL events once per frame and routes them here by
    // window id; CreateWindow() opens one.
    private readonly List<App> _secondaryWindows = [];
    private readonly List<Snackbar> _snackbars = [];

    private Widget? _capturedWidget;

    private float _csdCornerRadius = 15f;

    private Color _csdOutlineColor = Color.Transparent;
    private Widget? _hoveredWidget;

    // True while the native resize event-watch is driving a live-resize frame — prevents re-entry.
    private bool _inLiveResize;
    private int _initialFramesToPaint = 10;

    private long _lastPaintExplosionLogMs = long.MinValue;
    private long _lastTicks;
    private Offset _mousePos;
    private bool _needsLayout = true;

    // ── Viewport FPS controls (for performance testing) ───────────────────────
    private long _paceAnchorTicks;

    // Set when a frame blocks in WaitEvents (idle/hidden); the NEXT frame's dt includes that
    // wait, so its length says nothing about jank. See the capture at the top of FrameCore.
    private bool _idledInWait;

    // True while the UI thread is parked inside Engine.WaitEvents — Post reads it (Volatile) to
    // decide whether a cross-thread completion should wake the OS wait early.
    private bool _inWaitEvents;

    // Set true when a ResizeEvent arrives mid-frame so Frame() re-layouts before Paint
    private bool _pendingRelayout;
    private Widget? _rightCapturedWidget;

    private Widget? _root;

    // Device safe-area insets (notch / home indicator), fed into MediaQueryData.Padding.
    // Queried lazily by LayoutTree and re-queried after a resize (rotation moves the notch).
    private EdgeInsets _safeArea = EdgeInsets.Zero;
    private bool _safeAreaValid;
    private bool _semanticsDirty = true;

    private ThemeData _theme = ThemeData.Dark;
    private Widget? _titleBarLeading;
    private TooltipBubble? _tooltipOverlay;

    // Tooltip system
    private float _tooltipTimer;
    private bool _vsync = true;

    private App(App parent, string title, uint width, uint height)
    {
        ParentApp = parent;
        Title = title;
        Engine = parent.Engine;
        NativeWindow = Engine.CreateWindow(title: title, width: width, height: height);
        Theme = parent.Theme;
        _lastTicks = _clock.ElapsedTicks;
        // Secondary windows do not sample DebugStats or host a devtools panel — the devtools package
        // installs on the main app only, and a second sampler would double the frame-time
        // accumulation (halved FPS). Shift+D therefore targets the main window's panel.
        // AnimationController.RequestFrameAction stays owned by the main app (it fans out to every
        // window — see the main ctor); assigning it here would strand the main window's animations.
    }

    /// <param name="gpuPreference">
    ///     Which GPU to run on when the machine has more than one. Defaults to
    ///     <see cref="GpuPowerPreference.Efficiency" /> because a plain <see cref="App" /> is a 2D/UI
    ///     app — on a laptop that keeps the discrete card asleep instead of waking it to draw
    ///     rectangles. Hosts that drive a 3D scene (the editor, the player,
    ///     <see cref="Zigote.Game.GameApp" />) pass <see cref="GpuPowerPreference.Performance" />.
    ///     <c>ZIGOTE_GPU</c> / <c>ZIGOTE_GPU_POWER</c> override this at launch.
    /// </param>
    /// <param name="gpuIndex">
    ///     Pin a specific GPU by its index in <see cref="ZigoteEngine.EnumerateGpus" />, ignoring
    ///     <paramref name="gpuPreference" />. -1 (the default) means "decide from the preference".
    ///     This is the editor's GPU setting and the developer testing knob; an index that doesn't
    ///     exist (or can't drive the window) falls back to the automatic pick rather than failing.
    /// </param>
    public App(string title, uint width = 960, uint height = 640,
        string? fontPath = null, string? fontName = null,
        GpuPowerPreference gpuPreference = GpuPowerPreference.Efficiency,
        int gpuIndex = -1,
        bool transparentWindow = false)
    {
        Active = this;
        MainApp = this;
        Title = title;
        Engine = new ZigoteEngine();
        FontLicenses.EnsureRegistered();

        // Alpha-composited window for CSD rounded corners — a creation-time property, so it must
        // be requested before Initialize. Harmless where the platform refuses it (the per-frame
        // rounding check reads back what actually took).
        if (transparentWindow) Engine.SetWindowTransparent(true);

        // Prefer bundled, cross-platform fonts in Fonts/ next to the executable so the UI
        // doesn't depend on system fonts. Inter is the default UI face; Iosevka is bundled
        // separately for code-oriented views in the editor. Falls back to caller path / default.
        string fontsDir = Path.Combine(path1: AppContext.BaseDirectory, path2: "Fonts");
        string bundledMain = Path.Combine(path1: fontsDir, path2: "Inter-Regular.ttf");
        string? mainFont = fontPath ?? (File.Exists(bundledMain) ? bundledMain : null);

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
            width: width,
            height: height,
            title: title,
            fontPath: mainFont,
            fontName: fontName ?? "Inter",
            backend: backend,
            gpuPreference: gpuPreference,
            gpuIndex: gpuIndex
        );

        // Native file dialogs parent to the focused OS window (macOS sheet / Windows owner) —
        // resolved at show time so a dialog opened from a secondary window (e.g. Settings)
        // sheets onto that window. Main window is the fallback while nothing is focused.
        FileDialog.DefaultParentWindow = Engine.MainWindowId;
        FileDialog.ParentWindowProvider = () => FocusedWindowId;

        // Real Inter weight variants — the FreeType renderer shapes one face per family, so each
        // weight registers as its own family and FontFaces maps FontWeight → family at the
        // AddText/MeasureText choke points. Only when the default face is in use (fontPath null).
        if (fontPath is null)
        {
            foreach ((var weight, string faceName) in (ReadOnlySpan<(FontWeight, string)>) [
                         (FontWeight.Medium, "Inter-Medium"),
                         (FontWeight.SemiBold, "Inter-SemiBold"),
                         (FontWeight.Bold, "Inter-Bold"),
                         (FontWeight.ExtraBold, "Inter-ExtraBold"),
                     ])
            {
                string facePath = Path.Combine(path1: fontsDir, path2: faceName + ".ttf");
                if (File.Exists(facePath) && Engine.LoadFont(name: faceName, path: facePath))
                    FontFaces.RegisterWeight(weight: weight, family: faceName);
            }
        }

        // Iosevka (monospace) for code/text widgets — registered as a named font so code views
        // can request it; the general UI stays on Inter.
        string codeFont = Path.Combine(path1: fontsDir, path2: "Iosevka-Regular.ttc");
        if (File.Exists(codeFont)) Engine.LoadFont(name: "code", path: codeFont);

        // Material Icons — monochrome UI line-icon face, registered under the family name the
        // Icons token class draws with (PaintList.AddText(..., fontFamily: Icons.Family)). Glyphs
        // live in the Private-Use area, so they never collide with text in the default face.
        string iconFont = Path.Combine(path1: fontsDir, path2: "MaterialIcons-Regular.ttf");
        if (File.Exists(iconFont)) Engine.LoadFont(name: "MaterialIcons", path: iconFont);

        // Color-emoji atlas needs a font with BGRA bitmap strikes (CBDT/CBLC or sbix). A bundled
        // Fonts/NotoColorEmoji.ttf wins if present; otherwise the platform's own color-emoji face
        // is borrowed (Apple Color Emoji, Segoe UI Emoji, a system Noto) — see SystemFonts. The
        // engine probes registration and refuses fonts its color path cannot actually draw, so a
        // COLR-outline-only candidate degrades to the monochrome fallback below instead of
        // silently capturing every emoji and rendering nothing.
        string colorEmoji = Path.Combine(path1: fontsDir, path2: "NotoColorEmoji.ttf");
        bool emojiRegistered = File.Exists(colorEmoji) &&
                               Engine.LoadFont(name: "emoji", path: colorEmoji) &&
                               Engine.AddEmojiFont("emoji");

        // Scripts the bundled face cannot draw — Japanese, Korean, Chinese, Arabic, Thai — come
        // from the platform's own fonts. Registered last, so the app's own faces always win for
        // the characters they do cover. See SystemFonts for why these are borrowed, not bundled.
        SystemFonts.Register(engine: Engine, needColorEmoji: !emojiRegistered);

        // The bundled monochrome Noto Emoji is the last resort: registered as a plain script
        // fallback (not the color path), it turns emoji on systems without any usable color font
        // from .notdef boxes into legible outline glyphs. Registered after the script fallbacks
        // so CJK/Arabic text never resolves through an emoji face.
        string monoEmoji = Path.Combine(path1: fontsDir, path2: "NotoEmoji-Regular.ttf");
        if (File.Exists(monoEmoji) && Engine.LoadFont(name: "emoji-mono", path: monoEmoji))
            Engine.AddFallbackFont("emoji-mono");
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

        // Platform plugins start once the engine is up — their Start may talk to it — and before
        // the first frame, so a widget built on frame one can already invoke their channels. Heads
        // register plugins before constructing the App; anything registered later starts itself.
        // A plugin that implements IAppLifecycleObserver is wired into lifecycle delivery for as
        // long as it runs — Core cannot name the interface, so the app watches the host's events
        // (subscribed first, so plugins started below and late-registered ones are both seen; the
        // Running sweep catches any a head started before constructing the App).
        PluginHost.PluginStarted += OnPluginStartedWireLifecycle;
        PluginHost.PluginStopped += OnPluginStoppedUnwireLifecycle;
        foreach (var running in PluginHost.Running) OnPluginStartedWireLifecycle(running);
        PluginHost.StartAll();
    }

    private void OnPluginStartedWireLifecycle(IPlatformPlugin plugin)
    {
        if (plugin is IAppLifecycleObserver observer) AddLifecycleObserver(observer);
    }

    private void OnPluginStoppedUnwireLifecycle(IPlatformPlugin plugin)
    {
        if (plugin is IAppLifecycleObserver observer) RemoveLifecycleObserver(observer);
    }

    // True while the measure/layout pass or the root/overlay paint walk is running. A reactive
    // subtree swap landing now would mutate the tree mid-walk (shrink a ListView's items between
    // its VisibleRange and the row loop, grow a ResponsiveGrid's children mid-measure via an
    // OnScrolled load-more signal) — Watch.OnChanged checks this and defers the swap to the next
    // frame instead. Watch.Measure's own deferred-apply entry point is unaffected: it swaps at a
    // point the walk is designed to tolerate.
    internal bool InTreeWalk { get; private set; }

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
            for (int i = 0; i < _secondaryWindows.Count; i++)
            {
                if (_secondaryWindows[i].WindowFocused)
                    return true;
            }

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
            for (int i = 0; i < _secondaryWindows.Count; i++)
            {
                if (_secondaryWindows[i].WindowFocused)
                    return _secondaryWindows[i].WindowId;
            }

            return WindowId;
        }
    }

    /// <summary>The currently active UiApp. Non-null while the app is running.</summary>
    public static App? Active { get; private set; }

    /// <summary>The main (window-owning) App — secondary windows resolve through it.</summary>
    internal static App? MainApp { get; private set; }

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
            if (ReferenceEquals(objA: _theme, objB: value)) return;
            _theme = value;
            BuildContext.Current.BumpGeneration();
            RequestLayout();
        }
    }

    /// <summary>This window's title (also shown by the in-app titlebar when chrome is active).</summary>
    public string Title { get; }

    /// <summary>
    ///     Chrome applied to this OS window (see <see cref="ApplyWindowChrome" />). AdwaitaCsd
    ///     windows compose a <see cref="WindowChromeHost" /> headerbar above the root (it must
    ///     carry the close/minimize/maximize buttons); MacUnified windows have NO strip — the
    ///     content extends under the transparent titlebar and the native traffic lights float
    ///     over it, so top-level layouts should respect <see cref="TitleBarLeftInset" />.
    /// </summary>
    public WindowChromeStyle ChromeStyle { get; private set; }

    /// <summary>
    ///     Left inset the native traffic lights occupy in MacUnified chrome — top-left
    ///     content (toolbars) should lead with this much space. 0 in other chromes.
    /// </summary>
    public float TitleBarLeftInset =>
        ChromeStyle == WindowChromeStyle.MacUnified ? MacTrafficLightInset : 0f;

    /// <summary>
    ///     Suggested top inset for MacUnified windows whose content has no toolbar row to
    ///     absorb the titlebar band (e.g. the Settings window). 0 in other chromes.
    /// </summary>
    public float TitleBarTopInset =>
        ChromeStyle == WindowChromeStyle.MacUnified ? 28f : 0f;

    /// <summary>
    ///     MacUnified: the height of the top band that acts as the draggable titlebar
    ///     wherever no interactive control claims the point.
    /// </summary>
    public float TitleBarDragHeight { get; set; } = 38f;

    /// <summary>
    ///     Corner radius of the window frame under Adwaita CSD chrome. libadwaita's
    ///     <c>--window-radius</c> is <c>$button_radius + 6</c> (15px as of GNOME 51); a rounder or
    ///     squarer corner is the tell that gives away a not-quite-GNOME window sitting next to real
    ///     ones. Only observed while the window is unmaximized, and — where the renderer draws the
    ///     corner rather than the OS — on a compositor that granted an alpha channel.
    /// </summary>
    public float CsdCornerRadius
    {
        get => _csdCornerRadius;
        set
        {
            if (_csdCornerRadius.Equals(value)) return;
            _csdCornerRadius = value;
            Engine.WindowChromeSetCornerRadius(windowId: WindowId, radius: value);
            RequestPaint();
        }
    }

    /// <summary>
    ///     Hairline drawn just inside the CSD window edge — libadwaita's
    ///     <c>window.csd { outline: 1px solid rgb(255 255 255 / 7%); outline-offset: -1px }</c>.
    ///     Transparent (the default) draws nothing, so only a design system that asks for one pays
    ///     for it. It is what keeps a dark window from bleeding into a dark desktop behind it, and
    ///     it follows the rounded corner rather than the window rect.
    /// </summary>
    public Color CsdOutlineColor
    {
        get => _csdOutlineColor;
        set
        {
            if (_csdOutlineColor.Equals(value)) return;
            _csdOutlineColor = value;
            RequestPaint();
        }
    }

    /// <summary>
    ///     Whether the corner belongs to the renderer at all. macOS masks the CSD window's own
    ///     layer (see <c>macos_window_chrome.m</c>): the OS cuts an antialiased, correctly-shadowed
    ///     corner after the frame is composited, and clipping the paint to a second rounded rect
    ///     underneath it only strands whatever the render target was cleared to in the sliver
    ///     between the two curves.
    /// </summary>
    private static bool PlatformRoundsCsdCorners => OperatingSystem.IsMacOS();

    /// <summary>
    ///     This window's paint is clipped to a rounded rect this frame: CSD chrome on a window the
    ///     compositor really composites with alpha (the alpha-0 clear then shows the desktop through
    ///     the corner cutouts). Square while maximized/fullscreen, like GNOME — that state only
    ///     flips alongside window events, which already dirty both layers via the resize path.
    /// </summary>
    private bool CsdRounded => !PlatformRoundsCsdCorners &&
                               ChromeStyle == WindowChromeStyle.AdwaitaCsd &&
                               Engine.WindowIsTransparent(WindowId) &&
                               !Engine.WindowIsMaximized(WindowId);

    // The surface being painted, which under a device preview is the device, not the window: the CSD
    // rounded-corner clip is taken from this, and clipping a 852pt-tall phone to a 760pt window is how
    // the bottom of every tall preview went black.
    private Rect WindowRect => new(
        x: 0f,
        y: 0f,
        width: LayoutWidth,
        height: LayoutHeight
    );

    public Widget? Root
    {
        get => _root;
        set
        {
            var effective = WrapWithChrome(value);
            if (_root == effective) return;
            _root?.Detach();
            _root = effective;
            _root?.Attach(owner: this, parent: null);
            RequestLayout();
        }
    }

    /// <summary>
    ///     True when this window composes an in-app titlebar strip that can host
    ///     <see cref="TitleBarLeading" /> — i.e. AdwaitaCsd chrome. Layouts check this to decide
    ///     whether their menu bar can move up into the titlebar row or needs its own strip.
    /// </summary>
    public bool HasTitleBarStrip => ChromeStyle == WindowChromeStyle.AdwaitaCsd;

    /// <summary>
    ///     Widget hosted at the left of the in-app titlebar (GNOME headerbar style — the app menu
    ///     shares the titlebar row instead of costing a second strip). Ignored while
    ///     <see cref="HasTitleBarStrip" /> is false; set it before assigning <see cref="Root" /> so
    ///     the strip is built with it.
    /// </summary>
    public Widget? TitleBarLeading
    {
        get => _titleBarLeading;
        set
        {
            if (ReferenceEquals(objA: _titleBarLeading, objB: value)) return;
            _titleBarLeading = value;
            if (_root is WindowChromeHost host)
            {
                host.Bar.Leading = value;
                value?.Attach(owner: this, parent: host.Bar);
                RequestLayout();
            }
        }
    }

    /// <summary>
    ///     AdwaitaCsd without the injected <see cref="WindowChromeHost" /> strip: the app's own
    ///     headerbars carry the window buttons and register themselves in
    ///     <see cref="CsdDragSurfaces" /> (the GNOME headerbar-as-titlebar pattern). Set before
    ///     assigning <see cref="Root" />.
    /// </summary>
    public bool SuppressChromeStrip { get; set; }

    /// <summary>
    ///     Widgets whose bounds act as the draggable titlebar when the chrome strip is suppressed
    ///     — points over interactive children inside them still reach the app. Registered by
    ///     headerbar widgets on Attach/Detach.
    /// </summary>
    public List<Widget> CsdDragSurfaces { get; } = [];

    // ── DevTools seams ────────────────────────────────────────────────────────
    // The devtools package (Zigote.UI.DevTools) is an opt-in host include; it plugs into these
    // hooks so Zigote.UI never depends on it. Nulls until installed → Shift+D and the toggle
    // methods are harmless no-ops in a host that ships no devtools.

    /// <summary>Invoked by Shift+D / <see cref="ToggleDebugPanel" /> to open/close the devtools panel.</summary>
    public Action? OnToggleDevTools { get; set; }

    /// <summary>Invoked by <see cref="ToggleCompactStats" /> to open/close the compact stats block.</summary>
    public Action? OnToggleDevCompact { get; set; }

    // ── Locale seam ───────────────────────────────────────────────────────────
    // A locale in Zigote is a LocalizationsScope in the widget tree, not a global switch — so the
    // scope registers itself here when it attaches, and Zigote.UI never depends on the
    // Localizations package. Same shape as the DevTools seams above. Null → the app has no
    // localization and the inspect socket's `locale`/`locales` commands say so.

    /// <summary>Switch the app's locale by BCP-47 tag. False when the tag does not parse.</summary>
    public Func<string, bool>? SetLocale { get; set; }

    /// <summary>The active locale tag and the tags the app supports.</summary>
    public Func<(string Current, IReadOnlyList<string> Supported)>? LocaleInfo { get; set; }

    /// <summary>
    ///     Consulted before the main window closes. Return true to keep the application alive —
    ///     the handler is then responsible for what happens instead, which is normally
    ///     <see cref="Hide" />.
    ///     <para>
    ///         This is the seam a background-capable app needs: a media player, a sync client or a
    ///         chat app closes its window and keeps working, and only an explicit Quit ends the
    ///         process. It is deliberately <i>not</i> consulted by <see cref="RequestQuit" />, so a
    ///         Quit from a menu, a media key or a session manager still quits.
    ///     </para>
    /// </summary>
    public Func<bool>? OnCloseRequest { get; set; }

    /// <summary>
    ///     The main window is hidden but the application is running: no window on screen, no
    ///     rendering, and the frame loop still turning so whatever the app does in the background
    ///     keeps happening. Always false for a secondary window, which closes rather than hides.
    /// </summary>
    public bool Hidden { get; private set; }

    /// <summary>
    ///     Lay the tree out at this size rather than the window's, or null for the window's.
    ///     <para>
    ///         This is what a device preview is: the live tree — with its state, its animations and its
    ///         hot-reloaded <c>Build()</c> bodies — measured at an iPhone's logical size, so everything
    ///         that reads <see cref="Widgets.MediaQuery" /> or a breakpoint behaves as it would on that
    ///         device. Rendering a fixed-size box inside a desktop window would look similar and adapt
    ///         to the wrong size, which is the one thing a device preview must not do.
    ///     </para>
    ///     <para>
    ///         Input hit-testing still works in window coordinates, so a preview at a size other than
    ///         the window's is meant to be viewed with the window hidden.
    ///     </para>
    /// </summary>
    internal Size? PreviewSize { get; set; }

    internal float LayoutWidth => PreviewSize?.Width ?? HostLogicalWidth;
    internal float LayoutHeight => PreviewSize?.Height ?? HostLogicalHeight;

    /// <summary>
    ///     Paint the dirty layer(s) and submit + present the frame. Shared by <see cref="Frame" /> and
    ///     the live-resize path. Partial repaint: re-emit only the layer(s) that changed — the clean
    ///     layer keeps last frame's command buffer, which is still re-submitted, so the GPU always
    ///     receives the full frame while a clean layer's paint walk is skipped. Root and overlays stay
    ///     in separate lists so they composite correctly (overlays above the 3D viewport in Pass 2).
    /// </summary>
    /// <summary>
    ///     Bumped whenever a paint walk actually ran. The inspect stream compares it to skip the
    ///     whole capture (offscreen render, BMP round-trip, byte compare) on frames where nothing
    ///     painted — an idle app with a stream attached does no capture work at all.
    /// </summary>
    internal int PaintVersion { get; private set; }

    /// <summary>
    ///     The app's root <see cref="Zigote.Core.Threading.Background" /> scope — structured
    ///     background work whose results land on the UI thread. <c>Deliver.WhenIdle</c> results and
    ///     <c>Slice</c> jobs are drained by the frame loop against a per-frame budget, so a flood of
    ///     completions fills in over several frames instead of stalling one. Secondary windows share
    ///     the main app's scope (one UI thread, one drain). Created on first use; child scopes for a
    ///     screen come from <c>Background.Child</c>.
    /// </summary>
    public Zigote.Core.Threading.Background Background =>
        ParentApp?.Background ?? (_background ??= new Zigote.Core.Threading.Background(Post));

    public void Dispose()
    {
        _background?.Dispose();
        // A secondary window doesn't own the engine — just close its OS window.
        if (ParentApp is not null)
        {
            Close();
            GC.SuppressFinalize(this);
            return;
        }

        for (int i = _secondaryWindows.Count - 1; i >= 0; i--) _secondaryWindows[i].Close();
        if (Active == this) Active = null;
        // Plugins stop while the engine still exists (their Stop may talk to it); then the channel
        // receiver detaches so a late send from a platform thread cannot reach a dead runtime.
        PluginHost.StopAll();
        PluginHost.PluginStarted -= OnPluginStartedWireLifecycle;
        PluginHost.PluginStopped -= OnPluginStoppedUnwireLifecycle;
        PlatformChannel.Shutdown();
        Engine.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Paint <see cref="CsdOutlineColor" /> over the content, inside the same rounded clip. On
    ///     top, not under: the outline's job is to bound the window, and a headerbar or sidebar
    ///     painted to the edge would swallow it.
    /// </summary>
    private void PaintCsdOutline(bool rounded)
    {
        if (!rounded || _csdOutlineColor.A <= 0f) return;
        _paint.AddBorder(bounds: WindowRect, color: _csdOutlineColor, radius: CsdCornerRadius);
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
        if (effective != WindowChromeStyle.System &&
            !Engine.WindowChromeSet(windowId: WindowId, style: effective))
            effective = WindowChromeStyle.System;
        if (effective == WindowChromeStyle.System && ChromeStyle != WindowChromeStyle.System)
            Engine.WindowChromeSet(windowId: WindowId, style: WindowChromeStyle.System);

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

        if (effective != WindowChromeStyle.System) EnsureDragHitProvider(Engine);
        // After the style, not before: applying one allocates the window's native chrome entry and
        // resets the radius it remembers to the default.
        if (effective == WindowChromeStyle.AdwaitaCsd)
            Engine.WindowChromeSetCornerRadius(windowId: WindowId, radius: CsdCornerRadius);

        // Cascade the REQUESTED style — each window degrades independently.
        for (int i = 0; i < _secondaryWindows.Count; i++)
            _secondaryWindows[i].ApplyWindowChrome(style);
        RequestPaint();
    }

    /// <summary>
    ///     Only Adwaita composes a strip — it must host the CSD buttons. MacUnified keeps
    ///     the content full-bleed (the native traffic lights float over it).
    /// </summary>
    private Widget? WrapWithChrome(Widget? userRoot)
    {
        if (userRoot is null || ChromeStyle != WindowChromeStyle.AdwaitaCsd ||
            SuppressChromeStrip || userRoot is WindowChromeHost) return userRoot;
        return new WindowChromeHost(window: this, content: userRoot) {
            Bar = { Leading = _titleBarLeading },
        };
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
        if (ParentApp is not null)
            return ParentApp.CreateWindow(title: title, width: width, height: height);
        var win = new App(
            parent: this,
            title: title,
            width: width,
            height: height
        );
        _secondaryWindows.Add(win);
        // New windows inherit the app-wide chrome (Settings, dialogs, torn-out panels…) — including
        // whether the app draws its own titlebar, or the window would get an injected chrome strip
        // above the headerbar its content already carries.
        win.SuppressChromeStrip = SuppressChromeStrip;
        if (ChromeStyle != WindowChromeStyle.System) win.ApplyWindowChrome(ChromeStyle);
        return win;
    }

    /// <summary>
    ///     Take the main window off screen without ending the application. Painting stops (there is
    ///     nothing to paint to) and the loop drops to a background cadence; <see cref="Show" />
    ///     brings it all back.
    /// </summary>
    public void Hide()
    {
        if (ParentApp is not null || Hidden) return;
        Hidden = true;
        Engine.MainWindowSetVisible(false);
    }

    /// <summary>
    ///     Bring the main window back and redraw it from scratch — it painted nothing while
    ///     it was away, so every layer is stale.
    /// </summary>
    public void Show()
    {
        if (ParentApp is not null || !Hidden) return;
        Hidden = false;
        Engine.MainWindowSetVisible(true);
        _safeAreaValid = false;
        RequestLayout();
        _repaint.MarkAll();
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
            if (OnCloseRequest?.Invoke() == true) return;
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
        for (int i = 0; i < _secondaryWindows.Count; i++)
            _secondaryWindows[i].RequestLayout();
    }

    private static Keymap CreateDefaultKeymap()
    {
        var km = new Keymap();
        km.Bind(
            action: ActionToggleDevTools,
            chord: new KeyChord(Key: KeyCode.D, Modifiers: Modifiers.Shift)
        );
        km.Bind(action: ActionProfilerCapture, chord: new KeyChord(KeyCode.F7));
        km.Bind(action: ActionFocusNext, chord: new KeyChord(KeyCode.Tab));
        km.Bind(
            action: ActionFocusPrev,
            chord: new KeyChord(Key: KeyCode.Tab, Modifiers: Modifiers.Shift)
        );
        km.Bind(action: ActionDismiss, chord: new KeyChord(KeyCode.Escape));
        return km;
    }

    /// <summary>Request the application to exit after the current frame.</summary>
    public void RequestQuit() => Engine.Quit();

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
        if (!Engine.LoadFont(name: family, path: path)) return false;
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
    ///     Layout request whose visible effect is confined to <paramref name="widget" />'s own
    ///     region (scrolling: children reposition inside the clipped viewport, no size changes).
    ///     Runs the same layout pass — measure caching makes the untouched rest of the tree nearly
    ///     free — but records only the widget's bounds as damage instead of full-clearing the
    ///     frame, so a scroll/fling frame repaints just the scroller. See
    ///     <see cref="Widget.MarkNeedsLayoutClipped" />.
    /// </summary>
    public void RequestLayoutFor(Widget widget)
    {
        _needsLayout = true;
        _semanticsDirty = true; // scrolled content moves its semantic nodes too
        MarkPaintFor(widget);
    }

    /// <summary>
    ///     Thread-safe: request that <paramref name="widget" /> be re-laid-out on the UI thread. For an
    ///     off-thread caller (a timer/async completion setting a signal that a <c>Watch</c>/reactive bind
    ///     reads) — walking the widget's <c>Parent</c> chain off-thread would race the UI thread's tree
    ///     mutation, so the actual <c>MarkNeedsLayout</c> is deferred to the next frame on the UI thread.
    ///     Without it, only the App-level layout flag is set and cached ancestors (ComposedWidget) skip
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
        {
            if (w.Owner is not null) // still attached
                w.MarkNeedsLayout();
        }
    }

    /// <summary>
    ///     Thread-safe: run <paramref name="action" /> on the UI thread at the top of the next frame,
    ///     before layout. The seam for finishing async work that has to touch widgets — an image
    ///     decoded on a worker thread swapping itself in, a fetch completing — without the caller
    ///     hand-rolling a queue per call site. Actions run in enqueue order; one that throws is
    ///     reported and skipped rather than killing the frame loop.
    /// </summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _posted.Enqueue(action);
        // No RequestPaint: the idle gate checks _posted.IsEmpty, and the action marks its own
        // damage — SetTexture calls MarkNeedsLayout, a signal write invalidates its binds.
        // Marking full-frame damage here defeated partial repaint for every background completion.
        // If the UI thread is parked in WaitEvents, wake it now instead of riding out the timeout
        // — an image decode landing mid-idle paints immediately, not one frame-budget later.
        if (Volatile.Read(ref _inWaitEvents)) Engine.WakeEventLoop();
    }

    /// <summary>
    ///     Thread-safe: run <paramref name="action" /> on the UI thread at the <b>end</b> of the next
    ///     frame, after event dispatch, layout and paint. The capture seam for the inspect socket: a
    ///     shot requested after a click must show the click's frame, and a plain <see cref="Post" />
    ///     drains before dispatch, so it captures the previous one.
    /// </summary>
    internal void PostAfterFrame(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _postedAfterFrame.Enqueue(action);
        // No RequestPaint: the drain runs on every frame exit, and WaitEvents' frame-budget timeout
        // bounds the wait. Marking dirty here would make a 30 fps stream full-repaint an idle app
        // continuously — the capture wants to observe frames, not manufacture them.
    }

    // Runs on every exit path of Frame (including the idle early-returns) so a posted capture can
    // never wedge its waiter.
    private void DrainPostedAfterFrame()
    {
        while (_postedAfterFrame.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                DebugLog.Error($"App.PostAfterFrame action threw: {ex}");
            }
        }
    }

    /// <summary>
    ///     Queue a synthetic input event for the current frame's dispatch. UI thread only (use
    ///     <see cref="Post" /> from elsewhere). Injected events run through the exact pipeline OS
    ///     input does — hit-testing, capture, focus, shortcuts — so a remote click is a click.
    /// </summary>
    internal void InjectEvent(InputEvent evt) => _injected.Add(evt);

    /// <summary>
    ///     Render the current UI to a 24-bit BMP at <paramref name="path" /> without disturbing the
    ///     window. Used by <see cref="Host.InspectServer" /> to hand a frame to an out-of-process viewer.
    ///     <para>
    ///         The re-submit is the whole trick, and it is not optional:
    ///         <see cref="ZigoteEngine.CaptureUiBmp" /> renders whatever was submitted <b>last</b>, and a
    ///         submitted list does not survive the frame that consumed it — capture a frame later and the
    ///         result is a black texture. So the last painted list goes back in immediately before the
    ///         capture, which is the same order the smoke-test golden-image path uses.
    ///     </para>
    ///     <para>
    ///         Call between frames (from <see cref="Post" />), never inside one: this submits outside the
    ///         live <c>BeginFrame</c>/<c>EndFrame</c> pair on purpose. The overlay layer (dialogs,
    ///         popovers, menus, snackbars) is composited in, because the capture renders only the main
    ///         submit — what the window shows is what the BMP shows.
    ///     </para>
    /// </summary>
    internal bool CaptureUi(string path, float scale = 1f) =>
        CaptureUi(path: path, scale: scale, density: out _);

    /// <inheritdoc cref="CaptureUi(string,float)" />
    /// <param name="density">
    ///     The scale the frame was actually captured at — <paramref name="scale" /> unless the pixel cap
    ///     lowered it. A viewer divides the image's pixel size by this to get back to layout points, so
    ///     it must be the effective value and not the requested one.
    /// </param>
    internal bool CaptureUi(string path, float scale, out float density)
    {
        // The target is sized in PIXELS while the tree is laid out in POINTS, so a 2× capture needs a
        // 2×-sized texture as well as a 2× paint transform. Passing the point size with scale > 1 does
        // not capture at higher density — it renders the UI twice as large into a same-sized texture,
        // which is a crop of the top-left corner. Capped so a big window at scale 4 cannot ask for a
        // texture past what the backend will allocate (and a shot that large is not a preview).
        density = CaptureDensity(scale);

        if (_paint.Count == 0) return false;
        if (_overlayPaint.Count == 0)
            Engine.SubmitPaintCommands(_paint);
        else
        {
            _capturePaint.Clear();
            _capturePaint.AppendFrom(_paint);
            _capturePaint.AppendFrom(_overlayPaint);
            Engine.SubmitPaintCommands(_capturePaint);
        }

        return Engine.CaptureUiBmp(
            path: path,
            width: (uint)MathF.Max(x: 1f, y: LayoutWidth * density),
            height: (uint)MathF.Max(x: 1f, y: LayoutHeight * density),
            scale: density
        );
    }

    /// <summary>
    ///     The density a capture at <paramref name="scale" /> will actually use — lower only when the
    ///     pixel cap bites. Public to the host so a viewer can be told the density up front, before the
    ///     first frame of a stream exists to measure.
    /// </summary>
    internal float CaptureDensity(float scale) => MathF.Max(
        x: 0.1f,
        y: MathF.Min(
            x: scale,
            y: MaxCapturePixels / MathF.Max(x: 1f, y: MathF.Max(x: LayoutWidth, y: LayoutHeight))
        )
    );

    private void DrainPosted()
    {
        while (_posted.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                DebugLog.Error($"App.Post action threw: {ex}");
            }
        }
    }

    /// <summary>
    ///     Host idle throttle: block until an OS event arrives, cross-thread work lands
    ///     (<see cref="Post" /> wakes the wait), or <paramref name="maxMs" /> elapses. For host
    ///     loops that run at a background cadence — a blind <c>Thread.Sleep</c> there sat on
    ///     completed background work and on the click refocusing the window for up to the whole
    ///     interval.
    /// </summary>
    public void WaitForWork(int maxMs)
    {
        if (maxMs <= 0) return;
        if (!_posted.IsEmpty || !_crossThreadInvalidations.IsEmpty ||
            Reactive.HasPendingDeferred || _background is { FrameIdle: false })
            return;
        Volatile.Write(ref _inWaitEvents, true);
        Engine.WaitEvents(maxMs);
        Volatile.Write(ref _inWaitEvents, false);
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
    ///     a full-frame clear. This is the precise counterpart of <see cref="RequestPaint" /> — a
    ///     control's
    ///     own <see cref="Widget.MarkNeedsPaint" /> (hover glow, slider thumb during a drag, blinking
    ///     caret)
    ///     routes here so a self-repaint on a mouse-move / idle frame stays a sub-rectangle blit instead
    ///     of
    ///     re-clearing the whole scene texture. Safely degrades to a full repaint when the region is
    ///     unknown
    ///     (an unlaid-out widget), <see cref="PartialRepaintEnabled" /> is off, or the widget lives
    ///     outside
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
    public void RequestExtraFrames(int count) =>
        _initialFramesToPaint = Math.Max(val1: _initialFramesToPaint, val2: count);

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
        {
            Console.Error.WriteLine(
                FormattableString.Invariant(
                    $"[dmg] mark {w.GetType().Name} bounds=({w.Bounds.X:F1},{w.Bounds.Y:F1} {w.Bounds.Width:F1}x{w.Bounds.Height:F1}) dmg=({region.X:F1},{region.Y:F1} {region.Width:F1}x{region.Height:F1})"
                )
            );
        }

        if (top == Root) _repaint.AddDamageRoot(region);
        else if (_overlays.Contains(top)) _repaint.AddDamageOverlay(region);
        else _repaint.MarkAll();
    }

    // ── Frame loop ────────────────────────────────────────────────────────────

    public void Frame()
    {
        try
        {
            FrameCore();
        }
        finally
        {
            using (Profiler.Scope("Loop.DrainAfterFrame")) DrainPostedAfterFrame();
        }
    }

    private void FrameCore()
    {
        // Compute dt
        long now = _clock.ElapsedTicks;
        DeltaTime = (float)(now - _lastTicks) / Stopwatch.Frequency;
        _lastTicks = now;

        // dt spans back to the PREVIOUS frame's start, so it includes that frame's idle
        // WaitEvents block — a long dt after deliberately sleeping is not jank. Capture the flag
        // before this frame's gates overwrite it; DebugStats uses it to keep the jank counters
        // honest (they only ever count frames that were actually working).
        bool idledLastFrame = _idledInWait;
        _idledInWait = false;

        // Close the previous frame's profiler scopes into Profiler.LastFrame and open this one.
        // The dt just measured covers exactly the frame EndFrame is closing, which is the pairing
        // DebugStats.Sample's jank attribution relies on — without this bracketing, JankCauses
        // stays empty in every app driven by this loop and the overlay can name a jank count but
        // never a culprit (only the editor's own loop bracketed frames before).
        Profiler.EndFrame();
        Profiler.BeginFrame();

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
        // Hidden means nobody is looking: whatever is animating or repainting is invisible, so the
        // loop drops to a background cadence unconditionally. It keeps turning — an app hides its
        // window precisely because it still has work to do — but at a tenth of the rate and with no
        // rendering at all (see PaintAndPresent).
        if (Hidden)
        {
            _idledInWait = true;
            Volatile.Write(ref _inWaitEvents, true);
            using (Profiler.Scope("WaitEvents"))
                Engine.WaitEvents(HiddenFrameIntervalMs);
            Volatile.Write(ref _inWaitEvents, false);
        }
        else if (!_repaint.AnyDirty && !_needsLayout && !ContinuousUpdate &&
                 !ForceContinuousRender &&
                 !Ticker.AnyActive &&
                 // A background thread that wrote a signal parked its Deferred effects for the drain
                 // below. Sleeping here would sit on that work until some unrelated event woke us.
                 !Reactive.HasPendingDeferred &&
                 // Work posted from another thread must not sit behind an OS wait, and a Background
                 // scope with queued idle results / unfinished slices needs the frame to keep turning.
                 _posted.IsEmpty && (_background?.FrameIdle ?? true) &&
                 !WantsContinuousFrame() && !HotReload.HasPendingReload &&
                 !AnySecondaryWindowWantsFrame())
        {
            _idledInWait = true;
            Volatile.Write(ref _inWaitEvents, true);
            using (Profiler.Scope("WaitEvents"))
                // Time out after one frame budget rather than a hardcoded 16 ms, so the idle
                // wake-up rate follows the monitor (and any app FPS cap) instead of pinning
                // everything to ~60 on a 144 Hz panel.
                Engine.WaitEvents((int)(FrameIntervalTicks * 1000 / Stopwatch.Frequency));
            Volatile.Write(ref _inWaitEvents, false);
        }

        using (Profiler.Scope("Loop.PollEvents")) Engine.PollEventsInto(_events);
        // Move events belonging to secondary OS windows out of the main batch and into each
        // window's own buffer — they are dispatched in that window's SecondaryFrame below.
        RouteEventsToSecondaryWindows();
        // Start queued / complete finished native file dialogs. Runs every frame regardless of
        // repaint activity — WaitEvents' 16 ms timeout above bounds the polling latency.
        FileDialog.Pump();
        var events = _events;

        // Run the effects a background thread parked (EffectAffinity.Deferred). This is the "host
        // calls it once per frame" half of that contract — without it, Deferred effects are queued
        // and never run, so the affinity silently swallows the work it was chosen to protect.
        // Placed here so it is on the UI thread and BEFORE the measure/layout pass: an effect that
        // mutates a retained widget lands in this frame rather than the next. (A signal written by
        // an animation tick — tickers advance after event dispatch below — drains next frame; a
        // UI-thread tick runs its effects synchronously anyway, so this only affects Deferred.)
        Reactive.DrainDeferred();

        // Service the audio engine each frame: ages + reaps fire-and-forget one-shots (UI clicks /
        // positioned pings) so a held oscillator one-shot is silenced after its duration. Cheap no-op
        // until audio is opened (lazy on first sound).
        using (Profiler.Scope("Loop.Audio")) Engine.AudioUpdate(DeltaTime);

        if (_initialFramesToPaint > 0)
        {
            _repaint.MarkAll();
            _initialFramesToPaint--;
        }

        // Sample the shared diagnostics stats once per frame (main window only — a second sampler
        // doubles the frame-time accumulation). Runs unconditionally so an always-on FPS badge and the
        // charts' history rings stay live even before any devtools panel is opened.
        DebugStats.Sample(
            dt: DeltaTime,
            frameBudget: (float)FrameIntervalTicks / Stopwatch.Frequency,
            animating: Ticker.AnyActive,
            idle: idledLastFrame
        );

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
        for (int i = _snackbars.Count - 1; i >= 0; i--)
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
        using (Profiler.Scope("Loop.DrainPosted")) DrainPosted();

        // Deliver Background idle results and advance slices against a per-frame budget, before
        // layout so anything produced here is laid out and painted on this same frame. This is the
        // host-side half of Deliver.WhenIdle / Slice — without it they queue forever (previously
        // only the editor's loop drained its scope).
        if (_background is { } bg)
            using (Profiler.Scope("Loop.Background"))
                bg.RunFrame(TimeSpan.FromMilliseconds(4));

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
        // Synthetic events (InjectEvent) join the real batch here — after DrainPosted, so a command
        // posted this frame dispatches this frame, and inside the same loop as OS input so the
        // discrete/repaint bookkeeping below treats them identically.
        if (_injected.Count > 0)
        {
            events.AddRange(_injected);
            _injected.Clear();
        }

        _pendingRelayout = false;
        bool discrete = false;
        using (Profiler.Scope("UI.Dispatch"))
        {
            foreach (var evt in events)
            {
                DispatchEvent(evt);
                // Pointer-move floods (mouse or finger) and wheel scrolls are not "discrete": they
                // must not force a relayout + full repaint per frame. Scrolling (wheel or touch)
                // repaints via the scroller's own onChanged — bounded to the scroller's region —
                // and presses via MarkPaintFor.
                if (evt is not MouseMoveEvent and not TouchMoveEvent and not ScrollEvent)
                    discrete = true;
            }
        }

        // Advance vsync-driven tickers AFTER event dispatch: a wheel/drag handled this frame moves
        // its scroller's offset in this same frame's tick instead of waiting for the next one —
        // input-to-photon latency drops by a full frame (cf. Chrome's Input Framer: align input
        // with the frame that presents it). Each playing AnimationController calls
        // RequestFrameAction inside Tick(), which requests a relayout (see ctor) so animations
        // that change size/position/rebuilt content are not painted with stale geometry.
        // Hit-testing above used last frame's presented geometry — what the user actually saw.
        using (Profiler.Scope("UI.Tick"))
            Ticker.AdvanceAll(AnimationDt(DeltaTime));

        // A ticker whose Tick lands a visible change marks its own damage (controllers request a
        // relayout, SmoothScroller's onChanged marks its scroller); trusting those marks is what
        // lets a scroll/fling frame keep bounded damage instead of full-clearing the window. A
        // still-active ticker that marked NOTHING gets a conservative full repaint so an
        // animation that repaints from Paint() without marking never freezes.
        // ponytail: a non-marking ticker running while something else marked (caret blink) can
        // stall visually; per-ticker damage tracking if that ever bites.
        if (Ticker.AnyActive && !_repaint.AnyDirty) _repaint.MarkAll();

        // Long-press ripening + fling velocity tracking for an active touch.
        TickTouch(DeltaTime);

        // Anything the platform sent since the last frame — a headset button, audio focus lost,
        // a permission answer. It arrives on whatever thread the OS chose and is replayed here,
        // alongside input, so a channel listener may touch widgets like any other handler.
        // Deliberately before the IsPaused return: a backgrounded app still has to hear from its
        // media session, which is the whole reason it is still running.
        PlatformChannel.Dispatch();

        // Backgrounded: drain events (the poll above must keep running so the foreground event
        // can arrive) but stop all layout/paint/present work — on iOS, GPU work while suspended
        // is a watchdog kill; on Android the surface may already be gone.
        if (IsPaused) return;

        // A handler may have changed structure/size, or a discrete (non-move) interaction
        // occurred — bring layout current once before painting. Per-widget Measure caching
        // makes the common case (nothing actually changed) close to free, and discrete
        // events fire at human rates, so this is cheap. Mouse-moves never reach here.
        if (_needsLayout || _pendingRelayout || discrete)
        {
            LayoutTree();
            // Layout requested via RequestLayout already carries full damage (it MarkAlls); only
            // the discrete-event and mid-frame-resize paths need to force it here. Scroll-driven
            // layout (RequestLayoutFor) keeps its bounded damage — that's the entire point.
            if (discrete || _pendingRelayout) _repaint.MarkAll();
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
    ///     dt handed to animations (<see cref="Ticker.AdvanceAll" />). Present jitter makes
    ///     wall-clock dt oscillate around the refresh interval, and integrating that jitter (scroll
    ///     ease, flings) turns time noise into position noise even when every frame hits its
    ///     deadline. Snap to the nearest whole number of frame intervals (1–3) when within 20% of
    ///     one; real hitches and unpaced loops pass through raw. Timers/stats keep the raw
    ///     <see cref="DeltaTime" /> so measurements stay honest.
    /// </summary>
    private float AnimationDt(float dt) =>
        ComputeAnimationDt(dt: dt, interval: (float)FrameIntervalTicks / Stopwatch.Frequency);

    /// <summary>The snapping arithmetic, split out so it can be exercised without a window.</summary>
    internal static float ComputeAnimationDt(float dt, float interval)
    {
        if (interval <= 0f) return dt;
        float n = MathF.Round(dt / interval);
        if (n is < 1f or > 3f) return dt;
        return MathF.Abs(dt - (n * interval)) < 0.2f * interval ? n * interval : dt;
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
        long now = Environment.TickCount64;
        if (now - _lastPaintExplosionLogMs < 5000) return;
        _lastPaintExplosionLogMs = now;

        Span<int> counts = stackalloc int[32];
        for (int i = 0; i < list.RecordCount; i++)
        {
            int kind = (int)list.RecordKind(i);
            if (kind < counts.Length) counts[kind]++;
        }

        var sb = new StringBuilder(256);
        sb.Append("PAINT EXPLOSION: ").Append(layer).Append(" layer emitted ")
            .Append(list.Count).Append(" commands this frame —");
        for (int k = 0; k < counts.Length; k++)
        {
            if (counts[k] == 0) continue;
            sb.Append(' ').Append((ZgPaintOp)k).Append('=')
                .Append(counts[k]);
        }

        string message = sb.ToString();
        DebugLog.Error(message: message, category: "paint");
        Console.Error.WriteLine(message);
    }

    private void PaintAndPresent()
    {
        // Hidden: there is no surface to present to, and the GPU work would be thrown away. The
        // damage is left dirty on purpose — Show() repaints everything anyway.
        if (Root is null || Hidden) return;

        bool rootWalked = _repaint.RootDirty;
        bool overlayWalked = _repaint.OverlayDirty;
        if (rootWalked || overlayWalked) PaintVersion++;

        bool csdRounded = CsdRounded;
        var windowRect = WindowRect;

        using (Profiler.Scope("UI.Paint"))
        {
            InTreeWalk = true;
            try
            {
                if (_repaint.RootDirty)
                {
                    _paint.Clear();
                    if (csdRounded)
                        _paint.AddClipStart(bounds: windowRect, radius: CsdCornerRadius);
                    PaintChromeBackdrop();
                    Root.Paint(_paint);
                    PaintCsdOutline(csdRounded);
                    if (csdRounded) _paint.AddClipEnd();
                    _repaint.RootPainted();
                }

                if (_repaint.OverlayDirty)
                {
                    _overlayPaint.Clear();
                    if (csdRounded)
                        _overlayPaint.AddClipStart(bounds: windowRect, radius: CsdCornerRadius);
                    foreach (var ov in _overlays) ov.Paint(_overlayPaint);
                    if (csdRounded) _overlayPaint.AddClipEnd();
                    _repaint.OverlayPainted();
                }
            }
            finally
            {
                InTreeWalk = false;
            }
        }

        DrainDeferredOverlayOps();

        // Partial-frame consistency: a re-walked list reflects CURRENT widget state, but replay only
        // touches the damage rects — any op that changed without marking damage this frame (a missed
        // MarkNeedsPaint, an overlay moved by a plain property write) would repaint torn: new inside
        // the rects it overlaps, stale outside. Diff each re-walked list against what was last
        // submitted and widen the damage to cover every changed op; unboundable changes degrade the
        // frame to a full repaint. Runs only on partial frames — full frames are consistent by
        // construction, and clean layers re-submit their previous (on-screen) list verbatim.
        if (PartialRepaintEnabled && !ContinuousUpdate && !ForceContinuousRender)
        {
            using var _ = Profiler.Scope("UI.Damage");
            if (rootWalked && _repaint.DamageCount > 0)
            {
                WidenDamageFromPaintDiff(
                    snapshot: _rootSnapshot,
                    current: _paint,
                    isOverlay: false
                );
            }

            if (overlayWalked && _repaint.DamageCount > 0)
            {
                WidenDamageFromPaintDiff(
                    snapshot: _overlaySnapshot,
                    current: _overlayPaint,
                    isOverlay: true
                );
            }

            if (rootWalked) _rootSnapshot.Capture(_paint);
            if (overlayWalked) _overlaySnapshot.Capture(_overlayPaint);
        }

        DebugStats.UiPaintCommands = _paint.Count;
        DebugStats.OverlayPaintCommands = _overlayPaint.Count;
        WarnIfPaintExplosion(list: _paint, layer: "root");
        WarnIfPaintExplosion(list: _overlayPaint, layer: "overlay");

        using (Profiler.Scope("Render.Submit"))
        {
            using (Profiler.Scope("Render.Begin")) Engine.BeginFrame(DeltaTime);

            using (Profiler.Scope("Render.Submit2D")) Engine.SubmitPaintCommands(_paint);

            if (_overlayPaint.Count > 0)
            {
                using (Profiler.Scope("Render.SubmitOverlay"))
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
                        $"[dmg] FULL root={_repaint.RootDirty} overlay={_repaint.OverlayDirty} rootOps={_paint.Count} ovOps={_overlayPaint.Count}"
                    );
                }
                else
                {
                    var sb = new StringBuilder("[dmg] PARTIAL ");
                    foreach (var r in _repaint.Damage)
                    {
                        sb.Append(
                            FormattableString.Invariant(
                                $"({r.X:F1},{r.Y:F1} {r.Width:F1}x{r.Height:F1}) "
                            )
                        );
                    }

                    sb.Append(
                        FormattableString.Invariant(
                            $"root={_repaint.RootDirty} overlay={_repaint.OverlayDirty} rootOps={_paint.Count} ovOps={_overlayPaint.Count}"
                        )
                    );
                    Console.Error.WriteLine(sb.ToString());
                }
            }

            // The bulk of a continuous editor/game frame: the full 3D scene render (shadow → G-buffer →
            // SSAO → SSR → bloom → tonemap → TAA → overlay composite) plus the swapchain present.
            // FrameEnd = render + end-of-frame in one FFI transition (zigote_frame_end delegates to
            // the same pair the old two calls hit).
            using (Profiler.Scope("Render.GPU")) Engine.FrameEnd();

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
        switch (snapshot.Diff(current: current, changed: changed, changedCount: out int count))
        {
            case PaintDiffResult.Identical:
                return;
            case PaintDiffResult.Bounded:
                for (int i = 0; i < count; i++)
                    _repaint.AddDamageBoundsOnly(changed[i].Inflate(DamageMargin));
                if (DebugDamageLog)
                {
                    Console.Error.WriteLine(
                        FormattableString.Invariant(
                            $"[dmg] diff {(isOverlay ? "overlay" : "root")} widened by {count} rect(s), first=({changed[0].X:F1},{changed[0].Y:F1} {changed[0].Width:F1}x{changed[0].Height:F1})"
                        )
                    );
                }

                return;
            case PaintDiffResult.Unbounded:
                _repaint.ForceFullDamage();
                if (DebugDamageLog)
                {
                    Console.Error.WriteLine(
                        $"[dmg] diff {(isOverlay ? "overlay" : "root")} UNBOUNDED -> full"
                    );
                }

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
                for (int i = 0; i < _secondaryWindows.Count; i++)
                {
                    if (_secondaryWindows[i].WindowId == windowId)
                    {
                        _secondaryWindows[i].LiveResizeSecondary();
                        break;
                    }
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

    // ── Event dispatch ────────────────────────────────────────────────────────

    private void DispatchEvent(InputEvent evt)
    {
        switch (evt)
        {
            case DisplayChangedEvent:
                // Moved to another monitor (or that monitor's mode/scale changed). The engine has
                // already refreshed the cached scale + refresh rate, so FrameIntervalTicks now
                // reflects the new panel. Relayout because HostScale feeds every logical→physical
                // conversion, and repaint because nothing else wakes the loop after a drag between
                // screens. Resync the pacer so the first frame at the new rate isn't slept against
                // the old one's anchor.
                _paceAnchorTicks = 0;
                RequestLayout();
                _repaint.MarkAll();
                return;

            case WindowFocusEvent wf:
                // App/host state only — never routed to widgets (widget focus is unrelated). Hosts read
                // WindowFocused to throttle background frame rates; repaint once on regain so anything
                // that went stale while throttled refreshes immediately.
                WindowFocused = wf.Focused;

                // A captured pointer must never survive losing focus: the OS has hidden and pinned the
                // cursor, and if the window goes to the background holding it, the user has no cursor
                // and no obvious way to get it back. Alt-tab therefore always releases.
                if (!wf.Focused && Engine.RelativeMouseMode) Engine.SetRelativeMouseMode(false);

                // Losing focus mid-drag means the release will never arrive: the pointer left the
                // window (or the user alt-tabbed) and the button came up somewhere we hear nothing
                // about. Without this the capture survives, and a slider or scrollbar keeps
                // tracking the cursor the next time it wanders back over the window — the control
                // appears to be "stuck" to the pointer with no button held.
                if (!wf.Focused && _capturedWidget is not null)
                {
                    var dragging = _capturedWidget;
                    _capturedWidget = null;
                    dragging.OnPointerCancel();
                }

                if (wf.Focused) _repaint.MarkAll();
                // Focus is the desktop face of Resumed↔Inactive (never Paused — that's the
                // mobile suspend pair). Secondary windows don't drive app-level lifecycle.
                if (ParentApp is null)
                {
                    switch (wf.Focused)
                    {
                        case false when LifecycleState == AppLifecycleState.Resumed:
                            SetLifecycleState(AppLifecycleState.Inactive);
                            break;
                        case true when LifecycleState == AppLifecycleState.Inactive:
                            SetLifecycleState(AppLifecycleState.Resumed);
                            break;
                    }
                }

                break;

            case TouchEvent te:
                DispatchTouchEvent(te);
                break;

            case AppBackgroundEvent or AppForegroundEvent or LowMemoryEvent
                or ScreenKeyboardEvent:
                HandleAppLifecycleEvent(evt);
                break;

            case MouseMoveEvent m:
                PointerIsTouchFlag = false; // a real cursor is back in charge of hit-target sizing

                // Pointer captured (mouselook): the cursor is pinned and hidden, so m.X/m.Y no longer
                // describe anything. Hit-testing and hover are meaningless here — the delta goes
                // straight to whoever holds focus, and nothing else about the frame changes.
                if (Engine.RelativeMouseMode)
                {
                    var target = _capturedWidget ?? FocusedWidget;
                    if (target is not null && (m.RelativeX != 0f || m.RelativeY != 0f))
                    {
                        target.OnPointerRelative(deltaX: m.RelativeX, deltaY: m.RelativeY);
                        MarkPaintFor(target);
                    }

                    break;
                }

                _mousePos = new Offset(x: m.X, y: m.Y);
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
                PointerIsTouchFlag =
                    false; // a click can arrive without a preceding move (tablet, remote)
                var point = new Offset(x: d.X, y: d.Y);

                // Captured: there is no cursor to hit-test with. The reported position is frozen
                // wherever it was when capture began — often not even over the widget that asked for
                // capture — so every button must go to the focused widget, the one already receiving
                // the motion.
                if (Engine.RelativeMouseMode)
                {
                    _capturedWidget = FocusedWidget;
                    _capturedWidget?.OnPointerDown(point);
                    break;
                }

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
                var point = new Offset(x: u.X, y: u.Y);
                _capturedWidget?.OnPointerUp(point);
                _capturedWidget = null;
                // Drag ended — re-resolve from whatever is now under the pointer.
                ResolveAndApplyCursor();
                break;
            }

            case MouseDownEvent { Button: MouseButton.Right } r:
            {
                var point = new Offset(x: r.X, y: r.Y);
                if (Engine.RelativeMouseMode)
                {
                    _rightCapturedWidget = FocusedWidget;
                    _rightCapturedWidget?.OnRightClick(point);
                    break;
                }

                var hit = HitTestAll(point);
                _rightCapturedWidget = hit;
                hit?.OnRightClick(point);
                break;
            }

            case MouseUpEvent { Button: MouseButton.Right } ru:
            {
                var point = new Offset(x: ru.X, y: ru.Y);
                _rightCapturedWidget?.OnRightPointerUp(point);
                _rightCapturedWidget = null;
                break;
            }

            case ScrollEvent s:
            {
                float dx = s.ScrollX;
                float dy = s.ScrollY;
                // Native normalizes flipped/natural wheel events to a canonical orientation; re-apply the
                // host's natural-scroll preference so it isn't silently discarded.
                if (HonorHostScrollOrientation &&
                    Engine.GetScrollOrientation() == ScrollOrientation.Flipped)
                {
                    dx = -dx;
                    dy = -dy;
                }

                var target = Engine.RelativeMouseMode ? FocusedWidget : HitTestAll(_mousePos);
                if (DebugScrollLog)
                {
                    Console.Error.WriteLine(
                        FormattableString.Invariant(
                            $"[scroll] raw=({s.ScrollX:F3},{s.ScrollY:F3}) dispatched=({dx:F3},{dy:F3}) orient={Engine.GetScrollOrientation()} target={target?.GetType().Name ?? "null"} mouse=({_mousePos.X:F0},{_mousePos.Y:F0})"
                        )
                    );
                }

                target?.OnScroll(dx: dx, dy: dy);
                break;
            }

            case KeyEvent k:
                CurrentModifiers = k.Modifiers;
                CurrentKeyRepeat = k.Repeat;

                if (k.Down)
                {
                    // The system back action (Android's back gesture/button, trapped by the
                    // engine so it reaches us instead of finishing the activity). Handled before
                    // focus-directed keys: back is a navigation command, not text input.
                    if (!k.Repeat && k.Key == KeyCode.AcBack)
                    {
                        if (HandleSystemBack()) break;
                        // Nothing left to go back to — let the platform close the app, which is
                        // what a user pressing back on the first screen expects.
                        RequestQuit();
                        break;
                    }

                    string? action = Keymap.Resolve(key: k.Key, modifiers: k.Modifiers);

                    // Global toggles — fire once per physical press (ignore OS auto-repeat), any focus.
                    if (!k.Repeat && action == ActionToggleDevTools)
                    {
                        OnToggleDevTools?.Invoke();
                        break;
                    }

                    if (!k.Repeat && action == ActionProfilerCapture)
                    {
                        Profiler.Capture(frames: 120, outputPath: "profile_capture.json");
                        break;
                    }

                    // App-defined shortcuts (see OnShortcut), ahead of the focus-scoped ones so an app
                    // can own a chord the framework has no meaning for. A modifier-less chord is
                    // withheld while a text editor holds focus — Space in a search box is typing,
                    // not play/pause.
                    // ponytail: blocks every unmodified chord (F-keys too) while typing; refine per
                    // key if an app wants F9 mid-search.
                    if (!k.Repeat && action is not null && OnShortcut is { } onShortcut &&
                        action is not (ActionFocusNext or ActionFocusPrev or ActionDismiss) &&
                        ShortcutOutranksFocus(modifiers: k.Modifiers, focused: FocusedWidget) &&
                        onShortcut(action))
                        break;

                    // Menu-bar accelerators, after the app's own handler so an explicit binding wins.
                    if (!k.Repeat && Accelerators.Count > 0 &&
                        ShortcutOutranksFocus(modifiers: k.Modifiers, focused: FocusedWidget) &&
                        RunAccelerator(key: k.Key, modifiers: k.Modifiers))
                        break;

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
                            bool handled = k.Key switch {
                                KeyCode.Right => MoveFocusDirectional(dirX: 1f, dirY: 0f),
                                KeyCode.Left => MoveFocusDirectional(dirX: -1f, dirY: 0f),
                                KeyCode.Down => MoveFocusDirectional(dirX: 0f, dirY: 1f),
                                KeyCode.Up => MoveFocusDirectional(dirX: 0f, dirY: -1f),
                                _ => false,
                            };
                            if (handled) break;
                        }
                    }
                }

                FocusedWidget?.OnKey(
                    keyChar: k.KeyChar,
                    scancode: k.Scancode,
                    down: k.Down,
                    mods: k.Modifiers
                );
                break;

            case TextInputEvent ti:
                if (FocusedWidget is ITextInputClient && !string.IsNullOrEmpty(ti.Text))
                    UiFeedback.Type?.Invoke();
                FocusedWidget?.OnTextInput(ti.Text);
                break;

            case TextCompositionEvent composition:
                FocusedWidget?.OnTextComposition(
                    text: composition.Text,
                    selectionStart: composition.SelectionStart,
                    selectionLength: composition.SelectionLength
                );
                break;

            case ResizeEvent:
                // Secondary windows track their own surface size (the engine's cached size is the
                // main window's); refresh before the relayout so MediaQuery sees the new bounds.
                NativeWindow?.RefreshSize();
                _pendingRelayout = true;
                // Rotation moves the notch/home indicator — re-query the safe area with the
                // new geometry (LayoutTree refreshes it lazily).
                _safeAreaValid = false;
                // macOS drops the unified-titlebar styleMask bit on fullscreen/zoom round-trips
                // — re-assert it whenever the window geometry changes (no-op when intact).
                if (ChromeStyle == WindowChromeStyle.MacUnified)
                    Engine.WindowChromeSync(WindowId);
                break;

            case WindowCloseEvent:
                // Titlebar ✕ — quit for the main window, close for a secondary one. Events are
                // routed per window, so this instance is always the one being closed.
                if (ParentApp is null)
                {
                    // An app that wants to keep running answers here; everything else quits.
                    if (OnCloseRequest?.Invoke() != true) RequestQuit();
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
        for (int i = _overlays.Count - 1; i >= 0; i--)
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
