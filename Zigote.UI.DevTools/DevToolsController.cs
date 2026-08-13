using Zigote.Core;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Host;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools;

/// <summary>
///     Per-app state and orchestration for the devtools overlay: the panel registry, the active
///     category + per-category panel selection, the on-screen inspector flags, and the open/compact
///     state. It owns the two overlay widgets (the passive <see cref="DevOverlayLayer" /> and the
///     docked
///     <see cref="DevToolsPanel" />) and drives their per-frame refresh from <see cref="Tick" />.
///     One is created per host by <see cref="DevTools.Install" />.
/// </summary>
public sealed class DevToolsController
{
    // ── Presentation: docked width, fullscreen, torn-off window ──

    /// <summary>Narrowest useful dock: below this the panel strip and readouts stop being legible.</summary>
    public const float MinDockWidth = 320f;

    private readonly Dictionary<IDevPanel, Widget> _cache = new();
    private readonly List<IDevPanel> _panels = [];
    private readonly int[] _selected = new int[3];

    private float _cycleTimer;

    private float _dockWidth = DevToolsPanel.PanelWidth;
    private DevOverlayLayer? _layer;
    private DevToolsPanel? _panel;
    private App? _window;
    private ThemeProvider? _windowTheme;
    private DevToolsView? _windowView;

    public DevToolsController(App app, DevToolsProfile profile)
    {
        App = app;
        Profile = profile;
    }

    public App App { get; }
    public DevToolsProfile Profile { get; set; }

    public DevCategory Category { get; private set; } = DevCategory.Generic;
    public bool PanelOpen { get; private set; }
    public bool CompactVisible { get; private set; }

    public bool WantsContinuousFrame =>
        PanelOpen || CompactVisible || WindowOpen || AnyDebugDraw;

    // ── On-screen inspector flags (read by DevOverlayLayer, driven by the UI Inspector panel) ──
    public bool ShowRepaintRainbow { get; set; }
    public bool ShowLayoutBounds { get; set; }
    public bool ShowOverflow { get; set; }
    public Widget? SelectedWidget { get; set; }

    /// <summary>
    ///     "select widget mode": while on, the overlay layer captures clicks over the app
    ///     area, hover previews the widget under the pointer, and a click selects it in the inspector
    ///     tree. Esc (or the panel's inspect button) exits.
    /// </summary>
    public bool InspectMode { get; set; }

    /// <summary>Transient on-screen highlight: inspect-mode hover, or a hovered inspector tree row.</summary>
    public Widget? HoverHighlight { get; set; }

    /// <summary>Bumped by <see cref="SelectWidget" /> so the inspector panel reveals the selection.</summary>
    public int SelectionRevision { get; private set; }

    /// <summary>
    ///     Demo/smoke aid: when set, the panel auto-advances through every tab (env-gated in
    ///     Install).
    /// </summary>
    public bool AutoCycle { get; set; }

    /// <summary>
    ///     Screen width the open panel covers on the right — the docked column, or the whole screen on a
    ///     phone. Zero while closed. Overlay chrome offsets itself by this so it stays visible.
    /// </summary>
    public float PanelInsetRight =>
        PanelOpen ? _panel?.VisibleWidth ?? DevToolsPanel.PanelWidth : 0f;

    /// <summary>
    ///     Width of the docked column, driven by <see cref="DevResizeHandle" />. Clamped to
    ///     <see cref="MinDockWidth" /> and to leaving a strip of the app visible, so a drag can never
    ///     resize the panel into something unusable or hide the app behind it.
    /// </summary>
    public float DockWidth
    {
        // Clamped on read as well as on write: shrinking the host window must not leave a dock
        // wider than the window it is docked to.
        get => Math.Clamp(value: _dockWidth, min: MinDockWidth, max: MaxDockWidth);
        set
        {
            float clamped = Math.Clamp(value: value, min: MinDockWidth, max: MaxDockWidth);
            if (MathF.Abs(clamped - _dockWidth) < 0.5f) return;
            _dockWidth = clamped;
            _panel?.MarkNeedsBuild();
        }
    }

    /// <summary>Widest dock that still leaves a usable strip of the app visible beside it.</summary>
    private float MaxDockWidth
    {
        get
        {
            float host = App.HostLogicalWidth;
            return MathF.Max(x: MinDockWidth, y: (host > 0f ? host : 1280f) - 120f);
        }
    }

    /// <summary>The docked column expanded to cover the host window. Always on at phone width.</summary>
    public bool Fullscreen { get; private set; }

    /// <summary>True while the devtools live in their own OS window.</summary>
    public bool WindowOpen => _window is { IsOpen: true };

