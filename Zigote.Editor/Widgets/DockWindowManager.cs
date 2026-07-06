using Zigote.Core;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Widgets;

/// <summary>
///     Cross-window panel docking: tear a dock tab out of a window (release it outside every dock
///     region) to float it in its own OS window with its own <see cref="DockLayout" />, drag tabs
///     between windows (SDL keeps the pointer captured by the source window during a drag, so this
///     manager converts window-local drag points to global desktop coordinates and hit-tests every
///     other editor window), and return panels to the main dock when a floating window closes.
///     The 3D viewport is pinned to the main window — its render pass draws there.
/// </summary>
public sealed class DockWindowManager(App app)
{
    private readonly List<Entry> _floats = [];

    private DockLayout? _mainDock;
    private ThemeData _theme = ThemeData.Dark;

    /// <summary>Panels that must never leave the main window (native passes bound to it).</summary>
    public Func<string, bool> CanTearOut { get; set; } = id => id != "viewport";

    /// <summary>Panel ids currently living in floating windows.</summary>
    public IEnumerable<string> FloatingPanelIds =>
        _floats.SelectMany(f => f.Dock.Panels.Select(p => p.PanelId));

    /// <summary>
    ///     Bind the manager to the current editor shell (called on project open and every shell
    ///     rebuild): the main dock becomes the tear-out source/target and the return destination.
    /// </summary>
    public void SetMain(DockLayout dock, ThemeData theme)
    {
        _mainDock = dock;
        _theme = theme;
        Wire(app, dock);
    }

    /// <summary>
    ///     Close every floating window WITHOUT returning panels (shell rebuild: the new
    ///     EditorLayout constructs fresh panel instances). Returns the ids that were floating so
    ///     the caller can re-open them in the new main dock.
    /// </summary>
    public IReadOnlyList<string> CloseAllForRebuild()
    {
        var ids = FloatingPanelIds.ToList();
        foreach (var f in _floats.ToList()) DestroyFloat(f);
        return ids;
    }

    private void Wire(App win, DockLayout dock)
    {
        dock.TabDragMoved = (id, local) => OnDragMoved(win, id, local);
        dock.TabDragReleased = (id, local) => OnDragReleased(
            win,
            dock,
            id,
            local
        );
    }

    // ── Drag tracking ──────────────────────────────────────────────────────────

    private void OnDragMoved(App sourceWin, string panelId, Offset local)
    {
        if (!CanTearOut(panelId))
        {
            ClearHovers();
            return;
        }

        var global = ToGlobal(sourceWin, local);
        var target = FindDockAt(global, sourceWin);
        foreach (var (win, dock) in AllDocks())
        {
            if (win == sourceWin) continue; // its own internal preview handles in-window feedback
            dock.SetExternalDropHover(target?.Win == win ? ToLocal(win, global) : null);
        }
    }

    private void OnDragReleased(App sourceWin, DockLayout sourceDock, string panelId, Offset local)
    {
        ClearHovers();
        if (!CanTearOut(panelId)) return;

        var global = ToGlobal(sourceWin, local);

        var target = FindDockAt(global, sourceWin);
        if (target is { } t)
        {
            MovePanel(
                sourceWin,
                sourceDock,
                panelId,
                t.Dock,
                ToLocal(t.Win, global)
            );
            return;
        }

        // Released inside the source window but on no drop zone → plain drag cancel.
        if (WindowRect(sourceWin).Contains(global.X, global.Y)) return;

        TearOut(
            sourceWin,
            sourceDock,
            panelId,
            global
        );
    }

    // ── Moves ──────────────────────────────────────────────────────────────────

