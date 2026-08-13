using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.Editor.History;
using Zigote.Editor.Scene;
using Zigote.Editor.Vfx;
using Zigote.Runtime.Scene;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Panels;

/// <summary>
///     Scene hierarchy tree panel. Supports expand/collapse, right-click context menu
///     (rename, duplicate, add child, delete), and colored kind badges.
/// </summary>
public sealed class HierarchyPanel : Widget
{
    private const float RowH = 26f;

    // SDL scancodes for keyboard navigation while the panel is focused.
    private const uint ScReturn = 40;
    private const uint ScBackspace = 42;
    private const uint ScF2 = 59;
    private const uint ScDelete = 76;
    private const uint ScRight = 79;
    private const uint ScLeft = 80;
    private const uint ScDown = 81;
    private const uint ScUp = 82;

    private readonly HashSet<SceneNode> _collapsed = [];
    private readonly DragState _drag = new();
    private readonly List<NodeRow> _rows = [];
    private readonly EditorState _state;
    private readonly ThemeData _theme;

    /// <summary>
    ///     Raised when the selected node should be scrolled into view — <c>(top, height)</c> in the
    ///     panel's own content space (row 0 = 0). The shell wires this to the wrapping
    ///     <see cref="ScrollView.EnsureVisible" />.
    /// </summary>
    public Action<float, float>? OnRevealRequested;

    private SceneNode? _lastClickedNode;
    private Size _size;
    private int _structuralSig;

    public HierarchyPanel(EditorState state, ThemeData theme)
    {
        _state = state;
        _theme = theme;
        _state.SceneChanged += Rebuild;
        _state.SelectionSignal.Changed += _ => OnSelectionChanged();
        Rebuild();
    }

    // The panel holds keyboard focus (requested on row click) so arrow keys navigate the tree.
    public override bool Focusable => true;

    // ── Selection follow (reveal into view) ─────────────────────────────────────

    private void OnSelectionChanged()
    {
        // A selection made elsewhere (viewport pick, keyboard) may sit inside a collapsed branch —
        // expand its ancestors so the row exists, rebuild, then scroll it into view.
        ExpandAncestorsOf(_state.Selected);
        Rebuild();
        RevealSelected();
    }

    private void ExpandAncestorsOf(SceneNode? node)
    {
        for (var p = node?.Parent; p != null && p != _state.Scene.Root; p = p.Parent)
            _collapsed.Remove(p);
    }

    private void RevealSelected()
    {
        if (OnRevealRequested is null || _state.Selected is not { } sel) return;
        int idx = _rows.FindIndex(r => r.Node == sel);
        if (idx >= 0) OnRevealRequested(arg1: idx * RowH, arg2: RowH);
    }

    // ── Keyboard navigation (panel focused) ─────────────────────────────────────

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        if (!down) return;

        switch (scancode)
        {
            case ScUp:
                MoveSelection(-1);
                return;
            case ScDown:
                MoveSelection(1);
                return;
            case ScLeft:
                CollapseOrSelectParent();
                return;
            case ScRight:
                ExpandOrSelectChild();
                return;
            case ScF2:
            case ScReturn:
                if (_state.Selected is { } toRename) ShowRenameDialog(toRename);
                return;
            case ScDelete:
            case ScBackspace:
                _state.DeleteSelected();
                return;
        }