    public IReadOnlyList<IDevPanel> Panels => _panels;
    public DevOverlayLayer Layer => _layer ??= new DevOverlayLayer(this);

    public IDevPanel? ActivePanel
    {
        get
        {
            var panels = PanelsIn(Category);
            return panels.Count == 0 ? null : panels[SelectedIndex(Category)];
        }
    }

    /// <summary>
    ///     True while the panels are mounted somewhere — docked/fullscreen overlay or torn-off window.
    ///     Select-widget mode keys off this: the layer may only swallow clicks while there is a panel
    ///     to show the pick in.
    /// </summary>
    public bool PanelsMounted => PanelOpen || WindowOpen;

    /// <summary>An on-screen debug draw is switched on and should paint.</summary>
    public bool AnyDebugDraw => ShowRepaintRainbow || ShowLayoutBounds || ShowOverflow;

    /// <summary>
    ///     True while the overlay should paint its debug layers. Not just while the panels are mounted:
    ///     a debug draw the user switched on stays on-screen after the panel is closed — that is the
    ///     point of a full-screen overlay you enable and then get out of the way of.
    /// </summary>
    public bool DebugDrawActive => PanelsMounted || AnyDebugDraw;

    /// <summary>Select a widget and notify the inspector tree to reveal it (expand + scroll to it).</summary>
    public void SelectWidget(Widget? widget)
    {
        SelectedWidget = widget;
        SelectionRevision++;
    }

    public void ToggleFullscreen()
    {
        Fullscreen = !Fullscreen;
        _panel?.MarkNeedsBuild();
        Layer.MarkNeedsPaint();
    }

    /// <summary>
    ///     Tear the devtools off into their own OS window (raising it if already open). The panels'
    ///     retained widget trees can only be mounted in one tree at a time, so this closes the in-app
    ///     overlay and rebuilds the panels in the new window.
    /// </summary>
    public void OpenWindow()
    {
        if (WindowOpen)
        {
            _window!.NativeWindow?.Raise();
            return;
        }

        SetPanelOpen(false);
        _cache.Clear();

        var win = App.CreateWindow(title: "DevTools", width: 520, height: 860);
        win.Theme = App.Theme;
        // Chrome inherited from the host: under AdwaitaCsd the view's own header is an AdwHeaderBar
        // (drag surface + window buttons on the system's button-layout side), so an injected strip
        // above it would be a second titlebar that does not match the host window's.
        _windowView = new DevToolsView(controller: this, chrome: DevToolsChrome.Window);
        Widget content = _windowTheme = new ThemeProvider(data: win.Theme, child: _windowView);
        // Unified chrome hides the titlebar: pad below the native buttons so the devtools header
        // does not collide with them (same treatment the editor's Settings window gets).
        if (win.TitleBarTopInset > 0f)
        {
            content = new Padding(
                padding: EdgeInsets.Only(top: win.TitleBarTopInset),
                child: content
            );
        }

        win.Root = content;
        // The devtools are a live instrument: the window renders every frame while it exists.
        win.AddContinuousFrameSource(() => true);
        // Parity with the docked panel, which lives in the host's overlay layer and is therefore
        // repainted by the host's continuous-frame path. A secondary window's continuous source only
        // marks its OVERLAY layer, and the view here is its Root — so without a per-frame
        // RequestLayout (which marks both layers) live readouts freeze and a resize repaints stale
        // content. The window's own FrameTick drives it, so it keeps running at the window's cadence
        // even while the host is idle.
        win.FrameTick += WindowTick;
        // Shift+D inside the devtools window docks it back, rather than doing nothing.
        win.OnToggleDevTools = TogglePanel;
        win.OnToggleDevCompact = ToggleCompact;
        win.CloseRequested += () =>
        {
            _window = null;
            _windowTheme = null;
            _windowView = null;
            _cache.Clear();
        };
        _window = win;
    }

    /// <summary>Close the torn-off window and bring the devtools back into the host as a docked panel.</summary>
    public void DockWindow()
    {
        _window?.RequestClose();
        _window = null;
        _windowTheme = null;
        _windowView = null;
        _cache.Clear();
        SetPanelOpen(true);
    }

    internal void AttachPanel(DevToolsPanel panel) => _panel = panel;

    public void Register(IDevPanel panel) => _panels.Add(panel);

    /// <summary>The category tabs to show for the current (resolved) profile.</summary>
    public List<DevCategory> VisibleCategories()
    {
        var cats = new List<DevCategory> {
            DevCategory.Generic,
            DevCategory.Ui2D,
        };
        if (Profile.ShowsRender3D()) cats.Add(DevCategory.Render3D);
        return cats;
    }

