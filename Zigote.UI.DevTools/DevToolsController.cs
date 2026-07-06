using Zigote.UI.Widgets;

using Zigote.UI.Host;
namespace Zigote.UI.DevTools;

/// <summary>
///     Per-app state and orchestration for the devtools overlay: the panel registry, the active
///     category + per-category panel selection, the on-screen inspector flags, and the open/compact
///     state. It owns the two overlay widgets (the passive <see cref="DevOverlayLayer" /> and the docked
///     <see cref="DevToolsPanel" />) and drives their per-frame refresh from <see cref="Tick" />.
///     One is created per host by <see cref="DevTools.Install" />.
/// </summary>
public sealed class DevToolsController
{
    private readonly Dictionary<IDevPanel, Widget> _cache = new();
    private readonly List<IDevPanel> _panels = [];
    private readonly int[] _selected = new int[3];
    private DevOverlayLayer? _layer;
    private DevToolsPanel? _panel;

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
    public bool WantsContinuousFrame => PanelOpen || CompactVisible;

    // ── On-screen inspector flags (read by DevOverlayLayer, driven by the UI Inspector panel) ──
    public bool ShowRepaintRainbow { get; set; }
    public bool ShowLayoutBounds { get; set; }
    public bool ShowOverflow { get; set; } = true;
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

    /// <summary>Select a widget and notify the inspector tree to reveal it (expand + scroll to it).</summary>
    public void SelectWidget(Widget? widget)
    {
        SelectedWidget = widget;
        SelectionRevision++;
    }

    /// <summary>Demo/smoke aid: when set, the panel auto-advances through every tab (env-gated in Install).</summary>
    public bool AutoCycle { get; set; }

    private float _cycleTimer;

    public IReadOnlyList<IDevPanel> Panels => _panels;
    public DevOverlayLayer Layer => _layer ??= new DevOverlayLayer(this);

    internal void AttachPanel(DevToolsPanel panel)
    {
        _panel = panel;
    }

    public void Register(IDevPanel panel)
    {
        _panels.Add(panel);
    }

    /// <summary>The category tabs to show for the current (resolved) profile.</summary>
    public List<DevCategory> VisibleCategories()
    {
        var cats = new List<DevCategory> { DevCategory.Generic, DevCategory.Ui2D };
        if (Profile.ShowsRender3D()) cats.Add(DevCategory.Render3D);
        return cats;
    }

    public List<IDevPanel> PanelsIn(DevCategory category)
    {
        var list = new List<IDevPanel>();
        foreach (var p in _panels)
            if (p.Category == category && p.IsAvailable)
                list.Add(p);
        return list;
    }

    public int SelectedIndex(DevCategory category)
    {
        var count = PanelsIn(category).Count;
        return count == 0 ? 0 : Math.Clamp(_selected[(int)category], 0, count - 1);
    }

    public IDevPanel? ActivePanel
    {
        get
        {
            var panels = PanelsIn(Category);
            return panels.Count == 0 ? null : panels[SelectedIndex(Category)];
        }
    }

    public void SetCategory(DevCategory category)
    {
        Category = category;
    }

    public void SetSelected(DevCategory category, int index)
    {
        _selected[(int)category] = index;
    }

    /// <summary>Build-and-cache a panel's retained widget tree so its state survives panel switches.</summary>
    public Widget WidgetFor(IDevPanel panel, BuildContext context)
    {
        return _cache.TryGetValue(panel, out var w) ? w : _cache[panel] = panel.Build(context);
    }

    /// <summary>True once a panel's widget tree has been built (so <see cref="IDevPanel.Refresh" /> is safe).</summary>
    private bool IsBuilt(IDevPanel panel)
    {
        return _cache.ContainsKey(panel);
    }

    // ── Toggles (wired to App.OnToggleDevTools / OnToggleDevCompact / Shift+D) ──

    public void TogglePanel()
    {
        if (_panel is null) return;
        PanelOpen = !PanelOpen;
        if (PanelOpen)
        {
            App.PushOverlay(_panel);
        }
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
        _layer?.Tick(dt, App.Root);
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

    // Walk every (category, panel) pair on a timer so a smoke run visits — and thus builds, refreshes
    // and paints — the whole overlay without any input.
    private void AdvanceCycle(float dt)
    {
        _cycleTimer += dt;
        if (_cycleTimer < 0.6f) return;
        _cycleTimer = 0f;

        var cats = VisibleCategories();
        var panels = PanelsIn(Category);
        var next = SelectedIndex(Category) + 1;
        if (next < panels.Count)
        {
            SetSelected(Category, next);
        }
        else
        {
            SetSelected(Category, 0);
            SetCategory(cats[(cats.IndexOf(Category) + 1) % cats.Count]);
        }

        _panel?.MarkNeedsBuild();
    }
}
