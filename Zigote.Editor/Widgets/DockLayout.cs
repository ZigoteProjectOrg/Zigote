using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

// Aliases to avoid ambiguity with Zigote.Core.EdgeInsets

namespace Zigote.Editor.Widgets;

/// <summary>
///     Panel descriptor for <see cref="DockLayout" />.
/// </summary>
public sealed class DockPanel
{
    public required string PanelId { get; init; }

    /// <summary>Tab label. Mutable so a host can retitle a panel at runtime (e.g. the open file name).</summary>
    public required string Title { get; set; }

    public required Widget Content { get; init; }

    /// <summary>Unsaved-changes flag — the tab shows a dot instead of the close × until cleared.</summary>
    public bool Dirty { get; set; }
}

/// <summary>
///     IDE-style dockable layout with tabbed regions. Each dock region (leaf) holds one or
///     more panels shown as tabs; tabs switch on click, close with ✕, and can be dragged to
///     another region's tab bar (to join it) or to a region's edge (to split). Dividers
///     remain draggable. Each header has a collapse button (shrinks the region to a strip) and a
///     maximize button (fills the dock, hiding the rest — see <see cref="ToggleMaximize" />).
///     Structural changes raise <see cref="LayoutChanged" /> for persistence.
/// </summary>
public sealed class DockLayout : Widget
{
    // ── Constants ─────────────────────────────────────────────────────────────
    // The tab strip. Adwaita's tab bar is a band of rounded tab cards with a hairline under it,
    // so the strip needs room for the card plus its inset — 30/3 is the dense-IDE reading of the
    // 38px libadwaita bar.
    private const float HeaderH = 30f;
    private const float TabInset = 3f; // vertical gap between the tab card and the strip edges
    private const float TabRadius = 6f;
    private const float DivW = 4f;
    private const float DragStart = 6f;
    private const float ArrowSz = 26f;
    private const float MinPanelPx = 60f;
    private const float TabW = 104f;
    private const float CloseSz = 14f;
    private const float HdrBtn = 16f; // collapse / maximize header button size
    private const float CollapsedW = 30f; // width of a region collapsed inside a horizontal split

    private readonly App _app;
    private readonly List<DivEntry> _dividers = [];

    // ── Per-frame layout data ─────────────────────────────────────────────────
    private readonly Dictionary<string, Rect> _leafBounds = new(); // LeafId -> full rect
    private readonly Dictionary<string, DockLeaf> _leaves = new(); // LeafId -> leaf
    private readonly Dictionary<string, DockPanel> _panels;
    private readonly ThemeData _theme;
    private DropZone? _activeZone;

    // ── Divider drag ──────────────────────────────────────────────────────────
    private DivEntry? _divDrag;
    private float _divDragOrigin;
    private float _divRatioAtDrag;
    private Rect _divSplitBounds;
    private Offset _dragCursor;
    private string? _draggingId; // panel (tab) in flight

    private string? _hoverHeaderLeafId;
    private string? _hoverLeafId; // leaf under cursor during drag
    private string? _hoverTabPanelId;

    /// <summary>
    ///     Panel id of the region currently maximized to fill the whole dock (or null). Transient —
    ///     not persisted. Other panels are hidden while one is maximized; the header shows a restore
    ///     button. Use <see cref="ToggleMaximize" /> (e.g. the viewport's "fullscreen for testing").
    /// </summary>
    private string? _maximizedPanelId;

    private Offset _pendingDragFrom;

    // ── Tab/panel drag ────────────────────────────────────────────────────────
    private string? _pendingDragId; // panel id pressed, not yet dragging

    private Size _size;

    /// <summary>
    ///     Raised after any structural change (drag/dock/close/divider/collapse) so callers can
    ///     persist.
    /// </summary>
    public Action? LayoutChanged;

    public DockLayout(App app, ThemeData theme, DockNode root, IEnumerable<DockPanel> panels)
    {
        _app = app;
        _theme = theme;
        Root = root;
        _panels = panels.ToDictionary(p => p.PanelId);
    }

    public DockNode Root { get; private set; }

    /// <summary>True while a panel fills the whole dock (other panels hidden).</summary>
    public bool IsMaximized => _maximizedPanelId != null;

    /// <summary>Replace the whole dock tree (e.g. when restoring a saved layout).</summary>
    public void SetRoot(DockNode root)
    {
        Root = root;
        _maximizedPanelId = null;
        RequestLayout();
    }

    /// <summary>
    ///     Toggle a panel between maximized (fills the dock, other panels hidden) and the normal
    ///     docked layout. No-op if the panel isn't present. Used for the viewport's testing-fullscreen.
    /// </summary>
    public void ToggleMaximize(string panelId)
    {
        if (_maximizedPanelId == panelId)
        {
            _maximizedPanelId = null;
        }
        else if (_panels.ContainsKey(panelId) && FindLeaf(Root, panelId) is { } leaf)
        {
            // Surface the target tab and ensure it isn't collapsed so it shows when maximized.
            leaf.Collapsed = false;
            leaf.ActiveIndex = leaf.PanelIds.IndexOf(panelId);
            _maximizedPanelId = panelId;
        }

        RequestLayout();
    }