    public List<IDevPanel> PanelsIn(DevCategory category)
    {
        var list = new List<IDevPanel>();
        foreach (var p in _panels)
        {
            if (p.Category == category && p.IsAvailable)
                list.Add(p);
        }

        return list;
    }

    public int SelectedIndex(DevCategory category)
    {
        int count = PanelsIn(category).Count;
        return count == 0 ? 0 : Math.Clamp(value: _selected[(int)category], min: 0, max: count - 1);
    }

    public void SetCategory(DevCategory category) => Category = category;

    public void SetSelected(DevCategory category, int index) => _selected[(int)category] = index;

    /// <summary>Build-and-cache a panel's retained widget tree so its state survives panel switches.</summary>
    public Widget WidgetFor(IDevPanel panel, BuildContext context)
    {
        // Grouped once, on the way into the cache: panels build flat lists of rows and DevPage lays
        // them out as Adwaita boxed lists (see DevPage).
        return _cache.TryGetValue(key: panel, value: out var w)
            ? w
            : _cache[panel] = DevPage.Group(panel.Build(context));
    }

    /// <summary>
    ///     True once a panel's widget tree has been built (so <see cref="IDevPanel.Refresh" /> is
    ///     safe).
    /// </summary>
    private bool IsBuilt(IDevPanel panel) => _cache.ContainsKey(panel);

    // ── Toggles (wired to App.OnToggleDevTools / OnToggleDevCompact / Shift+D) ──

    public void TogglePanel()
    {
        if (_panel is null) return;
        // The torn-off window owns the panels while it is up; the toggle brings them back in.
        if (WindowOpen && !PanelOpen)
        {
            DockWindow();
            return;
        }

        PanelOpen = !PanelOpen;
        if (PanelOpen)
            App.PushOverlay(_panel);
        else
        {
            App.PopOverlay(_panel);
            InspectMode = false;
            HoverHighlight = null;
        }

        Layer.MarkNeedsPaint();
    }

    public void SetPanelOpen(bool open)
    {
        if (open == PanelOpen) return;
        TogglePanel();
    }

    public void ToggleCompact()
    {
        CompactVisible = !CompactVisible;
        Layer.MarkNeedsPaint();
    }

    // ── Per-frame (App.FrameTick) ──

    public void Tick(float dt)
    {
        _layer?.Tick(dt: dt, root: App.Root);
        // The on-screen debug draws stay live whichever host owns the panels — and after they are all
        // closed, for as long as a draw is switched on.
        if (WindowOpen || AnyDebugDraw) _layer?.MarkNeedsPaint();
        if (!PanelOpen) return;
        if (AutoCycle) AdvanceCycle(dt);
        // Only refresh a panel whose widget tree has actually been built — the FrameTick fires before
        // the layout pass that first calls Build (WidgetFor), so on the frame a panel opens its
        // Build-created fields are not ready yet.
        if (ActivePanel is { } active && IsBuilt(active)) active.Refresh(dt);
        // Force the (small) panel subtree to relayout each frame so live meters/labels/charts advance
        // even when nothing structural changed — the panel is explicitly a continuous-while-open tool.
        _panel?.MarkNeedsLayout();
        _layer?.MarkNeedsPaint();
    }

    /// <summary>Per-frame work for the torn-off window (its own <see cref="App.FrameTick" />).</summary>
    private void WindowTick(float dt)
    {
        if (_window is not { IsOpen: true } win) return;

        // The host owns the theme; follow it so a theme switch does not leave the window behind.
        if (_windowTheme is { } scope && !ReferenceEquals(objA: scope.Data, objB: App.Theme))
        {
            win.Theme = App.Theme;
            scope.Data = App.Theme;
        }

        if (AutoCycle) AdvanceCycle(dt);
        if (ActivePanel is { } active && IsBuilt(active)) active.Refresh(dt);
        win.RequestLayout();
    }

    // Walk every (category, panel) pair on a timer so a smoke run visits — and thus builds, refreshes
    // and paints — the whole overlay without any input.
    private void AdvanceCycle(float dt)
    {
        _cycleTimer += dt;
        if (_cycleTimer < 0.6f) return;
        _cycleTimer = 0f;

        var cats = VisibleCategories();
        var panels = PanelsIn(Category);
        int next = SelectedIndex(Category) + 1;
        if (next < panels.Count)
            SetSelected(category: Category, index: next);
        else
        {
            SetSelected(category: Category, index: 0);
            SetCategory(cats[(cats.IndexOf(Category) + 1) % cats.Count]);
        }

        _panel?.MarkNeedsBuild();
        _windowView?.MarkNeedsBuild();
    }
}