    private void MovePanel(App sourceWin, DockLayout sourceDock, string panelId,
        DockLayout targetDock, Offset dropPoint)
    {
        // A floating window losing its last panel dies with it; the main dock keeps its last tab.
        var lastTab = sourceDock.OpenPanelIds.Count() <= 1;
        if (sourceWin == app && lastTab) return;

        var panel = sourceDock.DetachPanelForTransfer(panelId);
        if (panel is null) return;
        targetDock.AdoptPanel(panel, dropPoint);

        if (sourceWin != app && lastTab &&
            _floats.FirstOrDefault(f => f.Win == sourceWin) is { } emptied)
            DestroyFloat(emptied);
    }

    private void TearOut(App sourceWin, DockLayout sourceDock, string panelId, Offset global)
    {
        // Tearing the source window's only tab out of a float is a no-op (it IS a window already);
        // out of the main dock it would empty the shell.
        if (sourceDock.OpenPanelIds.Count() <= 1) return;

        var panel = sourceDock.DetachPanelForTransfer(panelId);
        if (panel is null) return;

        var win = app.CreateWindow(panel.Title, 560, 420);
        win.Theme = app.Theme;
        var dock = new DockLayout(
            win,
            _theme,
            new DockLeaf(panel.PanelId),
            [panel]
        );
        Wire(win, dock);
        win.Root = new ThemeProvider(win.Theme, new ColoredBox(_theme.Window, dock));
        // Position so the tab lands roughly under the cursor.
        win.NativeWindow!.SetPosition((int)(global.X - 80f), (int)(global.Y - 14f));

        var entry = new Entry(win, dock);
        _floats.Add(entry);
        win.CloseRequested += () => ReturnPanels(entry);
    }

    /// <summary>Titlebar ✕ on a floating window: every panel goes home to the main dock.</summary>
    private void ReturnPanels(Entry entry)
    {
        _floats.Remove(entry);
        foreach (var id in entry.Dock.OpenPanelIds.ToList())
        {
            var panel = entry.Dock.DetachPanelForTransfer(id);
            if (panel is not null) _mainDock?.AdoptPanel(panel, null);
        }
        // The window closes itself right after CloseRequested (see App.DispatchEvent).
    }

    private void DestroyFloat(Entry entry)
    {
        _floats.Remove(entry);
        entry.Win.Close();
    }

    // ── Geometry ───────────────────────────────────────────────────────────────

    private (App Win, DockLayout Dock)? FindDockAt(Offset global, App exclude)
    {
        // Floating windows first (usually above the main window); most recent first so overlaps
        // resolve to the newest float. Without OS z-order this is the best available guess.
        for (var i = _floats.Count - 1; i >= 0; i--)
        {
            var f = _floats[i];
            if (f.Win != exclude && WindowRect(f.Win).Contains(global.X, global.Y))
                return (f.Win, f.Dock);
        }

        if (app != exclude && _mainDock is { } main &&
            WindowRect(app).Contains(global.X, global.Y))
            return (app, main);

        return null;
    }

    private void ClearHovers()
    {
        foreach (var (_, dock) in AllDocks()) dock.SetExternalDropHover(null);
    }

    private IEnumerable<(App Win, DockLayout Dock)> AllDocks()
    {
        if (_mainDock is { } main) yield return (app, main);
        foreach (var f in _floats) yield return (f.Win, f.Dock);
    }

    private Rect WindowRect(App win)
    {
        var (x, y) = win.NativeWindow?.GetPosition() ?? app.Engine.MainWindowPosition();
        return new Rect(
            x,
            y,
            win.HostLogicalWidth,
            win.HostLogicalHeight
        );
    }

    private Offset ToGlobal(App win, Offset local)
    {
        var (x, y) = win.NativeWindow?.GetPosition() ?? app.Engine.MainWindowPosition();
        return new Offset(local.X + x, local.Y + y);
    }

    private Offset ToLocal(App win, Offset global)
    {
        var (x, y) = win.NativeWindow?.GetPosition() ?? app.Engine.MainWindowPosition();
        return new Offset(global.X - x, global.Y - y);
    }

    private sealed record Entry(App Win, DockLayout Dock);
}