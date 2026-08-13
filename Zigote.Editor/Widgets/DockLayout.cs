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

    /// <summary>
    ///     Raised after any structural change (drag/dock/close/divider/collapse) so callers can
    ///     persist.
    /// </summary>
    public Action? LayoutChanged;

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

    private DropZone? _activeZone;

    // ── Divider drag ──────────────────────────────────────────────────────────
    private DivEntry? _divDrag;
    private float _divDragOrigin;
    private float _divRatioAtDrag;
    private Rect _divSplitBounds;
    private Offset _dragCursor;
    private string? _draggingId; // panel (tab) in flight

    private bool _externalHover;

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

    /// <summary>Every registered panel descriptor (open or closed) — the settings Panels section.</summary>
    public IReadOnlyCollection<DockPanel> Panels => _panels.Values;

    /// <summary>Panel ids that currently have a tab somewhere in the dock tree.</summary>
    public IEnumerable<string> OpenPanelIds => Root.LeafIds();

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
            _maximizedPanelId = null;
        else if (_panels.ContainsKey(panelId) && FindLeaf(node: Root, panelId: panelId) is { } leaf)
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
        if (FindLeaf(node: Root, panelId: panelId) is not { } leaf) return;
        leaf.Collapsed = false;
        leaf.ActiveIndex = leaf.PanelIds.IndexOf(panelId);
        // If a *different* leaf is maximized, this panel would stay hidden — drop the maximize.
        if (_maximizedPanelId != null && !leaf.PanelIds.Contains(_maximizedPanelId))
            _maximizedPanelId = null;
        RequestLayout();
    }

    /// <summary>The panel descriptor registered under <paramref name="panelId" />, if any.</summary>
    public DockPanel? GetPanel(string panelId) => _panels.GetValueOrDefault(panelId);

    /// <summary>
    ///     Take a panel out of this dock for a cross-window move: removes its tab from the tree,
    ///     unregisters the descriptor, and detaches the content (clearing this window's focus/hover
    ///     references to it). The caller hands the descriptor to another dock's
    ///     <see cref="AdoptPanel" />. The last-tab guard is the caller's job.
    /// </summary>
    public DockPanel? DetachPanelForTransfer(string panelId)
    {
        if (!_panels.Remove(key: panelId, value: out var panel)) return null;
        Root = RemovePanel(node: Root, panelId: panelId) ?? Root;
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
        if (Owner is not null) panel.Content.Attach(owner: Owner, parent: this);

        if (dropPoint is { } p)
        {
            ResolveDropZone(p);
            if (_hoverLeafId is { } leafId && _activeZone is { } zone &&
                _leaves.TryGetValue(key: leafId, value: out var dstLeaf))
            {
                _externalHover = false;
                _hoverLeafId = null;
                _activeZone = null;
                InsertAtZone(panelId: panel.PanelId, dstLeaf: dstLeaf, zone: zone);
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
        Root = RemovePanel(node: Root, panelId: panelId) ?? Root;
        RequestLayout();
        LayoutChanged?.Invoke();
    }

    /// <summary>Whether a panel currently has a tab somewhere in the dock tree.</summary>
    public bool IsPanelOpen(string panelId) => FindLeaf(node: Root, panelId: panelId) is not null;

    /// <summary>
    ///     Close a panel: remove its tab from the tree (the region collapses away). No-op on the
    ///     last visible panel. The inverse of <see cref="OpenPanel" />.
    /// </summary>
    public void ClosePanelById(string panelId) => ClosePanel(panelId);

    /// <summary>
    ///     Re-open a closed panel by appending its tab to the first dock leaf (drag it wherever it
    ///     belongs afterwards); an already-open panel is just surfaced.
    /// </summary>
    public void OpenPanel(string panelId)
    {
        if (!_panels.ContainsKey(panelId)) return;
        if (FindLeaf(node: Root, panelId: panelId) is not null)
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
        if (_panels.TryGetValue(key: panelId, value: out var p) && p.Dirty != dirty)
        {
            p.Dirty = dirty;
            MarkNeedsPaint();
        }
    }

    private static bool IsCollapsed(DockNode n) => n is DockLeaf { Collapsed: true };

    // ── Widget protocol ───────────────────────────────────────────────────────

    public override void Attach(App owner, Widget? parent)
    {
        Owner = owner;
        Parent = parent;
        foreach (var p in _panels.Values) p.Content.Attach(owner: owner, parent: this);
    }

    public override void Detach()
    {
        foreach (var p in _panels.Values) p.Content.Detach();
        Owner = null;
        Parent = null;
    }

    public override IEnumerable<Widget> GetChildren() => _panels.Values.Select(p => p.Content);

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        _leafBounds.Clear();
        _leaves.Clear();
        _dividers.Clear();

        // Maximized: lay out only the target leaf, filling the whole dock. A stale id (panel closed
        // or moved out) falls back to the normal tree. Only that leaf gets bounds, so Paint/HitTest —
        // which iterate _leafBounds — naturally show just it.
        if (_maximizedPanelId != null && FindLeaf(node: Root, panelId: _maximizedPanelId) is
                { } maxLeaf)
            LayoutNode(node: maxLeaf, r: Bounds);
        else
        {
            _maximizedPanelId = null;
            LayoutNode(node: Root, r: Bounds);
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
                if (!leaf.Collapsed && _panels.TryGetValue(
                        key: leaf.ActivePanelId,
                        value: out var p
                    ))
                {
                    float cH = Math.Max(val1: 0f, val2: r.Height - HeaderH);
                    p.Content.Measure(Constraints.Tight(width: r.Width, height: cH));
                    p.Content.Layout(new Offset(x: r.X, y: r.Y + HeaderH));
                }

                break;

            case DockSplit s:
                var (fr, sr, divR) = SplitRects(r: r, s: s);
                // No draggable divider when one side is collapsed — its extent is fixed.
                if (!IsCollapsed(s.First) && !IsCollapsed(s.Second))
                    _dividers.Add(new DivEntry(Split: s, Rect: divR));
                LayoutNode(node: s.First, r: fr);
                LayoutNode(node: s.Second, r: sr);
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
                (fH, sH) = (HeaderH, HeaderH);
            else if (fc)
                (fH, sH) = (HeaderH, r.Height - DivW - HeaderH);
            else if (sc)
                (fH, sH) = (r.Height - DivW - HeaderH, HeaderH);
            else
            {
                fH = MathF.Floor((r.Height - DivW) * s.Ratio);
                sH = r.Height - DivW - fH;
            }

            fH = Math.Clamp(value: fH, min: 0f, max: MathF.Max(x: 0f, y: r.Height - DivW));
            return (new Rect(
                    x: r.X,
                    y: r.Y,
                    width: r.Width,
                    height: fH
                ),
                new Rect(
                    x: r.X,
                    y: r.Y + fH + DivW,
                    width: r.Width,
                    height: sH
                ),
                new Rect(
                    x: r.X,
                    y: r.Y + fH,
                    width: r.Width,
                    height: DivW
                ));
        }

        float fW, sW;
        if (fc && sc)
            (fW, sW) = (CollapsedW, CollapsedW);
        else if (fc)
            (fW, sW) = (CollapsedW, r.Width - DivW - CollapsedW);
        else if (sc)
            (fW, sW) = (r.Width - DivW - CollapsedW, CollapsedW);
        else
        {
            fW = MathF.Floor((r.Width - DivW) * s.Ratio);
            sW = r.Width - DivW - fW;
        }

        fW = Math.Clamp(value: fW, min: 0f, max: MathF.Max(x: 0f, y: r.Width - DivW));
        return (new Rect(
                x: r.X,
                y: r.Y,
                width: fW,
                height: r.Height
            ),
            new Rect(
                x: r.X + fW + DivW,
                y: r.Y,
                width: sW,
                height: r.Height
            ),
            new Rect(
                x: r.X + fW,
                y: r.Y,
                width: DivW,
                height: r.Height
            ));
    }

    // ── Tab geometry ──────────────────────────────────────────────────────────

    private static IEnumerable<(string PanelId, Rect Tab, int Index)> TabRects(DockLeaf leaf,
        Rect bounds)
    {
        float avail = Math.Max(
            val1: 40f,
            val2: bounds.Width - 46f
        ); // leave room for the collapse + maximize buttons
        float w = MathF.Min(
            x: TabW,
            y: leaf.PanelIds.Count > 0 ? avail / leaf.PanelIds.Count : avail
        );
        for (int i = 0; i < leaf.PanelIds.Count; i++)
        {
            yield return (leaf.PanelIds[i], new Rect(
                x: bounds.X + (i * w),
                y: bounds.Y,
                width: w,
                height: HeaderH
            ), i);
        }
    }

    private static Rect CloseRect(Rect tab)
    {
        return new Rect(
            x: tab.Right - CloseSz - 4f,
            y: tab.Y + ((HeaderH - CloseSz) * 0.5f),
            width: CloseSz,
            height: CloseSz
        );
    }

    /// <summary>Maximize / restore button — far right of a region header.</summary>
    private static Rect MaximizeBtnRect(Rect bounds)
    {
        return new Rect(
            x: bounds.Right - HdrBtn - 4f,
            y: bounds.Y + ((HeaderH - HdrBtn) * 0.5f),
            width: HdrBtn,
            height: HdrBtn
        );
    }

    /// <summary>Collapse button — to the left of the maximize button.</summary>
    private static Rect CollapseBtnRect(Rect bounds)
    {
        return new Rect(
            x: bounds.Right - (HdrBtn * 2f) - 8f,
            y: bounds.Y + ((HeaderH - HdrBtn) * 0.5f),
            width: HdrBtn,
            height: HdrBtn
        );
    }

    // ── Paint ───────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        paint.AddRect(bounds: Bounds, color: _theme.Background);
        PaintNode(node: Root, paint: paint);

        foreach (var d in _dividers)
        {
            bool active = _divDrag?.Split == d.Split;
            paint.AddRect(
                bounds: d.Rect,
                color: active ? _theme.Primary.WithAlpha(0.7f) : _theme.Border
            );
        }

        if (_draggingId != null || _externalHover) PaintDragOverlay(paint);
    }

    private void PaintNode(DockNode node, PaintList paint)
    {
        if (node is DockLeaf leaf)
            PaintLeaf(leaf: leaf, paint: paint);
        else if (node is DockSplit s)
        {
            PaintNode(node: s.First, paint: paint);
            PaintNode(node: s.Second, paint: paint);
        }
    }

    private void PaintLeaf(DockLeaf leaf, PaintList paint)
    {
        if (!_leafBounds.TryGetValue(key: leaf.LeafId, value: out var bounds)) return;

        if (leaf.Collapsed)
        {
            PaintCollapsedLeaf(leaf: leaf, bounds: bounds, paint: paint);
            return;
        }

        var hdr = new Rect(
            x: bounds.X,
            y: bounds.Y,
            width: bounds.Width,
            height: HeaderH
        );

        // Adwaita tab bar: a headerbar-toned band closed by a hairline, with the selected tab
        // riding on it as a rounded card in the content colour — the card merges into the panel
        // body below, which is what marks it selected. No accent underline; Adwaita has none.
        paint.AddRect(bounds: hdr, color: _theme.Toolbar);
        paint.AddRect(
            bounds: new Rect(
                x: hdr.X,
                y: hdr.Bottom - 1f,
                width: hdr.Width,
                height: 1f
            ),
            color: _theme.Separator
        );

        float fs = _theme.FontSizeCaption;
        bool multi = leaf.PanelIds.Count > 1;
        foreach ((string panelId, var tab, int index) in TabRects(leaf: leaf, bounds: bounds))
        {
            bool isActive = index == leaf.ActiveIndex;
            bool isHover = _hoverTabPanelId == panelId;
            bool showClose = isActive || isHover;

            // The card: inset from the strip edges, square along the bottom so it runs into the
            // panel body. Unselected tabs are bare until hovered, then take the shared row wash.
            var card = new Rect(
                x: tab.X + 1f,
                y: tab.Y + TabInset,
                width: MathF.Max(x: 0f, y: tab.Width - 2f),
                height: MathF.Max(x: 0f, y: tab.Height - TabInset)
            );
            if (isActive) paint.AddRect(bounds: card, color: _theme.Panel, radius: TabRadius);
            else if (AdwStyle.RowFill(theme: _theme, hovered: isHover, pressed: false) is
                     { A: > 0f } wash)
                paint.AddRect(bounds: card, color: wash, radius: TabRadius);

            if (panelId == _draggingId)
            {
                paint.AddRect(
                    bounds: card,
                    color: _theme.Primary.WithAlpha(0.15f),
                    radius: TabRadius
                );
            }

            _panels.TryGetValue(key: panelId, value: out var dp);
            string title = dp?.Title ?? panelId;
            bool dirty = dp?.Dirty == true;
            // Reserve room for whatever sits in the trailing slot (close × or the unsaved dot).
            bool reserveRight = showClose || dirty;
            int maxChars = (int)((tab.Width - 16f - (reserveRight ? CloseSz : 0f)) / (fs * 0.52f));
            if (title.Length > maxChars && maxChars > 1) title = title[..(maxChars - 1)] + "…";
            // Adwaita dims unselected tab labels rather than recolouring them per hover state.
            var titleColor = isActive ? _theme.OnSurface : AdwPalette.For(_theme).DimLabel;
            paint.AddText(
                text: title,
                baselineX: tab.X + 8f,
                baselineY: tab.Y + (HeaderH * 0.72f),
                color: titleColor,
                fontSize: fs - 1f
            );

            // Trailing slot: an unsaved-changes dot when dirty (and not hovering), otherwise the close ×
            // on the active/hovered tab. Hovering a dirty tab swaps the dot for × so it stays closable.
            var cr = CloseRect(tab);
            if (dirty && !isHover)
            {
                const float dotD = 7f;
                paint.AddRect(
                    bounds: new Rect(
                        x: cr.X + ((CloseSz - dotD) * 0.5f),
                        y: cr.Y + ((CloseSz - dotD) * 0.5f),
                        width: dotD,
                        height: dotD
                    ),
                    color: isActive ? _theme.OnSurface : _theme.Hint,
                    radius: dotD * 0.5f
                );
            }
            else if (showClose)
            {
                // The real close glyph, not a "×" character — the multiplication sign renders at
                // whatever weight the UI face happens to give it and never matched the icon set.
                Icons.Draw(
                    paint: paint,
                    glyph: Icons.Close,
                    box: cr,
                    color: isHover ? _theme.OnBackground : AdwPalette.For(_theme).DimLabel,
                    size: 12f
                );
            }

            // Hairline between two unselected tabs, as AdwTabBar draws it — never beside the
            // selected card, whose rounded edge is its own separation.
            bool nextActive = index + 1 == leaf.ActiveIndex;
            if (multi && index < leaf.PanelIds.Count - 1 && !isActive && !nextActive)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: tab.Right - 1f,
                        y: tab.Y + TabInset + 4f,
                        width: 1f,
                        height: MathF.Max(x: 0f, y: HeaderH - (TabInset * 2f) - 8f)
                    ),
                    color: _theme.Separator
                );
            }
        }

        // Header buttons (right): collapse + maximize. While maximized, collapse is hidden and the
        // maximize button reads as "restore".
        bool hover = _hoverHeaderLeafId == leaf.LeafId;
        bool maximized = _maximizedPanelId != null && leaf.PanelIds.Contains(_maximizedPanelId);
        PaintHdrBtn(
            paint: paint,
            r: MaximizeBtnRect(bounds),
            icon: maximized ? Icons.FullscreenExit : Icons.Fullscreen,
            headerHover: hover
        );
        if (!maximized)
        {
            PaintHdrBtn(
                paint: paint,
                r: CollapseBtnRect(bounds),
                icon: Icons.UnfoldLess,
                headerHover: hover
            );
        }

        // Active content (clipped). The body sits one elevation above the window so the shell reads
        // as layered surfaces; the panel content paints over this fill.
        var active = _panels.TryGetValue(key: leaf.ActivePanelId, value: out var ap) ? ap : null;
        float ch = Math.Max(val1: 0f, val2: bounds.Height - HeaderH);
        if (ch > 0f && active != null)
        {
            var contentRect = new Rect(
                x: bounds.X,
                y: bounds.Y + HeaderH,
                width: bounds.Width,
                height: ch
            );
            paint.AddClipStart(contentRect);
            paint.AddRect(bounds: contentRect, color: _theme.Panel);
            active.Content.Paint(paint);
            paint.AddClipEnd();
        }

        paint.AddBorder(bounds: bounds, color: _theme.Border);
    }

    /// <summary>A flat circular header action, the shape AdwButton gives a header-bar icon.</summary>
    private void PaintHdrBtn(PaintList paint, Rect r, string icon, bool headerHover)
    {
        if (AdwStyle.RowFill(theme: _theme, hovered: headerHover, pressed: false) is
            { A: > 0f } wash)
            paint.AddRect(bounds: r, color: wash, radius: r.Height * 0.5f);
        Icons.Draw(
            paint: paint,
            glyph: icon,
            box: r,
            color: headerHover ? _theme.OnBackground : AdwPalette.For(_theme).DimLabel,
            size: 14f
        );
    }

    /// <summary>
    ///     A collapsed region: only a thin strip with an expand affordance + the active panel's title.
    ///     Horizontal strip (collapsed inside a vertical split) keeps the title inline; a vertical strip
    ///     (collapsed inside a horizontal split) stacks the title down the bar. Clicking expands it.
    /// </summary>
    private void PaintCollapsedLeaf(DockLeaf leaf, Rect bounds, PaintList paint)
    {
        paint.AddRect(bounds: bounds, color: _theme.Toolbar);

        float fs = _theme.FontSizeCaption;
        bool hovered = _hoverHeaderLeafId == leaf.LeafId;
        var col = hovered ? _theme.OnBackground : AdwPalette.For(_theme).DimLabel;
        string title = _panels.TryGetValue(key: leaf.ActivePanelId, value: out var dp)
            ? dp.Title
            : leaf.ActivePanelId;

        if (bounds.Height <= HeaderH + 1f)
        {
            // Horizontal strip — chevron + inline title.
            Icons.Draw(
                paint: paint,
                glyph: Icons.UnfoldMore,
                box: new Rect(
                    x: bounds.X + 2f,
                    y: bounds.Y,
                    width: HeaderH,
                    height: HeaderH
                ),
                color: col,
                size: 14f
            );
            paint.AddText(
                text: title,
                baselineX: bounds.X + HeaderH + 2f,
                baselineY: bounds.Y + (HeaderH * 0.72f),
                color: col,
                fontSize: fs - 1f
            );
        }
        else
        {
            // Vertical strip — chevron on top, title stacked downward.
            Icons.Draw(
                paint: paint,
                glyph: Icons.UnfoldMore,
                box: new Rect(
                    x: bounds.X,
                    y: bounds.Y + 2f,
                    width: bounds.Width,
                    height: HeaderH
                ),
                color: col,
                size: 14f
            );
            float cy = bounds.Y + HeaderH + 4f;
            foreach (char glyph in title)
            {
                if (cy > bounds.Bottom - fs) break;
                paint.AddText(
                    text: glyph.ToString(),
                    baselineX: bounds.X + ((bounds.Width - (fs * 0.55f)) * 0.5f),
                    baselineY: cy + (fs * 0.8f),
                    color: col,
                    fontSize: fs - 1f
                );
                cy += fs + 1f;
            }
        }

        paint.AddBorder(bounds: bounds, color: _theme.Border);
    }

    private void PaintDragOverlay(PaintList paint)
    {
        // The floating title ghost belongs to the window whose pointer drives the drag; an
        // external hover (drag from another window) shows only the drop-zone previews.
        if (_draggingId is not null)
        {
            const float gW = 150f, gH = 26f;
            var ghost = new Rect(
                x: _dragCursor.X - (gW * 0.5f),
                y: _dragCursor.Y - (gH * 0.5f),
                width: gW,
                height: gH
            );
            paint.AddRect(bounds: ghost, color: _theme.Primary.WithAlpha(0.85f), radius: 4f);
            string title = _panels.TryGetValue(key: _draggingId, value: out var dp)
                ? dp.Title
                : _draggingId;
            paint.AddText(
                text: title,
                baselineX: ghost.X + 8f,
                baselineY: ghost.Y + (gH * 0.75f),
                color: _theme.OnPrimary,
                fontSize: _theme.FontSizeBody
            );
        }

        if (_hoverLeafId == null ||
            !_leafBounds.TryGetValue(key: _hoverLeafId, value: out var hb)) return;
        paint.AddRect(bounds: hb, color: _theme.Primary.WithAlpha(0.06f));

        if (_activeZone.HasValue)
        {
            var pr = DropPreviewRect(b: hb, z: _activeZone.Value);
            paint.AddRect(bounds: pr, color: _theme.Primary.WithAlpha(0.30f));
            paint.AddBorder(
                bounds: pr,
                color: _theme.Primary,
                radius: 0f,
                width: 2f
            );
        }

        PaintArrows(paint: paint, b: hb);
    }

    private static Rect DropPreviewRect(Rect b, DropZone z)
    {
        return z switch {
            DropZone.Left => new Rect(
                x: b.X,
                y: b.Y,
                width: b.Width * 0.33f,
                height: b.Height
            ),
            DropZone.Right => new Rect(
                x: b.X + (b.Width * 0.67f),
                y: b.Y,
                width: b.Width * 0.33f,
                height: b.Height
            ),
            DropZone.Top => new Rect(
                x: b.X,
                y: b.Y,
                width: b.Width,
                height: b.Height * 0.25f
            ),
            DropZone.Bottom => new Rect(
                x: b.X,
                y: b.Y + (b.Height * 0.75f),
                width: b.Width,
                height: b.Height * 0.25f
            ),
            _ => new Rect(
                x: b.X,
                y: b.Y,
                width: b.Width,
                height: HeaderH
            ), // Center → tab bar
        };
    }

    private void PaintArrows(PaintList paint, Rect b)
    {
        float cx = b.X + (b.Width * 0.5f) - (ArrowSz * 0.5f);
        float cy = b.Y + (b.Height * 0.5f) - (ArrowSz * 0.5f);
        const float m = 8f;

        if (b.Width > ArrowSz + (m * 2f))
        {
            PaintArrow(
                paint: paint,
                r: new Rect(
                    x: b.X + m,
                    y: cy,
                    width: ArrowSz,
                    height: ArrowSz
                ),
                icon: "◀",
                active: _activeZone == DropZone.Left
            );
            PaintArrow(
                paint: paint,
                r: new Rect(
                    x: b.Right - ArrowSz - m,
                    y: cy,
                    width: ArrowSz,
                    height: ArrowSz
                ),
                icon: "▶",
                active: _activeZone == DropZone.Right
            );
        }

        if (b.Height > ArrowSz + (m * 2f))
        {
            PaintArrow(
                paint: paint,
                r: new Rect(
                    x: cx,
                    y: b.Y + m,
                    width: ArrowSz,
                    height: ArrowSz
                ),
                icon: "▲",
                active: _activeZone == DropZone.Top
            );
            PaintArrow(
                paint: paint,
                r: new Rect(
                    x: cx,
                    y: b.Bottom - ArrowSz - m,
                    width: ArrowSz,
                    height: ArrowSz
                ),
                icon: "▼",
                active: _activeZone == DropZone.Bottom
            );
        }

        PaintArrow(
            paint: paint,
            r: new Rect(
                x: cx,
                y: cy,
                width: ArrowSz,
                height: ArrowSz
            ),
            icon: "+",
            active: _activeZone == DropZone.Center
        );
    }

    private void PaintArrow(PaintList paint, Rect r, string icon, bool active)
    {
        paint.AddRect(
            bounds: r,
            color: active ? _theme.Primary : _theme.Surface.WithAlpha(0.88f),
            radius: 5f
        );
        paint.AddBorder(
            bounds: r,
            color: active ? _theme.Primary : _theme.OnSurface.WithAlpha(0.25f),
            radius: 5f
        );
        paint.AddText(
            text: icon,
            baselineX: r.X + (r.Width * 0.22f),
            baselineY: r.Y + (r.Height * 0.76f),
            color: active ? _theme.OnPrimary : _theme.OnSurface.WithAlpha(0.75f),
            fontSize: _theme.FontSizeBody
        );
    }

    // ── Hit testing ─────────────────────────────────────────────────────────────

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;

        foreach (var d in _dividers)
        {
            if (d.Rect.Contains(px: point.X, py: point.Y))
                return this;
        }

        // Tab bars + header buttons + collapsed strips — handled by the dock itself.
        foreach ((string leafId, var b) in _leafBounds)
        {
            var leaf = _leaves.GetValueOrDefault(leafId);
            if (leaf is { Collapsed: true })
            {
                if (b.Contains(px: point.X, py: point.Y)) return this;
                continue;
            }

            var hdr = new Rect(
                x: b.X,
                y: b.Y,
                width: b.Width,
                height: HeaderH
            );
            if (hdr.Contains(px: point.X, py: point.Y)) return this;
        }

        if (_draggingId != null)
        {
            foreach (var (_, r) in _leafBounds)
            {
                if (r.Contains(px: point.X, py: point.Y))
                    return this;
            }
        }

        // Content → active panel of the containing leaf
        foreach ((string leafId, var b) in _leafBounds)
        {
            var content = new Rect(
                x: b.X,
                y: b.Y + HeaderH,
                width: b.Width,
                height: Math.Max(val1: 0f, val2: b.Height - HeaderH)
            );
            if (!content.Contains(px: point.X, py: point.Y)) continue;
            if (_leaves.TryGetValue(key: leafId, value: out var leaf) &&
                _panels.TryGetValue(key: leaf.ActivePanelId, value: out var p))
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
            if (!d.Rect.Contains(px: point.X, py: point.Y)) continue;
            _divDrag = d;
            _divDragOrigin = d.Split.Vertical ? point.Y : point.X;
            _divRatioAtDrag = d.Split.Ratio;
            _divSplitBounds = ComputeSplitBounds(d.Split);
            return;
        }

        foreach ((string leafId, var b) in _leafBounds)
        {
            var leaf = _leaves[leafId];

            // Collapsed strip: a click anywhere expands the region.
            if (leaf.Collapsed)
            {
                if (!b.Contains(px: point.X, py: point.Y)) continue;
                leaf.Collapsed = false;
                RequestLayout();
                LayoutChanged?.Invoke();
                return;
            }

            var hdr = new Rect(
                x: b.X,
                y: b.Y,
                width: b.Width,
                height: HeaderH
            );
            if (!hdr.Contains(px: point.X, py: point.Y)) continue;

            // Maximize / restore button.
            if (MaximizeBtnRect(b).Contains(px: point.X, py: point.Y))
            {
                ToggleMaximize(leaf.ActivePanelId);
                return;
            }

            // Collapse button — hidden (and ignored) while a panel is maximized.
            bool maximized = _maximizedPanelId != null && leaf.PanelIds.Contains(_maximizedPanelId);
            if (!maximized && CollapseBtnRect(b).Contains(px: point.X, py: point.Y))
            {
                leaf.Collapsed = true;
                RequestLayout();
                LayoutChanged?.Invoke();
                return;
            }

            foreach ((string panelId, var tab, int index) in TabRects(leaf: leaf, bounds: b))
            {
                if (!tab.Contains(px: point.X, py: point.Y)) continue;

                if (CloseRect(tab).Contains(px: point.X, py: point.Y))
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
            UpdateDividerRatio(d: d, pt: point);
            return;
        }

        if (_pendingDragId != null)
        {
            float dx = point.X - _pendingDragFrom.X;
            float dy = point.Y - _pendingDragFrom.Y;
            if (MathF.Sqrt((dx * dx) + (dy * dy)) >= DragStart)
            {
                _draggingId = _pendingDragId;
                _pendingDragId = null;
            }
        }

        if (_draggingId != null)
        {
            _dragCursor = point;
            ResolveDropZone(point);
            TabDragMoved?.Invoke(arg1: _draggingId, arg2: point);
            return;
        }

        _hoverHeaderLeafId = null;
        _hoverTabPanelId = null;
        foreach ((string leafId, var b) in _leafBounds)
        {
            var leaf = _leaves[leafId];
            if (leaf.Collapsed)
            {
                if (!b.Contains(px: point.X, py: point.Y)) continue;
                _hoverHeaderLeafId = leafId;
                break;
            }

            var hdr = new Rect(
                x: b.X,
                y: b.Y,
                width: b.Width,
                height: HeaderH
            );
            if (!hdr.Contains(px: point.X, py: point.Y)) continue;
            _hoverHeaderLeafId = leafId;
            foreach ((string panelId, var tab, int _) in TabRects(leaf: leaf, bounds: b))
            {
                if (tab.Contains(px: point.X, py: point.Y))
                {
                    _hoverTabPanelId = panelId;
                    break;
                }
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
            string dragged = _draggingId;
            string? leafId = _hoverLeafId;
            var zone = _activeZone;
            _draggingId = null;
            _hoverLeafId = null;
            _activeZone = null;

            if (leafId != null && zone.HasValue)
                CommitDrop(srcPanelId: dragged, dstLeafId: leafId, zone: zone.Value);
            else
                TabDragReleased?.Invoke(arg1: dragged, arg2: point);
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
        float pos = d.Split.Vertical ? pt.Y - b.Y : pt.X - b.X;
        float tot = d.Split.Vertical ? b.Height - DivW : b.Width - DivW;
        if (tot <= 0f) return;
        float minR = MinPanelPx / tot;
        d.Split.Ratio = Math.Clamp(
            value: pos / tot,
            min: Math.Min(val1: minR, val2: 0.05f),
            max: Math.Max(val1: 1f - minR, val2: 0.95f)
        );
        RequestLayout();
    }

    private Rect ComputeSplitBounds(DockSplit s)
    {
        float x1 = float.MaxValue, y1 = float.MaxValue, x2 = float.MinValue, y2 = float.MinValue;
        foreach (var leaf in CollectLeaves(s))
        {
            if (!_leafBounds.TryGetValue(key: leaf.LeafId, value: out var r)) continue;
            x1 = Math.Min(val1: x1, val2: r.X);
            y1 = Math.Min(val1: y1, val2: r.Y);
            x2 = Math.Max(val1: x2, val2: r.Right);
            y2 = Math.Max(val1: y2, val2: r.Bottom);
        }

        return x1 < float.MaxValue
            ? new Rect(
                x: x1,
                y: y1,
                width: x2 - x1,
                height: y2 - y1
            )
            : Rect.Zero;
    }

    private static IEnumerable<DockLeaf> CollectLeaves(DockNode n)
    {
        if (n is DockLeaf l) yield return l;
        else if (n is DockSplit s)
        {
            foreach (var x in CollectLeaves(s.First).Concat(CollectLeaves(s.Second)))
                yield return x;
        }
    }

    // ── Drop zone resolution ──────────────────────────────────────────────────

    private void ResolveDropZone(Offset pt)
    {
        _hoverLeafId = null;
        _activeZone = null;

        foreach ((string leafId, var r) in _leafBounds)
        {
            if (!r.Contains(px: pt.X, py: pt.Y)) continue;
            _hoverLeafId = leafId;

            // Over the tab bar (or central region) → join as a tab.
            if (pt.Y <= r.Y + HeaderH)
            {
                _activeZone = DropZone.Center;
                break;
            }

            float rx = (pt.X - r.X) / r.Width;
            float ry = (pt.Y - r.Y) / r.Height;
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
        if (!_leaves.TryGetValue(key: dstLeafId, value: out var dstLeaf)) return;
        var srcLeaf = FindLeaf(node: Root, panelId: srcPanelId);
        if (srcLeaf == null) return;

        // No-op: dropping the only tab of a leaf onto itself.
        if (srcLeaf == dstLeaf && (zone == DropZone.Center || dstLeaf.PanelIds.Count == 1)) return;

        Root = RemovePanel(node: Root, panelId: srcPanelId) ?? Root;
        InsertAtZone(panelId: srcPanelId, dstLeaf: dstLeaf, zone: zone);

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
                first: newLeaf,
                second: dstLeaf,
                vertical: false,
                ratio: 0.33f
            ),
            DropZone.Right => new DockSplit(
                first: dstLeaf,
                second: newLeaf,
                vertical: false,
                ratio: 0.67f
            ),
            DropZone.Top => new DockSplit(
                first: newLeaf,
                second: dstLeaf,
                vertical: true,
                ratio: 0.25f
            ),
            DropZone.Bottom => new DockSplit(
                first: dstLeaf,
                second: newLeaf,
                vertical: true,
                ratio: 0.75f
            ),
            _ => new DockSplit(first: dstLeaf, second: newLeaf),
        };
        Root = ReplaceNode(node: Root, target: dstLeaf, replacement: split);
    }

    private void ClosePanel(string panelId)
    {
        // Never close the very last visible panel.
        if (Root.LeafIds().Count() <= 1) return;
        Root = RemovePanel(node: Root, panelId: panelId) ?? Root;
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
                        leaf.ActiveIndex = Math.Max(val1: 0, val2: leaf.PanelIds.Count - 1);
                    return leaf.PanelIds.Count == 0 ? null : leaf;
                }

                return leaf;

            case DockSplit s:
                if (TreeContains(node: s.First, panelId: panelId))
                {
                    var nf = RemovePanel(node: s.First, panelId: panelId);
                    if (nf == null) return s.Second;
                    s.First = nf;
                    return s;
                }

                if (TreeContains(node: s.Second, panelId: panelId))
                {
                    var ns = RemovePanel(node: s.Second, panelId: panelId);
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
        if (ReferenceEquals(objA: node, objB: target)) return replacement;
        if (node is DockSplit s)
        {
            s.First = ReplaceNode(node: s.First, target: target, replacement: replacement);
            s.Second = ReplaceNode(node: s.Second, target: target, replacement: replacement);
        }

        return node;
    }

    private static DockLeaf? FindLeaf(DockNode node, string panelId)
    {
        return node switch {
            DockLeaf l => l.PanelIds.Contains(panelId) ? l : null,
            DockSplit s => FindLeaf(node: s.First, panelId: panelId) ??
                           FindLeaf(node: s.Second, panelId: panelId),
            _ => null,
        };
    }

    private static bool TreeContains(DockNode node, string panelId)
    {
        return node switch {
            DockLeaf l => l.PanelIds.Contains(panelId),
            DockSplit s => TreeContains(node: s.First, panelId: panelId) ||
                           TreeContains(node: s.Second, panelId: panelId),
            _ => false,
        };
    }

    private record struct DivEntry(DockSplit Split, Rect Rect);
}