    /// <summary>
    ///     Bring a panel to the foreground: select its tab, un-collapse its region, and clear any
    ///     unrelated maximize so it's actually visible. No-op if the panel isn't in the tree. Used when a
    ///     file is opened into the docked code editor so the editor surfaces instead of staying hidden.
    /// </summary>
    public void ShowPanel(string panelId)
    {
        if (FindLeaf(Root, panelId) is not { } leaf) return;
        leaf.Collapsed = false;
        leaf.ActiveIndex = leaf.PanelIds.IndexOf(panelId);
        // If a *different* leaf is maximized, this panel would stay hidden — drop the maximize.
        if (_maximizedPanelId != null && !leaf.PanelIds.Contains(_maximizedPanelId))
            _maximizedPanelId = null;
        RequestLayout();
    }

    /// <summary>Every registered panel descriptor (open or closed) — the settings Panels section.</summary>
    public IReadOnlyCollection<DockPanel> Panels => _panels.Values;

    /// <summary>Panel ids that currently have a tab somewhere in the dock tree.</summary>
    public IEnumerable<string> OpenPanelIds => Root.LeafIds();

    // ── Cross-window panel dragging (tear-out) ─────────────────────────────────
    // The dock stays window-agnostic: it reports drags/releases in window-local coordinates and
    // exposes a transfer + external-drop API; DockWindowManager does the global-coordinate work.

    /// <summary>A tab drag is in flight (window-local point) — fired every pointer move.</summary>
    public Action<string, Offset>? TabDragMoved;

    /// <summary>
    ///     A tab drag released with NO internal drop zone (outside every leaf region or outside the
    ///     window). The host decides: drop into another window's dock, tear out, or cancel.
    /// </summary>
    public Action<string, Offset>? TabDragReleased;

    private bool _externalHover;

    /// <summary>The panel descriptor registered under <paramref name="panelId" />, if any.</summary>
    public DockPanel? GetPanel(string panelId)
    {
        return _panels.GetValueOrDefault(panelId);
    }

    /// <summary>
    ///     Take a panel out of this dock for a cross-window move: removes its tab from the tree,
    ///     unregisters the descriptor, and detaches the content (clearing this window's focus/hover
    ///     references to it). The caller hands the descriptor to another dock's
    ///     <see cref="AdoptPanel" />. The last-tab guard is the caller's job.
    /// </summary>
    public DockPanel? DetachPanelForTransfer(string panelId)
    {
        if (!_panels.Remove(panelId, out var panel)) return null;
        Root = RemovePanel(Root, panelId) ?? Root;
        panel.Content.Detach();
        RequestLayout();
        LayoutChanged?.Invoke();
        return panel;
    }

    /// <summary>
    ///     Register a panel moved in from another window's dock and insert its tab — at the drop
    ///     zone under <paramref name="dropPoint" /> when one resolves, else appended to the first
    ///     leaf. Re-attaches the content to this window's tree.
    /// </summary>
    public void AdoptPanel(DockPanel panel, Offset? dropPoint)
    {
        _panels[panel.PanelId] = panel;
        if (Owner is not null) panel.Content.Attach(Owner, this);

        if (dropPoint is { } p)
        {
            ResolveDropZone(p);
            if (_hoverLeafId is { } leafId && _activeZone is { } zone &&
                _leaves.TryGetValue(leafId, out var dstLeaf))
            {
                _externalHover = false;
                _hoverLeafId = null;
                _activeZone = null;
                InsertAtZone(panel.PanelId, dstLeaf, zone);
                RequestLayout();
                LayoutChanged?.Invoke();
                return;
            }
        }

        _externalHover = false;
        _hoverLeafId = null;
        _activeZone = null;
        OpenPanel(panel.PanelId);
    }

    /// <summary>
    ///     Show/clear drop-zone highlighting for a drag originating in ANOTHER window (this window
    ///     receives no pointer events while the source window holds the SDL mouse capture).
    /// </summary>
    public void SetExternalDropHover(Offset? localPoint)
    {
        if (localPoint is { } p)
        {
            _externalHover = true;
            _dragCursor = p;
            ResolveDropZone(p);
            MarkNeedsPaint();
        }
        else if (_externalHover)
        {
            _externalHover = false;
            _hoverLeafId = null;
            _activeZone = null;
            MarkNeedsPaint();
        }
    }

    /// <summary>
    ///     Remove a panel's tab unconditionally (cross-window move; the last-panel guard is the
    ///     host's job — see <see cref="ClosePanelById" /> for the user-facing close).
    /// </summary>
    public void RemovePanelById(string panelId)
    {
        Root = RemovePanel(Root, panelId) ?? Root;
        RequestLayout();
        LayoutChanged?.Invoke();
    }

    /// <summary>Whether a panel currently has a tab somewhere in the dock tree.</summary>
    public bool IsPanelOpen(string panelId)
    {
        return FindLeaf(Root, panelId) is not null;
    }

    /// <summary>
    ///     Close a panel: remove its tab from the tree (the region collapses away). No-op on the
    ///     last visible panel. The inverse of <see cref="OpenPanel" />.
    /// </summary>
    public void ClosePanelById(string panelId)
    {
        ClosePanel(panelId);
    }

    /// <summary>
    ///     Re-open a closed panel by appending its tab to the first dock leaf (drag it wherever it
    ///     belongs afterwards); an already-open panel is just surfaced.
    /// </summary>
    public void OpenPanel(string panelId)
    {
        if (!_panels.ContainsKey(panelId)) return;
        if (FindLeaf(Root, panelId) is not null)
        {
            ShowPanel(panelId);
            return;
        }

        if (FirstLeaf(Root) is not { } leaf) return;
        leaf.PanelIds.Add(panelId);
        leaf.ActiveIndex = leaf.PanelIds.Count - 1;
        leaf.Collapsed = false;
        RequestLayout();
        LayoutChanged?.Invoke();
    }