        if (mods.HasFlag(Modifiers.Ctrl) && char.ToLower(keyChar) == 'd')
            _state.DuplicateSelected();
    }

    private void MoveSelection(int dir)
    {
        if (_rows.Count == 0) return;
        int cur = _state.Selected is { } s ? _rows.FindIndex(r => r.Node == s) : -1;
        int next = cur < 0
            ? dir > 0 ? 0 : _rows.Count - 1
            : Math.Clamp(value: cur + dir, min: 0, max: _rows.Count - 1);
        _lastClickedNode = _rows[next].Node;
        _state.Select(_rows[next].Node); // fires SelectionSignal → OnSelectionChanged reveals it
    }

    private void CollapseOrSelectParent()
    {
        if (_state.Selected is not { } sel) return;
        if (sel.Children.Count > 0 && !_collapsed.Contains(sel))
        {
            _collapsed.Add(sel);
            Rebuild();
        }
        else if (sel.Parent is { } p && p != _state.Scene.Root) _state.Select(p);
    }

    private void ExpandOrSelectChild()
    {
        if (_state.Selected is not { } sel || sel.Children.Count == 0) return;
        if (_collapsed.Remove(sel))
            Rebuild();
        else
        {
            var firstVisible = sel.Children.FirstOrDefault(c => !c.IsHidden);
            if (firstVisible != null) _state.Select(firstVisible);
        }
    }

    private void Rebuild()
    {
        // Skip the row rebuild when the visible tree structure is unchanged. Play mode fires
        // SceneChanged every frame (physics/script transform ticks), but the hierarchy only shows
        // identity/name/kind/visibility — all of which NodeRow reads live in Paint. Recreating the
        // NodeRows each frame without a relayout would paint un-laid-out rows (Bounds = 0) until the
        // next layout pass intermittently catches up, so the panel flickers and is unusable in play.
        // Only a structural change (add/remove/reorder/reparent/expand/collapse/hide) needs new rows.
        int sig = ComputeStructuralSignature();
        if (_rows.Count > 0 && sig == _structuralSig)
        {
            MarkNeedsPaint(); // selection / name / eye repaint without rebuilding the row widgets
            return;
        }

        _structuralSig = sig;
        _rows.Clear();
        AddRows(node: _state.Scene.Root, depth: 0);
        MarkNeedsLayout(); // new rows must be measured + laid out before paint (play rebuilds aren't event-driven)
    }

    private void AddRows(SceneNode node, int depth)
    {
        if (node.IsHidden) return;

        if (node != _state.Scene.Root)
        {
            bool isExpanded = !_collapsed.Contains(node);
            _rows.Add(
                new NodeRow(
                    node: node,
                    depth: depth,
                    isExpanded: isExpanded,
                    state: _state,
                    theme: _theme,
                    toggleExpand: ToggleExpand,
                    drag: _drag,
                    getNodeAtY: GetNodeAtY,
                    onNodeClick: HandleNodeClick,
                    renameNode: ShowRenameDialog
                )
            );
            if (!isExpanded) return;
        }

        int childDepth = depth + (node == _state.Scene.Root ? 0 : 1);
        foreach (var child in node.Children)
            AddRows(node: child, depth: childDepth);
    }

    /// <summary>
    ///     A hash of the visible row set — node identity + depth + collapsed state, in display order.
    ///     Changes exactly when the rows would differ; lets <see cref="Rebuild" /> skip recreating the
    ///     row widgets on the per-frame SceneChanged ticks play mode emits (those only mutate transforms,
    ///     which the hierarchy does not display).
    /// </summary>
    private int ComputeStructuralSignature()
    {
        var hash = new HashCode();
        AccumulateSignature(
            hash: ref hash,
            node: _state.Scene.Root,
            depth: 0,
            isRoot: true
        );
        return hash.ToHashCode();
    }

    private void AccumulateSignature(ref HashCode hash, SceneNode node, int depth, bool isRoot)
    {
        if (node.IsHidden) return;

        if (!isRoot)
        {
            bool collapsed = _collapsed.Contains(node);
            hash.Add(node.Id);
            hash.Add(depth);
            hash.Add(collapsed);
            if (collapsed) return; // collapsed → descendants aren't rows (mirror AddRows)
        }

        int childDepth = depth + (isRoot ? 0 : 1);
        foreach (var child in node.Children)
        {
            AccumulateSignature(
                hash: ref hash,
                node: child,
                depth: childDepth,
                isRoot: false
            );
        }
    }

    private void ToggleExpand(SceneNode node)
    {
        if (!_collapsed.Remove(node))
            _collapsed.Add(node);
        Rebuild();
    }

    private SceneNode? GetNodeAtY(float y)
    {
        int idx = (int)((y - Bounds.Y) / RowH);
        return idx >= 0 && idx < _rows.Count ? _rows[idx].Node : null;
    }

    private void HandleNodeClick(SceneNode node, bool ctrl, bool shift)
    {
        App.Active?.RequestFocus(this); // claim keyboard focus so arrow keys navigate the tree
        if (ctrl)
        {
            _state.AddToSelection(node);
            _lastClickedNode = node;
        }
        else if (shift && _lastClickedNode != null)
        {
            int fromIdx = _rows.FindIndex(r => r.Node == _lastClickedNode);
            int toIdx = _rows.FindIndex(r => r.Node == node);
            if (fromIdx >= 0 && toIdx >= 0)
            {
                int lo = Math.Min(val1: fromIdx, val2: toIdx);
                int hi = Math.Max(val1: fromIdx, val2: toIdx);
                _state.SetSelection(_rows.Skip(lo).Take(hi - lo + 1).Select(r => r.Node));
            }
            else
            {
                _state.Select(node);
                _lastClickedNode = node;
            }
        }
        else
        {
            _state.Select(node);
            _lastClickedNode = node;
        }
    }

    private void ShowRenameDialog(SceneNode node)
    {
        var entry = new AdwEntry { Text = node.Name };
        var dlg = new AdwAlertDialog("Rename Node") {
            ExtraChild = entry,
            DefaultResponse = "rename",
            CloseResponse = "cancel",
        };
        dlg.AddResponse(id: "cancel", label: "Cancel");
        dlg.AddResponse(id: "rename", label: "Rename", appearance: AdwResponseAppearance.Suggested);
        dlg.OnResponse = id =>
        {
            string trimmed = entry.Text.Trim();
            if (id != "rename" || trimmed.Length == 0 || trimmed == node.Name) return;
            _state.History.Execute(
                new ChangePropertyCommand<string>(
                    state: _state,
                    oldValue: node.Name,
                    newValue: trimmed,
                    setter: v => node.Name = v
                )
            );
        };
        dlg.Show();
    }

    public override Size Measure(Constraints c)
    {
        float h = RowH * _rows.Count;
        _size = c.Constrain(new Size(width: c.MaxWidth, height: h));
        foreach (var r in _rows)
            r.Measure(new Constraints(maxWidth: _size.Width, maxHeight: RowH));
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
        float y = origin.Y;
        foreach (var r in _rows)
        {
            r.Layout(new Offset(x: origin.X, y: y));
            y += RowH;
        }
    }

    public override void Paint(PaintList paint)
    {
        foreach (var r in _rows) r.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        foreach (var r in _rows)
        {
            var hit = r.HitTest(point);
            if (hit is not null) return hit;
        }

        return null;
    }

    private sealed class DragState
    {
        public bool Active;
        public SceneNode? DropTarget;
        public SceneNode? Source;
        private float _startX, _startY;

        public void Begin(SceneNode source, float x, float y)
        {
            Source = source;
            _startX = x;
            _startY = y;
            Active = false;
            DropTarget = null;
        }

        public void Move(float x, float y, Func<float, SceneNode?> getAt)
        {
            float dx = x - _startX;
            float dy = y - _startY;
            if (!Active && (dx * dx) + (dy * dy) > 36f) Active = true;
            if (Active)
            {
                var candidate = getAt(y);
                DropTarget = IsValidDrop(src: Source!, target: candidate) ? candidate : null;
            }
        }

        public void Clear()
        {
            Source = null;
            DropTarget = null;
            Active = false;
        }

        private static bool IsValidDrop(SceneNode src, SceneNode? target)
        {
            if (target is null || target == src) return false;
            // Disallow dropping onto a descendant (would create a cycle)
            var cur = target;
            while (cur != null)
            {
                if (cur == src) return false;
                cur = cur.Parent;
            }

            return true;
        }
    }

    // ── Inner row widget ──────────────────────────────────────────────────────

    private sealed class NodeRow(
        SceneNode node,
        int depth,
        bool isExpanded,
        EditorState state,
        ThemeData theme,
        Action<SceneNode> toggleExpand,
        DragState drag,
        Func<float, SceneNode?> getNodeAtY,
        Action<SceneNode, bool, bool> onNodeClick,
        Action<SceneNode> renameNode)
        : Widget
    {
        private const float RowH = 26f;
        private bool _hovered;
        private Size _size;

        public SceneNode Node => node;
        private bool HasChildren => node.Children.Count > 0;

        private Color KindColor()
        {
            return node.Kind switch {
                NodeKind.Mesh => new Color(r: 0.4f, g: 0.75f, b: 1f),
                NodeKind.Light => new Color(r: 1f, g: 0.88f, b: 0.3f),
                NodeKind.Camera => new Color(r: 0.35f, g: 0.9f, b: 0.5f),
                NodeKind.Script => new Color(r: 0.75f, g: 0.45f, b: 1f),
                NodeKind.ReflectionProbe => new Color(r: 0.4f, g: 0.95f, b: 0.9f),
                NodeKind.AudioSource => new Color(r: 1f, g: 0.6f, b: 0.3f),
                NodeKind.VfxEmitter => new Color(r: 0.85f, g: 0.5f, b: 1f),
                NodeKind.Sprite => new Color(r: 0.5f, g: 0.85f, b: 1f),
                NodeKind.Tilemap => new Color(r: 0.6f, g: 0.9f, b: 0.7f),
                _ => theme.Hint.WithAlpha(0.6f),
            };
        }

        private string KindGlyph()
        {
            return node.Kind switch {
                NodeKind.Mesh => Icons.Cube,
                NodeKind.Light => Icons.Sun,
                NodeKind.Camera => Icons.Camera,
                NodeKind.Script => Icons.Bolt,
                NodeKind.ReflectionProbe => Icons.Water,
                NodeKind.AudioSource => Icons.Audio,
                NodeKind.VfxEmitter => Icons.LightMode,
                NodeKind.Sprite => Icons.Image,
                NodeKind.Tilemap => Icons.Grid,
                _ => Icons.Category,
            };
        }

        public override Size Measure(Constraints c)
        {
            _size = c.Constrain(new Size(width: c.MaxWidth, height: RowH));
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
        }

        public override void Paint(PaintList paint)
        {
            bool isPrimary = state.Selected == node;
            bool isAnySelected = state.SelectedNodes.Contains(node);
            bool isDropTarget = drag.Active && drag.DropTarget == node;
            // Adwaita list selection is a translucent accent wash (SelectionTint), not a filled
            // accent bar with inverted text; hover is the same neutral wash every other row-like
            // surface uses, so the hierarchy, the asset list and the menus all feel identical.
            var bg = isDropTarget ? theme.SelectionTint.WithAlpha(theme.SelectionTint.A * 1.4f)
                : isPrimary ? theme.SelectionTint
                : isAnySelected ? theme.SelectionTint.WithAlpha(theme.SelectionTint.A * 0.5f)
                : AdwStyle.RowFill(theme: theme, hovered: _hovered, pressed: false);

            // macOS-style inset rounded selection/hover pill (not a full-bleed bar).
            if (bg.A > 0)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: Bounds.X + 4f,
                        y: Bounds.Y + 1f,
                        width: Bounds.Width - 8f,
                        height: Bounds.Height - 2f
                    ),
                    color: bg,
                    radius: 6f
                );
            }

            // Drop target indicator line at top of row
            if (isDropTarget)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: Bounds.X + 4f,
                        y: Bounds.Y,
                        width: Bounds.Width - 8f,
                        height: 2f
                    ),
                    color: theme.Primary,
                    radius: 1f
                );
            }

            float indent = 14f + (depth * 14f);
            float fs = theme.FontSizeCaption;
            var fg = isPrimary ? theme.OnSurface
                : isAnySelected ? theme.OnSurface.WithAlpha(0.85f)
                : !node.Visible ? theme.TextMuted.WithAlpha(0.55f)
                : theme.OnSurface;
            float textY = Bounds.Y + ((RowH - fs) / 2f) + (fs * 0.8f);

            // Disclosure chevron (Material) — only when the node has children.
            if (HasChildren)
            {
                string chevron = isExpanded ? Icons.ChevronDown : Icons.ChevronRight;
                Icons.Draw(
                    paint: paint,
                    glyph: chevron,
                    box: new Rect(
                        x: Bounds.X + indent,
                        y: Bounds.Y,
                        width: 14f,
                        height: RowH
                    ),
                    color: theme.TextMuted,
                    size: 14f
                );
            }

            // Type icon, kind-coloured (dimmed when the node is hidden).
            float iconX = Bounds.X + indent + 16f;
            var iconTint = node.Visible ? KindColor() : KindColor().WithAlpha(0.45f);
            Icons.Draw(
                paint: paint,
                glyph: KindGlyph(),
                box: new Rect(
                    x: iconX,
                    y: Bounds.Y,
                    width: 16f,
                    height: RowH
                ),
                color: iconTint,
                size: 15f
            );

            // Node name.
            paint.AddText(
                text: node.Name,
                baselineX: iconX + 20f,
                baselineY: textY,
                color: fg,
                fontSize: fs
            );

            // Visibility toggle (Material eye), right-aligned.
            var eyeColor = node.Visible
                ? theme.TextMuted.WithAlpha(0.7f)
                : theme.TextMuted.WithAlpha(0.3f);
            Icons.Draw(
                paint: paint,
                glyph: node.Visible ? Icons.Visibility : Icons.VisibilityOff,
                box: new Rect(
                    x: Bounds.Right - 22f,
                    y: Bounds.Y,
                    width: 16f,
                    height: RowH
                ),
                color: eyeColor,
                size: 15f
            );
        }

        public override void OnPointerEnter() => _hovered = true;

        public override void OnPointerExit() => _hovered = false;

        public override void OnPointerDown(Offset point)
        {
            // Eye icon click — match the drawn glyph rect (Right-22 .. Right-6); the last 6px belong
            // to the row (selection/drag) like the rest.
            if (point.X >= Bounds.Right - 22f && point.X <= Bounds.Right - 6f)
            {
                node.Visible = !node.Visible;
                state.NotifySceneChanged();
                return;
            }

            float indent = 14f + (depth * 14f);
            if (HasChildren && point.X >= Bounds.X + indent && point.X <= Bounds.X + indent + 14f)
            {
                toggleExpand(node);
                return;
            }

            var mods = App.Active?.CurrentModifiers ?? Modifiers.None;
            onNodeClick(
                arg1: node,
                arg2: mods.HasFlag(Modifiers.Ctrl),
                arg3: mods.HasFlag(Modifiers.Shift)
            );

            // Begin potential drag (only for non-root nodes that have a parent)
            if (node.Parent != null)
                drag.Begin(source: node, x: point.X, y: point.Y);
        }

        public override void OnPointerMove(Offset point)
        {
            if (drag.Source == node)
                drag.Move(x: point.X, y: point.Y, getAt: getNodeAtY);
        }

        public override void OnPointerUp(Offset point)
        {
            if (drag.Active && drag.Source == node && drag.DropTarget != null)
            {
                state.History.Execute(
                    new ReparentNodeCommand(state: state, node: node, newParent: drag.DropTarget)
                );
            }

            drag.Clear();
        }

        public override void OnRightClick(Offset point)
        {
            state.Select(node);
            BuildContextMenu().ShowAt(point);
        }

        private ContextMenu BuildContextMenu()
        {
            return new ContextMenu(
                new ContextMenuItem(
                    Label: "Add Empty Child",
                    OnSelect: () => state.History.Execute(
                        new AddNodeCommand(state: state, parent: node, node: new SceneNode("Node"))
                    )
                ),
                new ContextMenuItem(
                    Label: "Add Mesh Child",
                    OnSelect: () => state.History.Execute(
                        new AddNodeCommand(
                            state: state,
                            parent: node,
                            node: new SceneNode(name: "Mesh", kind: NodeKind.Mesh)
                        )
                    )
                ),
                new ContextMenuItem(
                    Label: "Add Light",
                    OnSelect: () => state.History.Execute(
                        new AddNodeCommand(
                            state: state,
                            parent: node,
                            node: new SceneNode(name: "Light", kind: NodeKind.Light)
                        )
                    )
                ),
                new ContextMenuItem(
                    Label: "Add Reflection Probe",
                    OnSelect: () => state.History.Execute(
                        new AddNodeCommand(
                            state: state,
                            parent: node,
                            node: new SceneNode(
                                name: "Reflection Probe",
                                kind: NodeKind.ReflectionProbe
                            )
                        )
                    )
                ),
                new ContextMenuItem(
                    Label: "Add Audio Source",
                    OnSelect: () => state.History.Execute(
                        new AddNodeCommand(
                            state: state,
                            parent: node,
                            node: new SceneNode(name: "Audio Source", kind: NodeKind.AudioSource)
                        )
                    )
                ),
                new ContextMenuItem(
                    Label: "Add VFX Emitter",
                    OnSelect: () =>
                    {
                        var vfx = new SceneNode(name: "VFX", kind: NodeKind.VfxEmitter);
                        VfxNodeEditor.SeedDefault(vfx);
                        state.History.Execute(
                            new AddNodeCommand(state: state, parent: node, node: vfx)
                        );
                    }
                ),
                new ContextMenuItem(
                    Label: "Add Sprite",
                    OnSelect: () => state.History.Execute(
                        new AddNodeCommand(
                            state: state,
                            parent: node,
                            node: new SceneNode(name: "Sprite", kind: NodeKind.Sprite)
                        )
                    )
                ),
                new ContextMenuItem(
                    Label: "Add Tilemap",
                    OnSelect: () =>
                    {
                        // Seed one layer so the palette has somewhere to paint immediately.
                        var map = new SceneNode(name: "Tilemap", kind: NodeKind.Tilemap);
                        map.TilemapLayers.Add(new TilemapLayer { Name = "Ground" });
                        state.History.Execute(
                            new AddNodeCommand(state: state, parent: node, node: map)
                        );
                    }
                ),
                new ContextMenuItem(Label: "", OnSelect: null, Separator: true),
                new ContextMenuItem(Label: "Rename...", OnSelect: () => renameNode(node)),
                new ContextMenuItem(Label: "Duplicate", OnSelect: DuplicateNode),
                new ContextMenuItem(
                    Label: "Create Prefab",
                    OnSelect: () =>
                        state.History.Execute(new CreatePrefabCommand(state: state, source: node))
                ),
                new ContextMenuItem(Label: "", OnSelect: null, Separator: true),
                new ContextMenuItem(
                    Label: "Delete",
                    OnSelect: () =>
                    {
                        if (node.Parent != null)
                            state.History.Execute(new DeleteNodeCommand(state: state, node: node));
                    }
                )
            );
        }

        private void DuplicateNode()
        {
            var parent = node.Parent ?? state.Scene.Root;
            var copy = node.DeepClone(node.Name + " Copy");
            state.History.Execute(new AddNodeCommand(state: state, parent: parent, node: copy));
        }
    }
}