    private static DockLeaf? FirstLeaf(DockNode node)
    {
        return node switch {
            DockLeaf leaf => leaf,
            DockSplit split => FirstLeaf(split.First) ?? FirstLeaf(split.Second),
            _ => null,
        };
    }

    /// <summary>
    ///     Set a panel's unsaved-changes flag (drives the tab dot). Repaints when it actually
    ///     changes.
    /// </summary>
    public void SetPanelDirty(string panelId, bool dirty)
    {
        if (_panels.TryGetValue(panelId, out var p) && p.Dirty != dirty)
        {
            p.Dirty = dirty;
            MarkNeedsPaint();
        }
    }

    private static bool IsCollapsed(DockNode n)
    {
        return n is DockLeaf { Collapsed: true };
    }

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override void Attach(App owner, Widget? parent)
    {
        Owner = owner;
        Parent = parent;
        foreach (var p in _panels.Values) p.Content.Attach(owner, this);
    }

    public override void Detach()
    {
        foreach (var p in _panels.Values) p.Content.Detach();
        Owner = null;
        Parent = null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return _panels.Values.Select(p => p.Content);
    }

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(c.MaxWidth, c.MaxHeight));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _leafBounds.Clear();
        _leaves.Clear();
        _dividers.Clear();

        // Maximized: lay out only the target leaf, filling the whole dock. A stale id (panel closed
        // or moved out) falls back to the normal tree. Only that leaf gets bounds, so Paint/HitTest —
        // which iterate _leafBounds — naturally show just it.
        if (_maximizedPanelId != null && FindLeaf(Root, _maximizedPanelId) is { } maxLeaf)
        {
            LayoutNode(maxLeaf, Bounds);
        }
        else
        {
            _maximizedPanelId = null;
            LayoutNode(Root, Bounds);
        }
    }

    private void LayoutNode(DockNode node, Rect r)
    {
        switch (node)
        {
            case DockLeaf leaf:
                _leafBounds[leaf.LeafId] = r;
                _leaves[leaf.LeafId] = leaf;
                // Collapsed regions hide their content (only the strip is drawn), so skip the layout.
                if (!leaf.Collapsed && _panels.TryGetValue(leaf.ActivePanelId, out var p))
                {
                    var cH = Math.Max(0f, r.Height - HeaderH);
                    p.Content.Measure(Constraints.Tight(r.Width, cH));
                    p.Content.Layout(new Offset(r.X, r.Y + HeaderH));
                }

                break;

            case DockSplit s:
                var (fr, sr, divR) = SplitRects(r, s);
                // No draggable divider when one side is collapsed — its extent is fixed.
                if (!IsCollapsed(s.First) && !IsCollapsed(s.Second))
                    _dividers.Add(new DivEntry(s, divR));
                LayoutNode(s.First, fr);
                LayoutNode(s.Second, sr);
                break;
        }
    }

    private (Rect first, Rect second, Rect divider) SplitRects(Rect r, DockSplit s)
    {
        bool fc = IsCollapsed(s.First), sc = IsCollapsed(s.Second);

        if (s.Vertical)
        {
            // A collapsed side shrinks to just its header bar; the other side takes the rest. When
            // both are collapsed each keeps its bar and the leftover stays empty (rare).
            float fH, sH;
            if (fc && sc)
            {
                (fH, sH) = (HeaderH, HeaderH);
            }
            else if (fc)
            {
                (fH, sH) = (HeaderH, r.Height - DivW - HeaderH);
            }
            else if (sc)
            {
                (fH, sH) = (r.Height - DivW - HeaderH, HeaderH);
            }
            else
            {
                fH = MathF.Floor((r.Height - DivW) * s.Ratio);
                sH = r.Height - DivW - fH;
            }

            fH = Math.Clamp(fH, 0f, MathF.Max(0f, r.Height - DivW));
            return (new Rect(
                    r.X,
                    r.Y,
                    r.Width,
                    fH
                ),
                new Rect(
                    r.X,
                    r.Y + fH + DivW,
                    r.Width,
                    sH
                ),
                new Rect(
                    r.X,
                    r.Y + fH,
                    r.Width,
                    DivW
                ));
        }

        float fW, sW;
        if (fc && sc)
        {
            (fW, sW) = (CollapsedW, CollapsedW);
        }
        else if (fc)
        {
            (fW, sW) = (CollapsedW, r.Width - DivW - CollapsedW);
        }
        else if (sc)
        {
            (fW, sW) = (r.Width - DivW - CollapsedW, CollapsedW);
        }
        else
        {
            fW = MathF.Floor((r.Width - DivW) * s.Ratio);
            sW = r.Width - DivW - fW;
        }

        fW = Math.Clamp(fW, 0f, MathF.Max(0f, r.Width - DivW));
        return (new Rect(
                r.X,
                r.Y,
                fW,
                r.Height
            ),
            new Rect(
                r.X + fW + DivW,
                r.Y,
                sW,
                r.Height
            ),
            new Rect(
                r.X + fW,
                r.Y,
                DivW,
                r.Height
            ));
    }

    // ── Tab geometry ──────────────────────────────────────────────────────────

    private static IEnumerable<(string PanelId, Rect Tab, int Index)> TabRects(DockLeaf leaf,
        Rect bounds)
    {
        var avail = Math.Max(
            40f,
            bounds.Width - 46f
        ); // leave room for the collapse + maximize buttons
        var w = MathF.Min(TabW, leaf.PanelIds.Count > 0 ? avail / leaf.PanelIds.Count : avail);
        for (var i = 0; i < leaf.PanelIds.Count; i++)
            yield return (leaf.PanelIds[i], new Rect(
                bounds.X + i * w,
                bounds.Y,
                w,
                HeaderH
            ), i);
    }

    private static Rect CloseRect(Rect tab)
    {
        return new Rect(
            tab.Right - CloseSz - 4f,
            tab.Y + (HeaderH - CloseSz) * 0.5f,
            CloseSz,
            CloseSz
        );
    }

    /// <summary>Maximize / restore button — far right of a region header.</summary>
    private static Rect MaximizeBtnRect(Rect bounds)
    {
        return new Rect(
            bounds.Right - HdrBtn - 4f,
            bounds.Y + (HeaderH - HdrBtn) * 0.5f,
            HdrBtn,
            HdrBtn
        );
    }

    /// <summary>Collapse button — to the left of the maximize button.</summary>
    private static Rect CollapseBtnRect(Rect bounds)
    {
        return new Rect(
            bounds.Right - HdrBtn * 2f - 8f,
            bounds.Y + (HeaderH - HdrBtn) * 0.5f,
            HdrBtn,
            HdrBtn
        );
    }

    // ── Paint ───────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        paint.AddRect(Bounds, _theme.Background);
        PaintNode(Root, paint);

        foreach (var d in _dividers)
        {
            var active = _divDrag?.Split == d.Split;
            paint.AddRect(d.Rect, active ? _theme.Primary.WithAlpha(0.7f) : _theme.Border);
        }

        if (_draggingId != null || _externalHover) PaintDragOverlay(paint);
    }

    private void PaintNode(DockNode node, PaintList paint)
    {
        if (node is DockLeaf leaf)
        {
            PaintLeaf(leaf, paint);
        }
        else if (node is DockSplit s)
        {
            PaintNode(s.First, paint);
            PaintNode(s.Second, paint);
        }
    }

    private void PaintLeaf(DockLeaf leaf, PaintList paint)
    {
        if (!_leafBounds.TryGetValue(leaf.LeafId, out var bounds)) return;

        if (leaf.Collapsed)
        {
            PaintCollapsedLeaf(leaf, bounds, paint);
            return;
        }

        var hdr = new Rect(
            bounds.X,
            bounds.Y,
            bounds.Width,
            HeaderH
        );

        // Adwaita tab bar: a headerbar-toned band closed by a hairline, with the selected tab
        // riding on it as a rounded card in the content colour — the card merges into the panel
        // body below, which is what marks it selected. No accent underline; Adwaita has none.
        paint.AddRect(hdr, _theme.Toolbar);
        paint.AddRect(
            new Rect(
                hdr.X,
                hdr.Bottom - 1f,
                hdr.Width,
                1f
            ),
            _theme.Separator
        );

        var fs = _theme.FontSizeCaption;
        var multi = leaf.PanelIds.Count > 1;
        foreach (var (panelId, tab, index) in TabRects(leaf, bounds))
        {
            var isActive = index == leaf.ActiveIndex;
            var isHover = _hoverTabPanelId == panelId;
            var showClose = isActive || isHover;

            // The card: inset from the strip edges, square along the bottom so it runs into the
            // panel body. Unselected tabs are bare until hovered, then take the shared row wash.
            var card = new Rect(
                tab.X + 1f,
                tab.Y + TabInset,
                MathF.Max(0f, tab.Width - 2f),
                MathF.Max(0f, tab.Height - TabInset)
            );
            if (isActive) paint.AddRect(card, _theme.Panel, TabRadius);
            else if (AdwStyle.RowFill(_theme, isHover, false) is { A: > 0f } wash)
                paint.AddRect(card, wash, TabRadius);

            if (panelId == _draggingId) paint.AddRect(card, _theme.Primary.WithAlpha(0.15f), TabRadius);

            _panels.TryGetValue(panelId, out var dp);
            var title = dp?.Title ?? panelId;
            var dirty = dp?.Dirty == true;
            // Reserve room for whatever sits in the trailing slot (close × or the unsaved dot).
            var reserveRight = showClose || dirty;
            var maxChars = (int)((tab.Width - 16f - (reserveRight ? CloseSz : 0f)) / (fs * 0.52f));
            if (title.Length > maxChars && maxChars > 1) title = title[..(maxChars - 1)] + "…";
            // Adwaita dims unselected tab labels rather than recolouring them per hover state.
            var titleColor = isActive ? _theme.OnSurface : AdwPalette.For(_theme).DimLabel;
            paint.AddText(
                title,
                tab.X + 8f,
                tab.Y + HeaderH * 0.72f,
                titleColor,
                fs - 1f
            );

            // Trailing slot: an unsaved-changes dot when dirty (and not hovering), otherwise the close ×
            // on the active/hovered tab. Hovering a dirty tab swaps the dot for × so it stays closable.
            var cr = CloseRect(tab);
            if (dirty && !isHover)
            {
                const float dotD = 7f;
                paint.AddRect(
                    new Rect(
                        cr.X + (CloseSz - dotD) * 0.5f,
                        cr.Y + (CloseSz - dotD) * 0.5f,
                        dotD,
                        dotD
                    ),
                    isActive ? _theme.OnSurface : _theme.Hint,
                    dotD * 0.5f
                );
            }
            else if (showClose)
            {
                // The real close glyph, not a "×" character — the multiplication sign renders at
                // whatever weight the UI face happens to give it and never matched the icon set.
                Icons.Draw(
                    paint,
                    Icons.Close,
                    cr,
                    isHover ? _theme.OnBackground : AdwPalette.For(_theme).DimLabel,
                    12f
                );
            }

            // Hairline between two unselected tabs, as AdwTabBar draws it — never beside the
            // selected card, whose rounded edge is its own separation.
            var nextActive = index + 1 == leaf.ActiveIndex;
            if (multi && index < leaf.PanelIds.Count - 1 && !isActive && !nextActive)
                paint.AddRect(
                    new Rect(
                        tab.Right - 1f,
                        tab.Y + TabInset + 4f,
                        1f,
                        MathF.Max(0f, HeaderH - TabInset * 2f - 8f)
                    ),
                    _theme.Separator
                );
        }

        // Header buttons (right): collapse + maximize. While maximized, collapse is hidden and the
        // maximize button reads as "restore".
        var hover = _hoverHeaderLeafId == leaf.LeafId;
        var maximized = _maximizedPanelId != null && leaf.PanelIds.Contains(_maximizedPanelId);
        PaintHdrBtn(
            paint,
            MaximizeBtnRect(bounds),
            maximized ? Icons.FullscreenExit : Icons.Fullscreen,
            hover
        );
        if (!maximized)
            PaintHdrBtn(
                paint,
                CollapseBtnRect(bounds),
                Icons.UnfoldLess,
                hover
            );

        // Active content (clipped). The body sits one elevation above the window so the shell reads
        // as layered surfaces; the panel content paints over this fill.
        var active = _panels.TryGetValue(leaf.ActivePanelId, out var ap) ? ap : null;
        var ch = Math.Max(0f, bounds.Height - HeaderH);
        if (ch > 0f && active != null)
        {
            var contentRect = new Rect(
                bounds.X,
                bounds.Y + HeaderH,
                bounds.Width,
                ch
            );
            paint.AddClipStart(contentRect);
            paint.AddRect(contentRect, _theme.Panel);
            active.Content.Paint(paint);
            paint.AddClipEnd();
        }

        paint.AddBorder(bounds, _theme.Border);
    }

    /// <summary>A flat circular header action, the shape AdwButton gives a header-bar icon.</summary>
    private void PaintHdrBtn(PaintList paint, Rect r, string icon, bool headerHover)
    {
        if (AdwStyle.RowFill(_theme, headerHover, false) is { A: > 0f } wash)
            paint.AddRect(r, wash, r.Height * 0.5f);
        Icons.Draw(
            paint,
            icon,
            r,
            headerHover ? _theme.OnBackground : AdwPalette.For(_theme).DimLabel,
            14f
        );
    }

    /// <summary>
    ///     A collapsed region: only a thin strip with an expand affordance + the active panel's title.
    ///     Horizontal strip (collapsed inside a vertical split) keeps the title inline; a vertical strip
    ///     (collapsed inside a horizontal split) stacks the title down the bar. Clicking expands it.
    /// </summary>
    private void PaintCollapsedLeaf(DockLeaf leaf, Rect bounds, PaintList paint)
    {
        paint.AddRect(bounds, _theme.Toolbar);

        var fs = _theme.FontSizeCaption;
        var hovered = _hoverHeaderLeafId == leaf.LeafId;
        var col = hovered ? _theme.OnBackground : AdwPalette.For(_theme).DimLabel;
        var title = _panels.TryGetValue(leaf.ActivePanelId, out var dp)
            ? dp.Title
            : leaf.ActivePanelId;

        if (bounds.Height <= HeaderH + 1f)
        {
            // Horizontal strip — chevron + inline title.
            Icons.Draw(
                paint,
                Icons.UnfoldMore,
                new Rect(
                    bounds.X + 2f,
                    bounds.Y,
                    HeaderH,
                    HeaderH
                ),
                col,
                14f
            );
            paint.AddText(
                title,
                bounds.X + HeaderH + 2f,
                bounds.Y + HeaderH * 0.72f,
                col,
                fs - 1f
            );
        }
        else
        {
            // Vertical strip — chevron on top, title stacked downward.
            Icons.Draw(
                paint,
                Icons.UnfoldMore,
                new Rect(
                    bounds.X,
                    bounds.Y + 2f,
                    bounds.Width,
                    HeaderH
                ),
                col,
                14f
            );
            var cy = bounds.Y + HeaderH + 4f;
            foreach (var glyph in title)
            {
                if (cy > bounds.Bottom - fs) break;
                paint.AddText(
                    glyph.ToString(),
                    bounds.X + (bounds.Width - fs * 0.55f) * 0.5f,
                    cy + fs * 0.8f,
                    col,
                    fs - 1f
                );
                cy += fs + 1f;
            }
        }

        paint.AddBorder(bounds, _theme.Border);
    }

    private void PaintDragOverlay(PaintList paint)
    {
        // The floating title ghost belongs to the window whose pointer drives the drag; an
        // external hover (drag from another window) shows only the drop-zone previews.
        if (_draggingId is not null)
        {
            const float gW = 150f, gH = 26f;
            var ghost = new Rect(
                _dragCursor.X - gW * 0.5f,
                _dragCursor.Y - gH * 0.5f,
                gW,
                gH
            );
            paint.AddRect(ghost, _theme.Primary.WithAlpha(0.85f), 4f);
            var title = _panels.TryGetValue(_draggingId, out var dp) ? dp.Title : _draggingId;
            paint.AddText(
                title,
                ghost.X + 8f,
                ghost.Y + gH * 0.75f,
                _theme.OnPrimary,
                _theme.FontSizeBody
            );
        }

        if (_hoverLeafId == null || !_leafBounds.TryGetValue(_hoverLeafId, out var hb)) return;
        paint.AddRect(hb, _theme.Primary.WithAlpha(0.06f));

        if (_activeZone.HasValue)
        {
            var pr = DropPreviewRect(hb, _activeZone.Value);
            paint.AddRect(pr, _theme.Primary.WithAlpha(0.30f));
            paint.AddBorder(
                pr,
                _theme.Primary,
                0f,
                2f
            );
        }

        PaintArrows(paint, hb);
    }

    private static Rect DropPreviewRect(Rect b, DropZone z)
    {
        return z switch {
            DropZone.Left => new Rect(
                b.X,
                b.Y,
                b.Width * 0.33f,
                b.Height
            ),
            DropZone.Right => new Rect(
                b.X + b.Width * 0.67f,
                b.Y,
                b.Width * 0.33f,
                b.Height
            ),
            DropZone.Top => new Rect(
                b.X,
                b.Y,
                b.Width,
                b.Height * 0.25f
            ),
            DropZone.Bottom => new Rect(
                b.X,
                b.Y + b.Height * 0.75f,
                b.Width,
                b.Height * 0.25f
            ),
            _ => new Rect(
                b.X,
                b.Y,
                b.Width,
                HeaderH
            ), // Center → tab bar
        };
    }

    private void PaintArrows(PaintList paint, Rect b)
    {
        var cx = b.X + b.Width * 0.5f - ArrowSz * 0.5f;
        var cy = b.Y + b.Height * 0.5f - ArrowSz * 0.5f;
        const float m = 8f;

        if (b.Width > ArrowSz + m * 2f)
        {
            PaintArrow(
                paint,
                new Rect(
                    b.X + m,
                    cy,
                    ArrowSz,
                    ArrowSz
                ),
                "◀",
                _activeZone == DropZone.Left
            );
            PaintArrow(
                paint,
                new Rect(
                    b.Right - ArrowSz - m,
                    cy,
                    ArrowSz,
                    ArrowSz
                ),
                "▶",
                _activeZone == DropZone.Right
            );
        }

        if (b.Height > ArrowSz + m * 2f)
        {
            PaintArrow(
                paint,
                new Rect(
                    cx,
                    b.Y + m,
                    ArrowSz,
                    ArrowSz
                ),
                "▲",
                _activeZone == DropZone.Top
            );
            PaintArrow(
                paint,
                new Rect(
                    cx,
                    b.Bottom - ArrowSz - m,
                    ArrowSz,
                    ArrowSz
                ),
                "▼",
                _activeZone == DropZone.Bottom
            );
        }

        PaintArrow(
            paint,
            new Rect(
                cx,
                cy,
                ArrowSz,
                ArrowSz
            ),
            "+",
            _activeZone == DropZone.Center
        );
    }

    private void PaintArrow(PaintList paint, Rect r, string icon, bool active)
    {
        paint.AddRect(r, active ? _theme.Primary : _theme.Surface.WithAlpha(0.88f), 5f);
        paint.AddBorder(r, active ? _theme.Primary : _theme.OnSurface.WithAlpha(0.25f), 5f);
        paint.AddText(
            icon,
            r.X + r.Width * 0.22f,
            r.Y + r.Height * 0.76f,
            active ? _theme.OnPrimary : _theme.OnSurface.WithAlpha(0.75f),
            _theme.FontSizeBody
        );
    }

    // ── Hit testing ─────────────────────────────────────────────────────────────

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;

        foreach (var d in _dividers)
            if (d.Rect.Contains(point.X, point.Y))
                return this;

        // Tab bars + header buttons + collapsed strips — handled by the dock itself.
        foreach (var (leafId, b) in _leafBounds)
        {
            var leaf = _leaves.GetValueOrDefault(leafId);
            if (leaf is { Collapsed: true })
            {
                if (b.Contains(point.X, point.Y)) return this;
                continue;
            }

            var hdr = new Rect(
                b.X,
                b.Y,
                b.Width,
                HeaderH
            );
            if (hdr.Contains(point.X, point.Y)) return this;
        }

        if (_draggingId != null)
            foreach (var (_, r) in _leafBounds)
                if (r.Contains(point.X, point.Y))
                    return this;

        // Content → active panel of the containing leaf
        foreach (var (leafId, b) in _leafBounds)
        {
            var content = new Rect(
                b.X,
                b.Y + HeaderH,
                b.Width,
                Math.Max(0f, b.Height - HeaderH)
            );
            if (!content.Contains(point.X, point.Y)) continue;
            if (_leaves.TryGetValue(leafId, out var leaf) &&
                _panels.TryGetValue(leaf.ActivePanelId, out var p))
                return p.Content.HitTest(point) ?? this;
            return this;
        }

        return this;
    }

    // ── Pointer input ─────────────────────────────────────────────────────────

    public override void OnPointerDown(Offset point)
    {
        foreach (var d in _dividers)
        {
            if (!d.Rect.Contains(point.X, point.Y)) continue;
            _divDrag = d;
            _divDragOrigin = d.Split.Vertical ? point.Y : point.X;
            _divRatioAtDrag = d.Split.Ratio;
            _divSplitBounds = ComputeSplitBounds(d.Split);
            return;
        }

        foreach (var (leafId, b) in _leafBounds)
        {
            var leaf = _leaves[leafId];

            // Collapsed strip: a click anywhere expands the region.
            if (leaf.Collapsed)
            {
                if (!b.Contains(point.X, point.Y)) continue;
                leaf.Collapsed = false;
                RequestLayout();
                LayoutChanged?.Invoke();
                return;
            }

            var hdr = new Rect(
                b.X,
                b.Y,
                b.Width,
                HeaderH
            );
            if (!hdr.Contains(point.X, point.Y)) continue;

            // Maximize / restore button.
            if (MaximizeBtnRect(b).Contains(point.X, point.Y))
            {
                ToggleMaximize(leaf.ActivePanelId);
                return;
            }

            // Collapse button — hidden (and ignored) while a panel is maximized.
            var maximized = _maximizedPanelId != null && leaf.PanelIds.Contains(_maximizedPanelId);
            if (!maximized && CollapseBtnRect(b).Contains(point.X, point.Y))
            {
                leaf.Collapsed = true;
                RequestLayout();
                LayoutChanged?.Invoke();
                return;
            }

            foreach (var (panelId, tab, index) in TabRects(leaf, b))
            {
                if (!tab.Contains(point.X, point.Y)) continue;

                if (CloseRect(tab).Contains(point.X, point.Y))
                {
                    ClosePanel(panelId);
                    return;
                }

                leaf.ActiveIndex = index; // activate on press
                _pendingDragId = panelId; // may become a drag
                _pendingDragFrom = point;
                return;
            }

            return;
        }
    }

    public override void OnPointerMove(Offset point)
    {
        if (_divDrag is { } d)
        {
            UpdateDividerRatio(d, point);
            return;
        }

        if (_pendingDragId != null)
        {
            var dx = point.X - _pendingDragFrom.X;
            var dy = point.Y - _pendingDragFrom.Y;
            if (MathF.Sqrt(dx * dx + dy * dy) >= DragStart)
            {
                _draggingId = _pendingDragId;
                _pendingDragId = null;
            }
        }

        if (_draggingId != null)
        {
            _dragCursor = point;
            ResolveDropZone(point);
            TabDragMoved?.Invoke(_draggingId, point);
            return;
        }

        _hoverHeaderLeafId = null;
        _hoverTabPanelId = null;
        foreach (var (leafId, b) in _leafBounds)
        {
            var leaf = _leaves[leafId];
            if (leaf.Collapsed)
            {
                if (!b.Contains(point.X, point.Y)) continue;
                _hoverHeaderLeafId = leafId;
                break;
            }

            var hdr = new Rect(
                b.X,
                b.Y,
                b.Width,
                HeaderH
            );
            if (!hdr.Contains(point.X, point.Y)) continue;
            _hoverHeaderLeafId = leafId;
            foreach (var (panelId, tab, _) in TabRects(leaf, b))
                if (tab.Contains(point.X, point.Y))
                {
                    _hoverTabPanelId = panelId;
                    break;
                }

            break;
        }
    }

    public override void OnPointerUp(Offset point)
    {
        if (_divDrag != null)
        {
            _divDrag = null;
            LayoutChanged?.Invoke();
        }

        _pendingDragId = null;

        if (_draggingId != null)
        {
            // Clear the drag state BEFORE handing off: TabDragReleased may mutate this dock
            // (transfer the panel out) and repaint mid-callback.
            var dragged = _draggingId;
            var leafId = _hoverLeafId;
            var zone = _activeZone;
            _draggingId = null;
            _hoverLeafId = null;
            _activeZone = null;

            if (leafId != null && zone.HasValue)
                CommitDrop(dragged, leafId, zone.Value);
            else
                TabDragReleased?.Invoke(dragged, point);
        }
    }

    public override void OnPointerExit()
    {
        _hoverHeaderLeafId = null;
        _hoverTabPanelId = null;
    }

    // ── Divider drag ──────────────────────────────────────────────────────────

    private void UpdateDividerRatio(DivEntry d, Offset pt)
    {
        var b = _divSplitBounds;
        var pos = d.Split.Vertical ? pt.Y - b.Y : pt.X - b.X;
        var tot = d.Split.Vertical ? b.Height - DivW : b.Width - DivW;
        if (tot <= 0f) return;
        var minR = MinPanelPx / tot;
        d.Split.Ratio = Math.Clamp(pos / tot, Math.Min(minR, 0.05f), Math.Max(1f - minR, 0.95f));
        RequestLayout();
    }

    private Rect ComputeSplitBounds(DockSplit s)
    {
        float x1 = float.MaxValue, y1 = float.MaxValue, x2 = float.MinValue, y2 = float.MinValue;
        foreach (var leaf in CollectLeaves(s))
        {
            if (!_leafBounds.TryGetValue(leaf.LeafId, out var r)) continue;
            x1 = Math.Min(x1, r.X);
            y1 = Math.Min(y1, r.Y);
            x2 = Math.Max(x2, r.Right);
            y2 = Math.Max(y2, r.Bottom);
        }

        return x1 < float.MaxValue
            ? new Rect(
                x1,
                y1,
                x2 - x1,
                y2 - y1
            )
            : Rect.Zero;
    }

    private static IEnumerable<DockLeaf> CollectLeaves(DockNode n)
    {
        if (n is DockLeaf l) yield return l;
        else if (n is DockSplit s)
            foreach (var x in CollectLeaves(s.First).Concat(CollectLeaves(s.Second)))
                yield return x;
    }

    // ── Drop zone resolution ──────────────────────────────────────────────────

    private void ResolveDropZone(Offset pt)
    {
        _hoverLeafId = null;
        _activeZone = null;

        foreach (var (leafId, r) in _leafBounds)
        {
            if (!r.Contains(pt.X, pt.Y)) continue;
            _hoverLeafId = leafId;

            // Over the tab bar (or central region) → join as a tab.
            if (pt.Y <= r.Y + HeaderH)
            {
                _activeZone = DropZone.Center;
                break;
            }

            var rx = (pt.X - r.X) / r.Width;
            var ry = (pt.Y - r.Y) / r.Height;
            _activeZone = (rx, ry) switch {
                (< 0.25f, _) => DropZone.Left,
                (> 0.75f, _) => DropZone.Right,
                (_, < 0.25f) => DropZone.Top,
                (_, > 0.75f) => DropZone.Bottom,
                _ => DropZone.Center,
            };
            break;
        }
    }

    // ── Tree mutation ─────────────────────────────────────────────────────────

    private void CommitDrop(string srcPanelId, string dstLeafId, DropZone zone)
    {
        if (!_leaves.TryGetValue(dstLeafId, out var dstLeaf)) return;
        var srcLeaf = FindLeaf(Root, srcPanelId);
        if (srcLeaf == null) return;

        // No-op: dropping the only tab of a leaf onto itself.
        if (srcLeaf == dstLeaf && (zone == DropZone.Center || dstLeaf.PanelIds.Count == 1)) return;

        Root = RemovePanel(Root, srcPanelId) ?? Root;
        InsertAtZone(srcPanelId, dstLeaf, zone);

        RequestLayout();
        LayoutChanged?.Invoke();
    }

    /// <summary>Insert a (tree-absent) panel's tab at a zone relative to <paramref name="dstLeaf" />.</summary>
    private void InsertAtZone(string panelId, DockLeaf dstLeaf, DropZone zone)
    {
        if (zone == DropZone.Center)
        {
            dstLeaf.PanelIds.Add(panelId);
            dstLeaf.ActiveIndex = dstLeaf.PanelIds.Count - 1;
            return;
        }

        var newLeaf = new DockLeaf(panelId);
        var split = zone switch {
            DropZone.Left => new DockSplit(
                newLeaf,
                dstLeaf,
                false,
                0.33f
            ),
            DropZone.Right => new DockSplit(
                dstLeaf,
                newLeaf,
                false,
                0.67f
            ),
            DropZone.Top => new DockSplit(
                newLeaf,
                dstLeaf,
                true,
                0.25f
            ),
            DropZone.Bottom => new DockSplit(
                dstLeaf,
                newLeaf,
                true,
                0.75f
            ),
            _ => new DockSplit(dstLeaf, newLeaf),
        };
        Root = ReplaceNode(Root, dstLeaf, split);
    }

    private void ClosePanel(string panelId)
    {
        // Never close the very last visible panel.
        if (Root.LeafIds().Count() <= 1) return;
        Root = RemovePanel(Root, panelId) ?? Root;
        RequestLayout();
        LayoutChanged?.Invoke();
    }

    private static DockNode? RemovePanel(DockNode node, string panelId)
    {
        switch (node)
        {
            case DockLeaf leaf:
                if (leaf.PanelIds.Remove(panelId))
                {
                    if (leaf.ActiveIndex >= leaf.PanelIds.Count)
                        leaf.ActiveIndex = Math.Max(0, leaf.PanelIds.Count - 1);
                    return leaf.PanelIds.Count == 0 ? null : leaf;
                }

                return leaf;

            case DockSplit s:
                if (TreeContains(s.First, panelId))
                {
                    var nf = RemovePanel(s.First, panelId);
                    if (nf == null) return s.Second;
                    s.First = nf;
                    return s;
                }

                if (TreeContains(s.Second, panelId))
                {
                    var ns = RemovePanel(s.Second, panelId);
                    if (ns == null) return s.First;
                    s.Second = ns;
                    return s;
                }

                return s;

            default: return node;
        }
    }

    private static DockNode ReplaceNode(DockNode node, DockNode target, DockNode replacement)
    {
        if (ReferenceEquals(node, target)) return replacement;
        if (node is DockSplit s)
        {
            s.First = ReplaceNode(s.First, target, replacement);
            s.Second = ReplaceNode(s.Second, target, replacement);
        }

        return node;
    }

    private static DockLeaf? FindLeaf(DockNode node, string panelId)
    {
        return node switch {
            DockLeaf l => l.PanelIds.Contains(panelId) ? l : null,
            DockSplit s => FindLeaf(s.First, panelId) ?? FindLeaf(s.Second, panelId),
            _ => null,
        };
    }

    private static bool TreeContains(DockNode node, string panelId)
    {
        return node switch {
            DockLeaf l => l.PanelIds.Contains(panelId),
            DockSplit s => TreeContains(s.First, panelId) || TreeContains(s.Second, panelId),
            _ => false,
        };
    }

    private record struct DivEntry(DockSplit Split, Rect Rect);
}