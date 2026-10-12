using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.Graphs.Commands;
using Zigote.Graphs.Core;
using Zigote.Graphs.Registry;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using AppInstance = Zigote.UI.Host.App;

namespace Zigote.Graphs.Editor;

/// <summary>
///     Interactive node graph canvas.
///     Handles pan, zoom, node drag, edge creation/deletion, selection, and box selection.
///     All graph mutations go through <see cref="GraphEditorState.Commands" />.
/// </summary>
public sealed class GraphCanvas : Widget
{
    // ── Layout constants ──────────────────────────────────────────────────────

    private const float NodeHeaderH = 30f;
    private const float NodePinH = 24f;
    private const float NodePinRadius = 5.5f;
    private const float NodePropH = 28f; // height per inline property row
    private const float NodePropSepH = 1f; // separator between pins and properties
    private const float NodeMinWidth = 230f;
    private const float NodePadX = 10f;
    private const float GridSpacing = 24f;
    private const float PreviewPad = 8f;
    private const float NodeResizeGrip = 10f; // world units — grab area size for resize handle

    // LOD zoom thresholds
    private const float ZoomLodFull = 0.45f; // below: skip property rows and pin labels
    private const float ZoomLodMin = 0.22f; // below: skip pins too; only header + name

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly GraphEditorState _state;
    private readonly ThemeData _theme;
    private float _boxStartX, _boxStartY, _boxEndX, _boxEndY;

    // While dragging a wire: the pin currently under the cursor + whether it's a legal target, and the
    // nearest compatible pin the wire snaps to (green beacons mark all valid targets, red marks invalid).
    private GraphPinEndpoint? _dragHoverPin;
    private bool _dragHoverValid;

    // Node drag
    private Guid? _dragNodeId;
    private float _dragNodeStartX, _dragNodeStartY;
    private float _dragOffsetX, _dragOffsetY;

    // Edge creation: dragging from a pin
    private GraphPinEndpoint? _dragPin;
    private PinDirection _dragPinDir;
    private float _dragPinWorldX, _dragPinWorldY;
    private GraphPinEndpoint? _dragSnapPin;
    private float _editDragStartX, _editDragStartFloat;
    private PropEditTarget? _editingProp;
    private GraphValue _editOriginalValue = GraphValue.Null();
    private InlineTextField? _textEditor;
    private string? _textError;
    private PropEditTarget? _textTarget;
    private Offset _editPress;
    private bool _editMoved;

    // Edge hover
    private Guid? _hoveredEdgeId;

    // Box selection
    private bool _isBoxSelecting;

    // RMB pan + context menu
    private bool _isPanning;
    private float _mousePinWorldX, _mousePinWorldY;

    // Mouse tracking (screen coords) — used for zoom-toward-cursor
    private float _mouseX, _mouseY;
    private Guid? _outputNodeId;
    private Size _outputPreviewMeasuredSize;

    // Output node live preview — the compiled Widget rendered directly on the canvas.
    private Widget? _outputPreviewWidget;
    private bool _panDragged; // true if RMB actually moved enough to count as a pan
    private float _panMouseStartX, _panMouseStartY;
    private float _panMouseX, _panMouseY;

    // Pan/zoom
    private float _panX, _panY;

    // Node resize (2-axis)
    private Guid? _resizeNodeId;
    private float _resizeStartWorldX, _resizeStartWidth;
    private float _resizeStartWorldY, _resizeStartHeight;
    private Size _size;
    private float _zoom = 1f;
    private Modifiers _modifiers;
    private string? _connectionError;
    private readonly HashSet<Guid> _invalidEdges = [];

    // Marquee: what was selected before it started (kept when Shift/Ctrl extends the selection).
    private Guid[] _boxBase = [];

    // A plain press on one node of a multi-selection keeps the group for a drag; if no drag
    // follows, the release narrows the selection to that node.
    private Guid? _collapseOnRelease;

    // The existing cable being re-plugged: picked up by dragging from its input end.
    private GraphEdge? _rerouteEdge;

    // While one unconnected node is dragged over a cable: where releasing would splice it in.
    private (GraphEdge Edge, string In, string Out)? _insert;

    /// <summary>Stands in for <c>App.CurrentScrollPrecise</c> when the canvas has no app (tests).</summary>
    public bool PreciseScroll { get; set; }

    // Tick of the last scroll event the native layer marked as precise (see OnScroll).
    private long _trackpadTick = -1000; // "long ago", without overflowing the subtraction

    // Copied nodes. Process-wide so a copy in one graph pastes into another of the same domain.
    private static Snapshot? _clipboard;
    private int _pasteCount;
    private readonly Dictionary<Guid, (float X, float Y)> _dragOrigins = [];
    public GraphPinEndpoint? SearchConnection { get; private set; }
    public Offset SearchAnchor { get; private set; }


    public GraphCanvas(GraphEditorState state, ThemeData theme)
    {
        _state = state;
        _theme = theme;

        _state.CompileChanged += OnCompileChanged;
        _panX = state.Graph.EditorData.ViewOffsetX;
        _panY = state.Graph.EditorData.ViewOffsetY;
        _zoom = Math.Clamp(state.Graph.EditorData.Zoom, 0.15f, 4f);
        _state.GraphChanged += () => { RefreshConnectionValidity(); InvalidateView(); };
        RefreshConnectionValidity();
        _state.SelectionChanged += MarkNeedsPaint;
        OnCompileChanged(); // seed from any compile already done
    }

    /// <summary>
    ///     Whether the bottom-right minimap overview is drawn. Off by default; toggled from the
    ///     toolbar.
    /// </summary>
    public bool MinimapVisible { get; set; }

    public override bool Focusable => true;

    /// <summary>Arrow keys nudge the selection, so they never move window focus.</summary>
    public override bool HandlesDirectionalKeys => true;

    // Pointer gestures read the live modifier state. _modifiers only follows key events delivered
    // while the canvas had focus, so Shift pressed over another control used to be missed and a
    // release missed the same way left it stuck on.
    private Modifiers Mods => Owner is { } app ? app.CurrentModifiers : _modifiers;

    private bool Additive => Mods.HasFlag(Modifiers.Shift) || Mods.HasCommand();

    private float NodeWidth(NodeLayoutData layout) =>
        MathF.Max(x: layout.Width > 0 ? layout.Width : NodeMinWidth, y: NodeMinWidth);

    public event Action<float, float>? SearchRequested;

    private void OnCompileChanged()
    {
        var cr = _state.LastCompileResult;
        if (cr?.CompiledArtifact is Widget w)
        {
            _outputPreviewWidget = w;
            // Identify the terminal node (no outputs, has inputs) as the preview anchor.
            _outputNodeId = _state.Graph.Nodes
                .FirstOrDefault(n =>
                    {
                        var d = _state.Registry.GetNodeDefinition(n.DefinitionId);
                        return d is not null && d.Outputs.Count == 0 && d.Inputs.Count > 0;
                    }
                )?.Id;
        }
        else
        {
            _outputPreviewWidget = null;
            _outputNodeId = null;
        }
    }

    // ── Coordinate helpers ────────────────────────────────────────────────────

    private float WorldToScreenX(float wx) => (wx * _zoom) + _panX + Bounds.X;

    private float WorldToScreenY(float wy) => (wy * _zoom) + _panY + Bounds.Y;

    private float ScreenToWorldX(float sx) => (sx - Bounds.X - _panX) / _zoom;

    private float ScreenToWorldY(float sy) => (sy - Bounds.Y - _panY) / _zoom;

    private float Scale(float v) => v * _zoom;

    // ── Measure / Layout ──────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
        return _size;
    }

    private static void DrawLine(PaintList paint, Offset a, Offset b, Color color, float width) =>
        paint.AddBezier(a.X, a.Y, a.X, a.Y, b.X, b.Y, b.X, b.Y, color, width);

    /// <summary>
    ///     Fit the view to the graph when the canvas first gets a size, and again on resize until
    ///     the user pans or zooms. Off by default: a host that positions the view itself keeps it.
    /// </summary>
    public bool AutoFrame { get; set; }

    private Size _framedSize;
    private float _autoPanX, _autoPanY, _autoZoom;

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );

        // Open on the graph, not on wherever world (0,0) happens to be: fit the first time the
        // canvas has a real size, and keep fitting as it resizes (a docked tab being maximized)
        // until the user pans or zooms — after that the view is theirs.
        bool untouched = _panX == _autoPanX && _panY == _autoPanY && _zoom == _autoZoom;
        if (AutoFrame && _size is { Width: > 0f, Height: > 0f } && _size != _framedSize &&
            (_framedSize == default || untouched))
        {
            _framedSize = _size;
            FrameAll();
            (_autoPanX, _autoPanY, _autoZoom) = (_panX, _panY, _zoom);
        }

        LayoutTextEditor();
        if (_outputPreviewWidget is null || !_outputNodeId.HasValue ||
            !_state.Graph.EditorData.NodeLayouts.TryGetValue(
                key: _outputNodeId.Value,
                value: out var outLayout
            ) || outLayout.IsCollapsed) return;
        var outNode = _state.Graph.FindNode(_outputNodeId.Value);
        var def = outNode is null ? null : _state.Registry.GetNodeDefinition(outNode.DefinitionId);
        float totalBodyH = EffectiveHeight(layout: outLayout, def: def);

        float ww = MathF.Max(
            x: outLayout.Width > 0 ? outLayout.Width : NodeMinWidth,
            y: NodeMinWidth
        );
        // Constrain preview to the node card width so it scales with zoom.
        float availW = MathF.Max(x: Scale(ww) - (PreviewPad * 2f), y: 40f);
        float availH = availW * 0.75f; // 4:3 aspect ratio

        _outputPreviewMeasuredSize = _outputPreviewWidget.Measure(
            Constraints.Tight(width: availW, height: availH)
        );

        float previewX = WorldToScreenX(outLayout.X) + PreviewPad;
        float previewY = WorldToScreenY(outLayout.Y) + Scale(totalBodyH) + PreviewPad;

        _outputPreviewWidget.Layout(new Offset(x: previewX, y: previewY));
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        paint.AddRect(bounds: Bounds, color: _theme.GraphBackground);
        paint.AddClipStart(Bounds);

        DrawGrid(paint);
        DrawEdges(paint);
        if (_dragPin.HasValue) DrawDragEdge(paint);
        else if (SearchConnection is { } pending && GetPinWorldPos(pending) is { } pos)
        {
            bool output = GraphConnections.Resolve(_state.Graph, _state.Registry, pending)?.Direction == PinDirection.Output;
            float x = WorldToScreenX(pos.wx), y = WorldToScreenY(pos.wy);
            DrawBezier(paint, output ? x : SearchAnchor.X, output ? y : SearchAnchor.Y,
                output ? SearchAnchor.X : x, output ? SearchAnchor.Y : y, _theme.OnSurface.WithAlpha(0.55f), 2f);
        }
        DrawNodes(paint);
        // Green halos on every compatible target pin (and a red ring on an invalid hovered pin) while
        // dragging a wire — shows valid/invalid connections live.
        if (_dragPin.HasValue) DrawConnectionBeacons(paint);
        // Render the output node's live widget preview on top of the node card.
        if (_outputNodeId is { } outputId && _state.Graph.EditorData.NodeLayouts.TryGetValue(outputId, out var outputLayout) && !outputLayout.IsCollapsed)
            _outputPreviewWidget?.Paint(paint);
        if (_isBoxSelecting) DrawBoxSelection(paint);
        _textEditor?.Paint(paint);

        if (_state.Graph.Nodes.Count == 0)
        {
            const string hint = "Empty graph — press Shift+A or Space to add a node";
            float fs = _theme.FontSizeBody;
            paint.AddText(hint,
                Bounds.X + (Bounds.Width - Zigote.UI.TextShaping.TextMeasure.Width(hint, fs)) / 2f,
                Bounds.Y + Bounds.Height / 2f, _theme.TextMuted, fs);
        }

        paint.AddClipEnd();
        DrawHints(paint);
        DrawMinimap(paint);
    }

    // ── Minimap ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     A bottom-right overview of the whole graph: every node as a tiny coloured rect plus a
    ///     framed rectangle for the currently-visible area. View-only (pan with RMB on the canvas).
    /// </summary>
    private void DrawMinimap(PaintList paint)
    {
        if (!MinimapVisible) return;
        var layouts = _state.Graph.EditorData.NodeLayouts;
        if (layouts.Count == 0) return;

        // World-space bounding box over all nodes + the visible viewport, so the frame always shows.
        float minX = float.MaxValue,
            minY = float.MaxValue,
            maxX = float.MinValue,
            maxY = float.MinValue;
        foreach (var node in _state.Graph.Nodes)
        {
            if (!layouts.TryGetValue(key: node.Id, value: out var l)) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            float w = MathF.Max(x: l.Width > 0 ? l.Width : NodeMinWidth, y: NodeMinWidth);
            float h = EffectiveHeight(layout: l, def: def);
            minX = MathF.Min(x: minX, y: l.X);
            minY = MathF.Min(x: minY, y: l.Y);
            maxX = MathF.Max(x: maxX, y: l.X + w);
            maxY = MathF.Max(x: maxY, y: l.Y + h);
        }

        if (minX > maxX) return;

        float vMinX = ScreenToWorldX(Bounds.X);
        float vMaxX = ScreenToWorldX(Bounds.Right);
        float vMinY = ScreenToWorldY(Bounds.Y);
        float vMaxY = ScreenToWorldY(Bounds.Bottom);
        minX = MathF.Min(x: minX, y: vMinX);
        maxX = MathF.Max(x: maxX, y: vMaxX);
        minY = MathF.Min(x: minY, y: vMinY);
        maxY = MathF.Max(x: maxY, y: vMaxY);

        const float pad = 24f;
        minX -= pad;
        minY -= pad;
        maxX += pad;
        maxY += pad;
        float worldW = maxX - minX;
        float worldH = maxY - minY;
        if (worldW <= 0f || worldH <= 0f) return;

        const float mmW = 180f, mmH = 120f, margin = 12f, inset = 6f;
        var mm = new Rect(
            x: Bounds.Right - mmW - margin,
            y: Bounds.Bottom - mmH - margin,
            width: mmW,
            height: mmH
        );
        paint.AddElevation(bounds: mm, radius: Radii.Md, style: Elevation.Z1);
        paint.AddRect(bounds: mm, color: _theme.PanelSunken.WithAlpha(0.92f), radius: Radii.Md);
        paint.AddBorder(bounds: mm, color: _theme.Border, radius: Radii.Md);

        float fitW = mmW - (inset * 2f);
        float fitH = mmH - (inset * 2f);
        float scale = MathF.Min(x: fitW / worldW, y: fitH / worldH);
        float offX = mm.X + inset + ((fitW - (worldW * scale)) * 0.5f);
        float offY = mm.Y + inset + ((fitH - (worldH * scale)) * 0.5f);

        float Mx(float wx) => offX + ((wx - minX) * scale);

        float My(float wy) => offY + ((wy - minY) * scale);

        paint.AddClipStart(mm);
        foreach (var node in _state.Graph.Nodes)
        {
            if (!layouts.TryGetValue(key: node.Id, value: out var l)) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            float w = MathF.Max(x: l.Width > 0 ? l.Width : NodeMinWidth, y: NodeMinWidth);
            float h = EffectiveHeight(layout: l, def: def);
            var col = _state.IsSelected(node.Id)
                ? _theme.Primary
                : CategoryColor(def?.Category ?? "");
            paint.AddRect(
                bounds: new Rect(
                    x: Mx(l.X),
                    y: My(l.Y),
                    width: MathF.Max(x: 2f, y: w * scale),
                    height: MathF.Max(x: 2f, y: h * scale)
                ),
                color: col,
                radius: 1f
            );
        }

        var vr = new Rect(
            x: Mx(vMinX),
            y: My(vMinY),
            width: (vMaxX - vMinX) * scale,
            height: (vMaxY - vMinY) * scale
        );
        paint.AddRect(bounds: vr, color: _theme.OnSurface.WithAlpha(0.06f));
        paint.AddBorder(
            bounds: vr,
            color: _theme.OnSurface.WithAlpha(0.7f),
            radius: 0f,
            width: 1.5f
        );
        paint.AddClipEnd();
    }

    // ── Grid ──────────────────────────────────────────────────────────────────

    private void DrawGrid(PaintList paint)
    {
        float spacing = GridSpacing * _zoom;
        while (spacing < 14f) spacing *= 2f;
        float startX = Bounds.X + ((_panX % spacing) + spacing) % spacing;
        float startY = Bounds.Y + ((_panY % spacing) + spacing) % spacing;
        var color = _theme.OnSurface.WithAlpha(0.10f);
        for (float x = startX; x < Bounds.Right; x += spacing)
            for (float y = startY; y < Bounds.Bottom; y += spacing)
                paint.AddRect(new Rect(x - 0.8f, y - 0.8f, 1.6f, 1.6f), color, 0.8f);
    }

    // ── Node height helpers ───────────────────────────────────────────────────

    // Height from top of node to bottom of pin rows (world units).
    private static float PinsBodyH(NodeDefinition? def)
    {
        int pinCount = (def?.Inputs.Count ?? 0) + (def?.Outputs.Count ?? 0);
        return NodeHeaderH + (MathF.Max(x: pinCount, y: 1) * NodePinH) + 6f;
    }

    // Height of just the inline property section (world units).
    private static float PropsBodyH(NodeDefinition? def)
    {
        int c = InlineIndex(def, null);
        return c > 0 ? NodePropSepH + (c * NodePropH) + 4f : 0f;
    }

    // Row of `prop` among the properties drawn on the card, or the row count when prop is null.
    // Inspector-only properties take no row.
    private static int InlineIndex(NodeDefinition? def, PropertyDefinition? prop)
    {
        int row = 0;
        if (def is null) return 0;
        foreach (var p in def.Properties)
        {
            if (p == prop) return row;
            if (!p.InspectorOnly) row++;
        }
        return row;
    }

    // True while a node's bypass property (PropertyDefinition.BypassEditor) is switched on.
    private static bool IsBypassed(GraphNode node, NodeDefinition? def)
    {
        if (def is null) return false;
        foreach (var p in def.Properties)
            if (p.Editor == PropertyDefinition.BypassEditor && p.Type == GraphTypeRef.Bool)
                return node.Properties.TryGetValue(p.Id, out var v) && v.Kind == GraphValueKind.Bool && v.AsBool();
        return false;
    }

    // Full node body height including inline property rows (world units).
    private static float TotalBodyH(NodeDefinition? def) => PinsBodyH(def) + PropsBodyH(def);

    // Effective node body height: the user-set layout.Height when larger than the auto content height,
    // else the auto height (so pins/props never clip; extra height is empty space below props).
    private static float EffectiveHeight(NodeLayoutData layout, NodeDefinition? def)
    {
        if (layout.IsCollapsed) return MathF.Max(NodeHeaderH, 12f + Math.Max(def?.Inputs.Count ?? 0, def?.Outputs.Count ?? 0) * 12f);
        float min = TotalBodyH(def);
        return layout.Height > min ? layout.Height : min;
    }

    /// <summary>
    ///     Request a relayout so view-derived widgets (e.g. the output preview / LiquidGlass) track
    ///     pan/zoom/drag.
    /// </summary>
    private void InvalidateView()
    {
        _state.Graph.EditorData.ViewOffsetX = _panX;
        _state.Graph.EditorData.ViewOffsetY = _panY;
        _state.Graph.EditorData.Zoom = _zoom;
        MarkNeedsLayout();
    }

    // The same geometry drives painting, hit-testing, snapping, beacons, and cable endpoints.
    private static float PinY(NodeLayoutData layout, NodeDefinition def, int index, bool output) =>
        layout.Y + (layout.IsCollapsed ? 12f + index * 12f : NodeHeaderH + 3f +
            ((output ? index : def.Outputs.Count + index) + 0.5f) * NodePinH);


    // ── Nodes ─────────────────────────────────────────────────────────────────

    private void DrawNodes(PaintList paint)
    {
        foreach (var node in _state.Graph.Nodes)
        {
            // Feature 3: cull nodes outside the visible canvas area
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            float sx = WorldToScreenX(layout.X);
            float sy = WorldToScreenY(layout.Y);
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            float sw = Scale(
                MathF.Max(x: layout.Width > 0 ? layout.Width : NodeMinWidth, y: NodeMinWidth)
            );
            float sh = Scale(EffectiveHeight(layout: layout, def: def));
            if (sx > Bounds.Right || sy > Bounds.Bottom || sx + sw < Bounds.X ||
                sy + sh < Bounds.Y) continue;
            DrawNode(paint: paint, node: node);
        }
    }

    private void DrawNode(PaintList paint, GraphNode node)
    {
        if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                key: node.Id,
                value: out var layout
            )) return;

        var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
        float totalBodyH = EffectiveHeight(layout: layout, def: def);
        float ww = MathF.Max(x: layout.Width > 0 ? layout.Width : NodeMinWidth, y: NodeMinWidth);

        bool hasPreview = !layout.IsCollapsed && _outputPreviewWidget is not null && node.Id == _outputNodeId;

        float sx = WorldToScreenX(layout.X);
        float sy = WorldToScreenY(layout.Y);
        float sw = Scale(ww);
        // Preview is constrained to node card width (set in Layout), so sw never needs expanding.
        float sh = hasPreview
            ? Scale(totalBodyH) + (PreviewPad * 2f) + _outputPreviewMeasuredSize.Height
            : Scale(totalBodyH);

        // Feature 2: LOD level based on zoom
        int lod = _zoom >= ZoomLodFull ? 2 : _zoom >= ZoomLodMin ? 1 : 0;

        bool missingInput = def?.Inputs.Any(p => GraphInputValidation.IsMissing(_state.Graph, _state.Registry, node, p)) == true;
        bool isSelected = _state.IsSelected(node.Id);
        var nodeRect = new Rect(
            x: sx,
            y: sy,
            width: sw,
            height: sh
        );
        float radius = 6f * _zoom;

        // LOD 0: skip shadow for performance
        if (lod >= 1)
        {
            paint.AddShadow(
                bounds: nodeRect,
                color: new Color(
                    r: 0,
                    g: 0,
                    b: 0,
                    a: 0.45f
                ),
                borderRadius: radius,
                blurRadius: Scale(8f),
                spread: Scale(-2f)
            );
        }

        paint.AddRect(bounds: nodeRect, color: _theme.Panel, radius: radius);

        // Category-colored headers keep node roles recognizable even when collapsed.
        var accent = CategoryColor(def?.Category ?? "");
        var headerRect = new Rect(
            x: sx,
            y: sy,
            width: sw,
            height: Scale(layout.IsCollapsed ? totalBodyH : NodeHeaderH)
        );
        // A bypassed node keeps its place and wiring but reads as switched off.
        bool bypassed = IsBypassed(node, def);
        var headerColor = bypassed ? _theme.PanelRaised : accent;
        paint.AddRect(bounds: headerRect, color: headerColor, radius: radius);
        paint.AddRect(
            bounds: new Rect(
                x: sx,
                y: sy + Scale(NodeHeaderH) - (radius * 0.5f),
                width: sw,
                height: radius * 0.5f
            ),
            color: headerColor
        );
        paint.AddRect(
            bounds: new Rect(
                x: sx,
                y: sy + Scale(NodeHeaderH) - 1f,
                width: sw,
                height: 1f
            ),
            color: _theme.Border
        );

        float fs = MathF.Max(7f, 13f * _zoom);
        float cx = sx + Scale(15f), cy = sy + Scale(NodeHeaderH * 0.5f);
        // Disclosure triangle, also usable when the card is collapsed.
        var ink = bypassed ? _theme.TextMuted : new Color(0.96f, 0.97f, 0.98f);
        if (layout.IsCollapsed)
        {
            DrawLine(paint, new Offset(cx - Scale(2f), cy - Scale(4f)), new Offset(cx + Scale(2f), cy), ink, Scale(1.5f));
            DrawLine(paint, new Offset(cx + Scale(2f), cy), new Offset(cx - Scale(2f), cy + Scale(4f)), ink, Scale(1.5f));
        }
        else
        {
            DrawLine(paint, new Offset(cx - Scale(4f), cy - Scale(2f)), new Offset(cx, cy + Scale(2f)), ink, Scale(1.5f));
            DrawLine(paint, new Offset(cx, cy + Scale(2f)), new Offset(cx + Scale(4f), cy - Scale(2f)), ink, Scale(1.5f));
        }
        paint.AddClipStart(new Rect(sx + Scale(29f), sy, sw - Scale(36f), Scale(NodeHeaderH)));
        paint.AddText((def?.DisplayName ?? node.DefinitionId) + (bypassed ? "  (bypassed)" : ""), sx + Scale(29f), cy + fs * 0.36f, ink, fs);
        paint.AddClipEnd();

        if (def is not null)
        {
            DrawPins(
                paint: paint,
                node: node,
                def: def,
                sx: sx,
                sy: sy,
                sw: sw,
                lod: lod
            );
            if (!layout.IsCollapsed && def.Properties.Count > 0 && lod >= 2)
            {
                DrawInlineProperties(
                    paint: paint,
                    node: node,
                    def: def,
                    sx: sx,
                    sy: sy,
                    sw: sw
                );
            }
        }

        // Feature 1: resize grip — 3 short diagonal lines at bottom-right corner
        if (!layout.IsCollapsed && _zoom > 0.4f)
        {
            var gripColor = _theme.TextMuted.WithAlpha(0.7f);
            float gripSize = Scale(NodeResizeGrip);
            float gx = sx + sw - gripSize;
            float gy = sy + Scale(totalBodyH) - gripSize;
            float lineLen = gripSize * 0.65f;
            float sep = lineLen * 0.38f;
            for (int k = 0; k < 3; k++)
            {
                float off = k * sep;
                // Tiny horizontal rects to approximate diagonal lines
                float midX = gx + gripSize - (lineLen * 0.5f) + (off * 0.5f);
                float midY = gy + gripSize - (off * 0.5f) - (lineLen * 0.5f);
                paint.AddRect(
                    bounds: new Rect(
                        x: midX - (lineLen * 0.5f),
                        y: midY - 0.75f,
                        width: lineLen,
                        height: 1.0f
                    ),
                    color: gripColor
                );
            }
        }

        // Thin divider separating body from the live preview area
        if (hasPreview)
        {
            float divY = sy + Scale(totalBodyH) + 1f;
            paint.AddRect(
                bounds: new Rect(
                    x: sx + 6f,
                    y: divY,
                    width: sw - 12f,
                    height: 1f
                ),
                color: _theme.Separator
            );
        }

        if (isSelected)
        {
            paint.AddBorder(
                bounds: nodeRect,
                color: _theme.Primary,
                radius: radius,
                width: 1.5f * _zoom
            );
        }
        else
        {
            paint.AddBorder(
                bounds: nodeRect,
                color: missingInput ? _theme.Error : _theme.Border,
                radius: radius,
                width: missingInput ? 1.5f : 0.75f
            );
        }
    }

    private void DrawPins(PaintList paint, GraphNode node, NodeDefinition def,
        float sx, float sy, float sw, int lod)
    {
        // LOD 0: skip pins entirely
        if (lod < 1) return;

        float fs = MathF.Max(x: 7f, y: 11.5f * _zoom);
        var layout = _state.Graph.EditorData.NodeLayouts[node.Id];
        float pinR = NodePinRadius * _zoom;
        float rowH = NodePinH * _zoom;
        float baseY = sy + Scale(NodeHeaderH) + Scale(3f);

        for (int i = 0; i < def.Inputs.Count; i++)
        {
            var pin = def.Inputs[i];
            float pinY = WorldToScreenY(PinY(layout, def, i, false));
            bool missing = GraphInputValidation.IsMissing(_state.Graph, _state.Registry, node, pin);
            var col = missing ? _theme.Error : PinColor(pin.Type);
            paint.AddRect(
                bounds: new Rect(
                    x: sx - pinR,
                    y: pinY - pinR,
                    width: pinR * 2f,
                    height: pinR * 2f
                ),
                color: col,
                radius: pinR
            );
            // LOD 1: skip pin label text
            if (lod >= 2 && !layout.IsCollapsed)
            {
                paint.AddText(
                    text: missing ? $"{pin.DisplayName} • required" : pin.DisplayName,
                    baselineX: sx + pinR + Scale(4f),
                    baselineY: pinY + (fs * 0.38f),
                    color: _theme.OnSurface,
                    fontSize: fs
                );
            }
        }

        for (int i = 0; i < def.Outputs.Count; i++)
        {
            var pin = def.Outputs[i];
            float pinY = WorldToScreenY(PinY(layout, def, i, true));
            var col = PinColor(pin.Type);
            paint.AddRect(
                bounds: new Rect(
                    x: sx + sw - pinR,
                    y: pinY - pinR,
                    width: pinR * 2f,
                    height: pinR * 2f
                ),
                color: col,
                radius: pinR
            );
            if (lod >= 2 && !layout.IsCollapsed)
            {
                float labelW = pin.DisplayName.Length * fs * 0.55f;
                paint.AddText(
                    text: pin.DisplayName,
                    baselineX: sx + sw - pinR - labelW - Scale(4f),
                    baselineY: pinY + (fs * 0.38f),
                    color: _theme.OnSurface,
                    fontSize: fs
                );
            }
        }
    }

    private void DrawInlineProperties(PaintList paint, GraphNode node, NodeDefinition def,
        float sx, float sy, float sw)
    {
        float fs = MathF.Max(7f, 11.5f * _zoom);
        float rowH = Scale(NodePropH);
        float py = sy + Scale(PinsBodyH(def) + NodePropSepH + 2f);
        for (int i = 0; i < def.Properties.Count; i++, py += rowH)
        {
            var prop = def.Properties[i];
            if (prop.InspectorOnly)
            {
                py -= rowH;
                continue;
            }
            var val = node.Properties.TryGetValue(prop.Id, out var stored) && !stored.IsNull ? stored : prop.DefaultValue;
            var control = new Rect(sx + Scale(10f), py + Scale(2f), sw - Scale(20f), rowH - Scale(4f));
            bool textEditing = _textTarget is { } textTarget && textTarget.NodeId == node.Id && textTarget.PropId == prop.Id;
            bool active = textEditing || (_editingProp is { } edit && edit.NodeId == node.Id && edit.PropId == prop.Id);
            paint.AddRect(control, _theme.PanelRaised, Scale(4f));
            paint.AddClipStart(control);
            if (IsNumeric(prop) && prop.Min is { } min && prop.Max is { } max && max > min)
            {
                float v = val.IsNull ? min : prop.Type == GraphTypeRef.Int ? val.AsInt() : val.AsFloat();
                float fraction = Math.Clamp((v - min) / (max - min), 0, 1);
                paint.AddRect(new Rect(control.X, control.Y, control.Width * fraction, control.Height),
                    _theme.Primary.WithAlpha(active ? 0.65f : 0.38f), Scale(4f));
            }
            string value = FormatPropValue(prop, val);
            if (!string.IsNullOrEmpty(prop.Unit)) value += " " + prop.Unit;
            if (prop.EnumLabels is { Length: > 0 }) value += " ▾";
            float valueWidth = textEditing ? control.Width * 0.55f
                : MathF.Min(control.Width * 0.48f, value.Length * fs * 0.58f);
            string name = prop.DisplayName;
            int maxName = Math.Max(1, (int)((control.Width - valueWidth - Scale(25f)) / (fs * 0.56f)));
            if (name.Length > maxName) name = name[..Math.Max(0, maxName - 1)] + "…";
            paint.AddText(name, control.X + Scale(8f), py + rowH * 0.66f, _theme.OnSurface, fs);
            if (textEditing)
            {
                if (!string.IsNullOrEmpty(prop.Unit))
                {
                    float unitWidth = prop.Unit.Length * fs * 0.58f;
                    paint.AddText(prop.Unit, control.Right - unitWidth - Scale(8f), py + rowH * 0.66f, _theme.TextMuted, fs);
                }
                paint.AddClipEnd();
                paint.AddBorder(control, _textError is null ? _theme.Primary : _theme.Error, Scale(4f), Scale(1.5f));
                continue;
            }
            paint.AddClipStart(new Rect(control.Right - valueWidth - Scale(8f), control.Y, valueWidth, control.Height));
            paint.AddText(value, control.Right - valueWidth - Scale(8f), py + rowH * 0.66f, _theme.OnSurface, fs);
            paint.AddClipEnd();
            paint.AddClipEnd();
            paint.AddBorder(control, active ? _theme.Primary : _theme.Border, Scale(4f));
        }
    }

    private static bool IsNumeric(PropertyDefinition prop) =>
        prop.Type == GraphTypeRef.Float || (prop.Type == GraphTypeRef.Int && prop.EnumLabels is not { Length: > 0 });

    private static string FormatPropValue(PropertyDefinition prop, GraphValue val)
    {
        if (val.IsNull) return "—";
        if (prop.Type == GraphTypeRef.Float)
        {
            float f = val.AsFloat();
            return f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (prop.Type == GraphTypeRef.Int && prop.EnumLabels is { Length: > 0 } labels)
            return labels[Math.Clamp(val.AsInt(), 0, labels.Length - 1)];
        if (prop.Type == GraphTypeRef.Bool) return val.AsBool() ? "on" : "off";
        if (prop.Type == GraphTypeRef.String)
        {
            string s = val.AsString();
            return s.Length > 12 ? s[..12] + "…" : s;
        }

        return val.ToString() ?? "—";
    }

    // ── Edges ─────────────────────────────────────────────────────────────────

    // Connection policy can traverse the graph; evaluate once per edit, not once per painted cable.
    private void RefreshConnectionValidity()
    {
        _invalidEdges.Clear();
        foreach (var edge in _state.Graph.Edges)
            if (!GraphConnections.CanConnect(_state.Graph, _state.Registry, edge.From, edge.To, out _, edge.Id))
                _invalidEdges.Add(edge.Id);
    }

    private void DrawEdges(PaintList paint)
    {
        foreach (var edge in _state.Graph.Edges)
        {
            if (edge.Id == _rerouteEdge?.Id) continue; // drawn as the in-flight cable instead
            var fromPin = GetPinWorldPos(edge.From);
            var toPin = GetPinWorldPos(edge.To);
            if (!fromPin.HasValue || !toPin.HasValue) continue;

            (float fx, float fy) = fromPin.Value;
            (float tx, float ty) = toPin.Value;

            // Feature 3: cull edges where bounding box is entirely outside visible area
            float fsx = WorldToScreenX(fx);
            float fsy = WorldToScreenY(fy);
            float tsx = WorldToScreenX(tx);
            float tsy = WorldToScreenY(ty);
            float margin = 50f;
            float minX = MathF.Min(x: fsx, y: tsx);
            float maxX = MathF.Max(x: fsx, y: tsx);
            float minY = MathF.Min(x: fsy, y: tsy);
            float maxY = MathF.Max(x: fsy, y: tsy);
            if (maxX < Bounds.X - margin || minX > Bounds.Right + margin ||
                maxY < Bounds.Y - margin || minY > Bounds.Bottom + margin) continue;

            // Derive edge color with optional hover/selection tint
            var baseColor = GetEdgeColor(edge);
            Color edgeColor;
            float lineWidth;
            if (_state.IsEdgeSelected(edge.Id))
            {
                edgeColor = _theme.Primary;
                lineWidth = 3f * _zoom;
            }
            else if (_hoveredEdgeId == edge.Id)
            {
                edgeColor = new Color(
                    r: baseColor.R + ((1f - baseColor.R) * 0.4f),
                    g: baseColor.G + ((1f - baseColor.G) * 0.4f),
                    b: baseColor.B + ((1f - baseColor.B) * 0.4f),
                    a: baseColor.A
                );
                lineWidth = 2.5f * _zoom;
            }
            else
            {
                edgeColor = baseColor;
                lineWidth = 2f * _zoom;
            }

            if (_invalidEdges.Contains(edge.Id))
                edgeColor = _theme.Error;
            if (_insert?.Edge.Id == edge.Id)
            {
                // The cable a dragged node would be spliced into on release.
                edgeColor = _theme.Success;
                lineWidth = 3.5f * _zoom;
            }

            // A dark under-stroke keeps a cable readable where it crosses another cable or a card.
            DrawBezier(paint, fsx, fsy, tsx, tsy, _theme.GraphBackground.WithAlpha(0.85f), lineWidth + (3f * _zoom));
            DrawBezier(
                paint: paint,
                x0: fsx,
                y0: fsy,
                x1: tsx,
                y1: tsy,
                color: edgeColor,
                width: lineWidth
            );
        }
    }

    private void DrawDragEdge(PaintList paint)
    {
        if (!_dragPin.HasValue) return;
        float fx, fy, tx, ty;
        if (_dragPinDir == PinDirection.Output)
        {
            fx = WorldToScreenX(_dragPinWorldX);
            fy = WorldToScreenY(_dragPinWorldY);
            tx = WorldToScreenX(_mousePinWorldX);
            ty = WorldToScreenY(_mousePinWorldY);
        }
        else
        {
            fx = WorldToScreenX(_mousePinWorldX);
            fy = WorldToScreenY(_mousePinWorldY);
            tx = WorldToScreenX(_dragPinWorldX);
            ty = WorldToScreenY(_dragPinWorldY);
        }

        var wireColor = _dragHoverPin is null
            ? new Color(
                r: 1f,
                g: 1f,
                b: 1f,
                a: 0.55f
            )
            : _dragHoverValid
                ? _theme.Success
                : _theme.Error;
        DrawBezier(
            paint: paint,
            x0: fx,
            y0: fy,
            x1: tx,
            y1: ty,
            color: wireColor,
            width: 1.5f * _zoom
        );
    }

    /// <summary>
    ///     While dragging a wire, halo every compatible target pin (green) + ring an invalid hovered
    ///     pin (red).
    /// </summary>
    private void DrawConnectionBeacons(PaintList paint)
    {
        if (!_dragPin.HasValue || _zoom < ZoomLodMin) return;

        foreach (var node in _state.Graph.Nodes)
        {
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            if (def is null) continue;
            float ww = MathF.Max(
                x: layout.Width > 0 ? layout.Width : NodeMinWidth,
                y: NodeMinWidth
            );
            float baseY = layout.Y + NodeHeaderH + 3f;

            for (int i = 0; i < def.Inputs.Count; i++)
            {
                Beacon(
                    ep: new GraphPinEndpoint(NodeId: node.Id, PinId: def.Inputs[i].Id),
                    wx: layout.X,
                    wy: PinY(layout, def, i, false)
                );
            }

            for (int i = 0; i < def.Outputs.Count; i++)
            {
                Beacon(
                    ep: new GraphPinEndpoint(NodeId: node.Id, PinId: def.Outputs[i].Id),
                    wx: layout.X + ww,
                    wy: PinY(layout, def, i, true)
                );
            }
        }

        if (_dragHoverPin is { } hp && !_dragHoverValid && GetPinWorldPos(hp) is { } wp)
        {
            float sx = WorldToScreenX(wp.wx);
            float sy = WorldToScreenY(wp.wy);
            float r = NodePinRadius * _zoom * 2.2f;
            paint.AddBorder(
                bounds: new Rect(
                    x: sx - r,
                    y: sy - r,
                    width: r * 2f,
                    height: r * 2f
                ),
                color: _theme.Error,
                radius: r,
                width: 2f
            );
        }

        return;

        void Beacon(GraphPinEndpoint ep, float wx, float wy)
        {
            if (!IsCompatibleTarget(ep)) return;
            float sx = WorldToScreenX(wx);
            float sy = WorldToScreenY(wy);
            float r = NodePinRadius * _zoom * 2.2f;
            var rect = new Rect(
                x: sx - r,
                y: sy - r,
                width: r * 2f,
                height: r * 2f
            );
            paint.AddRect(bounds: rect, color: _theme.Success.WithAlpha(0.30f), radius: r);
            paint.AddBorder(
                bounds: rect,
                color: _theme.Success,
                radius: r,
                width: 1.5f
            );
        }
    }

    /// <summary>
    ///     Draws a node-graph edge as a single native anti-aliased cubic-Bézier stroke. Both control
    ///     handles extend horizontally outward from their endpoints, so backward edges arc outward
    ///     (U-shape) instead of self-intersecting. The native renderer tessellates the curve into one
    ///     continuous ribbon — translucent edges (drag/hover) blend uniformly instead of banding, and
    ///     a wire is one draw call instead of the old hundreds of overlapping stamps.
    /// </summary>
    private static void DrawBezier(PaintList paint,
        float x0, float y0, float x1, float y1, Color color, float width)
    {
        float absDx = MathF.Abs(x1 - x0);
        float absDy = MathF.Abs(y1 - y0);
        float handleLen = MathF.Max(x: (absDx * 0.5f) + (absDy * 0.25f), y: 24f);
        paint.AddBezier(
            x0: x0,
            y0: y0,
            x1: x0 + handleLen,
            y1: y0,
            x2: x1 - handleLen,
            y2: y1,
            x3: x1,
            y3: y1,
            color: color,
            width: width
        );
    }

    // ── Box selection ─────────────────────────────────────────────────────────

    private void DrawBoxSelection(PaintList paint)
    {
        float x = MathF.Min(x: _boxStartX, y: _boxEndX);
        float y = MathF.Min(x: _boxStartY, y: _boxEndY);
        float w = MathF.Abs(_boxEndX - _boxStartX);
        float h = MathF.Abs(_boxEndY - _boxStartY);
        var rect = new Rect(
            x: x,
            y: y,
            width: w,
            height: h
        );
        paint.AddRect(bounds: rect, color: _theme.Primary.WithAlpha(0.08f));
        paint.AddBorder(bounds: rect, color: _theme.Primary.WithAlpha(0.55f));
    }

    // ── Hints ─────────────────────────────────────────────────────────────────

    private void DrawHints(PaintList paint)
    {
        string hint = _textError ?? (_textEditor is not null ? "Enter  Apply value     Esc  Cancel" : null) ?? _connectionError ?? (_dragPin.HasValue
            ? "Drop on a socket to connect · Drop on empty space to add a node · Esc cancels"
            : _insert is not null ? "Release to insert this node into the cable"
            : _state.SelectedNodes.Count > 0
                ? "Arrows  Nudge     Ctrl C / X / V  Copy, cut, paste     Ctrl D  Duplicate     Del  Delete     H  Collapse     F  Frame     Esc  Deselect"
                : _state.SelectedEdges.Count > 0
                    ? "Del  Disconnect     Shift-click  Select more cables     Drag a cable's input end to re-plug it     Alt-click  Cut"
                    : "Shift A / Space  Add node     Shift-click / drag  Select     Ctrl V  Paste     F  Frame all     Scroll or right-drag  Pan     Ctrl scroll  Zoom");
        var strip = new Rect(Bounds.X, Bounds.Bottom - 27f, Bounds.Width, 27f);
        paint.AddRect(strip, _theme.GraphBackground.WithAlpha(0.94f));
        paint.AddClipStart(new Rect(strip.X + 10f, strip.Y, MathF.Max(0, strip.Width - 70f), strip.Height));
        paint.AddText(hint, strip.X + 10f, strip.Y + 18f, _connectionError is null && _textError is null ? _theme.TextMuted : _theme.Error, 11f);
        paint.AddClipEnd();
        paint.AddText($"{_zoom * 100f:F0}%", Bounds.Right - 48f, strip.Y + 18f, _theme.TextMuted, 11f);
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    public override void OnPointerDown(Offset point)
    {
        AppInstance.Active?.RequestFocus(this);

        float wx = ScreenToWorldX(point.X);
        float wy = ScreenToWorldY(point.Y);

        _connectionError = null;
        var headerNode = HitTestNode(wx, wy);
        if (headerNode is { } hn && _state.Graph.EditorData.NodeLayouts.TryGetValue(hn, out var hl)
            && wx >= hl.X + 6f && wx <= hl.X + 27f && wy <= hl.Y + NodeHeaderH)
        {
            _state.Commands.Execute(new CollapseNodesCommand([hn], !hl.IsCollapsed));
            return;
        }

        // Feature 1: resize grip check — highest priority, before pin hit-test
        var resizeTarget = HitTestResizeGrip(wx: wx, wy: wy);
        if (resizeTarget.HasValue)
        {
            _resizeNodeId = resizeTarget.Value;
            _resizeStartWorldX = wx;
            _resizeStartWorldY = wy;
            var rLayout = _state.Graph.EditorData.NodeLayouts[resizeTarget.Value];
            _resizeStartWidth = MathF.Max(
                x: rLayout.Width > 0 ? rLayout.Width : NodeMinWidth,
                y: NodeMinWidth
            );
            var rNode = _state.Graph.FindNode(resizeTarget.Value);
            var rDef = rNode is null ? null : _state.Registry.GetNodeDefinition(rNode.DefinitionId);
            _resizeStartHeight = EffectiveHeight(layout: rLayout, def: rDef);
            return;
        }

        // Feature 5: property row hit-test — before node drag, after resize
        var propHit = HitTestPropRow(wx: wx, wy: wy);
        if (propHit.HasValue)
        {
            var (nid, prop, _) = propHit.Value;
            var node = _state.Graph.FindNode(nid)!;
            _state.Select(nid);
            if (IsNumeric(prop) || prop.Type == GraphTypeRef.Bool || prop.EnumLabels is { Length: > 0 })
            {
                _editOriginalValue = node.Properties.GetValueOrDefault(prop.Id) ?? GraphValue.Null();
                var current = _editOriginalValue.IsNull ? prop.DefaultValue : _editOriginalValue;
                _editDragStartFloat = current.IsNull ? 0f : prop.Type == GraphTypeRef.Int ? current.AsInt()
                    : prop.Type == GraphTypeRef.Float ? current.AsFloat() : 0f;
                _editingProp = new PropEditTarget(nid, prop.Id, prop, default);
                _editDragStartX = point.X;
                _editPress = point;
                _editMoved = false;
                MarkNeedsPaint();
                return;
            }
            if (prop.Type == GraphTypeRef.String && prop.Editor is null)
                BeginTextEdit(new PropEditTarget(nid, prop.Id, prop, default));
            // A property click selects its node, rather than starting a node drag.
            return;
        }

        // 1) Pin drag (edge creation)
        var hitPin = HitTestPin(wx: wx, wy: wy);
        if (hitPin.HasValue)
        {
            _dragPin = hitPin.Value.endpoint;
            _dragPinDir = hitPin.Value.dir;
            _dragPinWorldX = hitPin.Value.wx;
            _dragPinWorldY = hitPin.Value.wy;
            _mousePinWorldX = wx;
            _mousePinWorldY = wy;
            _rerouteEdge = null;
            // Pulling on an input that holds exactly one cable picks that cable up: drop it on
            // another input to re-plug it, or on empty canvas to unplug it. (With several cables
            // on the input there is no single one to mean; start from the output end instead.)
            if (_dragPinDir == PinDirection.Input)
            {
                var incoming = _state.Graph.Edges.Where(e => e.To == hitPin.Value.endpoint).Take(2).ToArray();
                if (incoming.Length == 1 && GetPinWorldPos(incoming[0].From) is { } source)
                {
                    _rerouteEdge = incoming[0];
                    _dragPin = incoming[0].From;
                    _dragPinDir = PinDirection.Output;
                    _dragPinWorldX = source.wx;
                    _dragPinWorldY = source.wy;
                }
            }

            return;
        }

        // 2) Edge click → select edge
        var hitEdge = HitTestEdge(sx: point.X, sy: point.Y);
        if (hitEdge.HasValue && HitTestNode(wx, wy) is null)
        {
            // Alt-click cuts the cable outright.
            if (Mods.HasFlag(Modifiers.Alt))
            {
                _state.Commands.Execute(new DeleteEdgeCommand(hitEdge.Value));
                _hoveredEdgeId = null;
            }
            else if (Additive) _state.ToggleEdgeSelection(hitEdge.Value);
            else _state.SelectEdge(hitEdge.Value);
            return;
        }

        // 3) Node click
        var hitNode = HitTestNode(wx: wx, wy: wy);
        if (hitNode.HasValue)
        {
            _collapseOnRelease = null;
            if (Additive) _state.ToggleSelection(hitNode.Value);
            else if (!_state.IsSelected(hitNode.Value)) _state.Select(hitNode.Value);
            else if (_state.SelectedNodes.Count > 1) _collapseOnRelease = hitNode.Value;
            if (!_state.IsSelected(hitNode.Value)) return; // toggled off: nothing to drag
            _dragOrigins.Clear();
            foreach (var id in _state.SelectedNodes)
                if (_state.Graph.EditorData.NodeLayouts.TryGetValue(id, out var origin))
                    _dragOrigins[id] = (origin.X, origin.Y);
            _dragNodeId = hitNode.Value;
            var layout = _state.Graph.EditorData.NodeLayouts[hitNode.Value];
            _dragNodeStartX = layout.X;
            _dragNodeStartY = layout.Y;
            _dragOffsetX = wx - layout.X;
            _dragOffsetY = wy - layout.Y;
            return;
        }

        // 4) Empty space → box select. Shift/Ctrl keeps what is selected and adds to it.
        _boxBase = Additive ? _state.SelectedNodes.ToArray() : [];
        if (!Additive) _state.ClearSelection();
        _isBoxSelecting = true;
        _boxStartX = _boxEndX = point.X;
        _boxStartY = _boxEndY = point.Y;
    }

    public override void OnPointerMove(Offset point)
    {
        _mouseX = point.X;
        _mouseY = point.Y;

        float wx = ScreenToWorldX(point.X);
        float wy = ScreenToWorldY(point.Y);

        if (_isPanning)
        {
            float dx = point.X - _panMouseStartX;
            float dy = point.Y - _panMouseStartY;
            if (!_panDragged && MathF.Sqrt((dx * dx) + (dy * dy)) > 5f)
                _panDragged = true;

            _panX += point.X - _panMouseX;
            _panY += point.Y - _panMouseY;
            _panMouseX = point.X;
            _panMouseY = point.Y;
            // Relayout so view-anchored widgets (output preview / LiquidGlass) track the pan; a captured
            // mouse-move otherwise only repaints, leaving them at their stale screen rect.
            InvalidateView();
            return;
        }

        // Feature 1: real-time 2-axis resize feedback
        if (_resizeNodeId.HasValue)
        {
            if (_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: _resizeNodeId.Value,
                    value: out var layout
                ))
            {
                layout.Width = Math.Clamp(
                    value: _resizeStartWidth + (wx - _resizeStartWorldX),
                    min: NodeMinWidth,
                    max: float.MaxValue
                );
                var rNode = _state.Graph.FindNode(_resizeNodeId.Value);
                var rDef = rNode is null
                    ? null
                    : _state.Registry.GetNodeDefinition(rNode.DefinitionId);
                layout.Height = Math.Clamp(
                    value: _resizeStartHeight + (wy - _resizeStartWorldY),
                    min: TotalBodyH(rDef),
                    max: float.MaxValue
                );
            }

            InvalidateView();
            return;
        }

        if (_editingProp is { } editing)
        {
            _editMoved |= MathF.Abs(point.X - _editPress.X) > 3f || MathF.Abs(point.Y - _editPress.Y) > 3f;
            if (!_editMoved || !IsNumeric(editing.Def)) return;
            var prop = editing.Def;
            float step = prop.Step is > 0 ? prop.Step.Value : prop.Type == GraphTypeRef.Int ? 1f : 0.01f;
            float value = _editDragStartFloat + (point.X - _editDragStartX) / _zoom * step;
            if (prop.Min is { } min && prop.Max is { } max && max > min)
            {
                var layout = _state.Graph.EditorData.NodeLayouts[editing.NodeId];
                float width = MathF.Max(layout.Width, NodeMinWidth);
                float precision = _modifiers.HasFlag(Modifiers.Shift) ? 0.1f : 1f;
                value = _editDragStartFloat + (point.X - _editDragStartX) / Scale(width - 20f) * (max - min) * precision;
            }
            value = MathF.Round(value / step) * step;
            value = Math.Clamp(value, prop.Min ?? float.MinValue, MathF.Max(prop.Min ?? float.MinValue, prop.Max ?? float.MaxValue));
            var node = _state.Graph.FindNode(editing.NodeId);
            if (node is not null)
            {
                node.Properties[prop.Id] = prop.Type == GraphTypeRef.Int ? GraphValue.FromInt((int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)) : GraphValue.FromFloat(value);
                if (_state.AutoCompile) _state.TriggerCompile();
                MarkNeedsPaint();
            }
            return;
        }

        if (_dragNodeId.HasValue)
        {
            var layout = _state.Graph.EditorData.NodeLayouts[_dragNodeId.Value];
            float dx = wx - _dragOffsetX - _dragNodeStartX;
            float dy = wy - _dragOffsetY - _dragNodeStartY;
            foreach (var pair in _dragOrigins)
                if (_state.Graph.EditorData.NodeLayouts.TryGetValue(pair.Key, out var moved))
                { moved.X = pair.Value.X + dx; moved.Y = pair.Value.Y + dy; }
            _insert = InsertTarget(nodeId: _dragNodeId.Value, sx: point.X, sy: point.Y);
            InvalidateView();
            return;
        }

        if (_dragPin.HasValue)
        {
            _mousePinWorldX = wx;
            _mousePinWorldY = wy;

            // Highlight the pin under the cursor (red if illegal) and snap the wire to the nearest
            // compatible pin within a small screen-space radius (green beacons mark all valid targets).
            var hover = HitTestPin(wx: wx, wy: wy);
            if (hover.HasValue && hover.Value.endpoint != _dragPin.Value)
            {
                _dragHoverPin = hover.Value.endpoint;
                _dragHoverValid = IsCompatibleTarget(hover.Value.endpoint);
            }
            else
                _dragHoverPin = null;

            _connectionError = _dragHoverPin is { } hovered && !GraphConnections.CanConnect(
                _state.Graph, _state.Registry, _dragPin.Value, hovered, out var reason, _rerouteEdge?.Id) ? reason : null;
            _dragSnapPin = _dragHoverPin.HasValue && !_dragHoverValid ? null : NearestCompatiblePin(wx: wx, wy: wy, worldRadius: 18f / _zoom);
            if (_dragSnapPin is { } snap && GetPinWorldPos(snap) is { } sp)
            {
                _mousePinWorldX = sp.wx;
                _mousePinWorldY = sp.wy;
            }

            return;
        }

        if (_isBoxSelecting)
        {
            _boxEndX = point.X;
            _boxEndY = point.Y;
            UpdateBoxSelection();
            return;
        }

        // Update hovered edge when not interacting
        _hoveredEdgeId = HitTestEdge(sx: point.X, sy: point.Y);
    }

    public override void OnPointerUp(Offset point)
    {
        float wx = ScreenToWorldX(point.X);
        float wy = ScreenToWorldY(point.Y);

        // Feature 1: commit 2-axis resize via command
        if (_resizeNodeId.HasValue)
        {
            if (_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: _resizeNodeId.Value,
                    value: out var rLayout
                ))
            {
                float finalWidth = rLayout.Width;
                float finalHeight = rLayout.Height;
                rLayout.Width = _resizeStartWidth; // restore so Execute sets correct final
                rLayout.Height = _resizeStartHeight;
                _state.Commands.Execute(
                    new ResizeNodeCommand(
                        nodeId: _resizeNodeId.Value,
                        oldWidth: _resizeStartWidth,
                        newWidth: finalWidth,
                        oldHeight: _resizeStartHeight,
                        newHeight: finalHeight
                    )
                );
            }

            _resizeNodeId = null;
            return;
        }

        if (_editingProp is { } editTarget)
        {
            var node = _state.Graph.FindNode(editTarget.NodeId);
            var released = HitTestPropRow(wx: ScreenToWorldX(point.X), wy: ScreenToWorldY(point.Y));
            _editingProp = null;
            if (node is null) return;
            var final = node.Properties.GetValueOrDefault(editTarget.PropId) ?? GraphValue.Null();
            if (!IsNumeric(editTarget.Def))
            {
                if (_editMoved || released is not { } hit || hit.nodeId != editTarget.NodeId || hit.prop.Id != editTarget.PropId) return;
                var current = _editOriginalValue.IsNull ? editTarget.Def.DefaultValue : _editOriginalValue;
                final = editTarget.Def.Type == GraphTypeRef.Bool
                    ? GraphValue.FromBool(current.IsNull || !current.AsBool())
                    : GraphValue.FromInt(((current.IsNull ? -1 : current.AsInt()) + 1) % editTarget.Def.EnumLabels!.Length);
            }
            RestoreProperty(node, editTarget.PropId);
            if (IsNumeric(editTarget.Def) && !_editMoved)
            {
                BeginTextEdit(editTarget);
                return;
            }
            if (final.ToString() != _editOriginalValue.ToString())
                _state.Commands.Execute(new ChangeNodePropertyCommand(editTarget.NodeId, editTarget.PropId, _editOriginalValue, final));
            MarkNeedsPaint();
            return;
        }

        if (_dragNodeId.HasValue &&
            _state.Graph.EditorData.NodeLayouts.TryGetValue(
                key: _dragNodeId.Value,
                value: out var nodeLayout
            ))
        {
            var commands = new List<IGraphCommand>();
            foreach (var pair in _dragOrigins)
                if (_state.Graph.EditorData.NodeLayouts.TryGetValue(pair.Key, out var moved))
                {
                    float x = moved.X, y = moved.Y;
                    moved.X = pair.Value.X; moved.Y = pair.Value.Y;
                    if (x != moved.X || y != moved.Y) commands.Add(new MoveNodeCommand(pair.Key, x, y));
                }
            if (commands.Count > 0 && _insert is { } splice)
            {
                // Dropped on a cable: the move and the re-wiring are one undo step.
                commands.Add(new DeleteEdgeCommand(splice.Edge.Id));
                commands.Add(new AddEdgeCommand(new GraphEdge {
                    From = splice.Edge.From, To = new GraphPinEndpoint(_dragNodeId.Value, splice.In),
                }));
                commands.Add(new AddEdgeCommand(new GraphEdge {
                    From = new GraphPinEndpoint(_dragNodeId.Value, splice.Out), To = splice.Edge.To,
                }));
            }

            if (commands.Count > 0) _state.Commands.Execute(new GraphGesture(commands.ToArray()));
            else if (_collapseOnRelease == _dragNodeId) _state.Select(_dragNodeId);
            _dragOrigins.Clear();
        }

        _insert = null;
        _collapseOnRelease = null;
        _dragNodeId = null;

        if (_dragPin.HasValue)
        {
            var hit = HitTestPin(wx, wy);
            var target = hit?.endpoint ?? _dragSnapPin;
            if (_rerouteEdge is { } old)
            {
                // Re-plug: one undo step moves the cable. Dropped back where it was: no change.
                // Dropped on empty canvas: unplugged. Dropped anywhere else: left as it was.
                if (target is { } replug && replug != old.To)
                {
                    if (GraphConnections.CanConnect(_state.Graph, _state.Registry, old.From, replug, out var why, old.Id))
                        _state.Commands.Execute(new GraphGesture(new DeleteEdgeCommand(old.Id),
                            new AddEdgeCommand(new GraphEdge { From = old.From, To = replug })));
                    else _connectionError = why;
                }
                else if (target is null && HitTestNode(wx, wy) is null && Bounds.Contains(point.X, point.Y))
                    _state.Commands.Execute(new DeleteEdgeCommand(old.Id));
            }
            else if (target is { } t)
            {
                if (GraphConnections.CanConnect(_state.Graph, _state.Registry, _dragPin.Value, t, out var reason))
                {
                    var from = _dragPinDir == PinDirection.Output ? _dragPin.Value : t;
                    var to = _dragPinDir == PinDirection.Input ? _dragPin.Value : t;
                    _state.Commands.Execute(new AddEdgeCommand(new GraphEdge { From = from, To = to }));
                    _connectionError = null;
                }
                else _connectionError = reason;
            }
            else if (HitTestNode(wx, wy) is null && Bounds.Contains(point.X, point.Y))
            {
                SearchConnection = _dragPin.Value;
                SearchAnchor = point;
                SearchRequested?.Invoke(wx, wy);
            }
            else _connectionError = "Drop on a socket, or on empty space to add a connected node.";

            _dragPin = null;
            _dragHoverPin = null;
            _dragSnapPin = null;
            _rerouteEdge = null;
            MarkNeedsPaint();
        }

        if (_isBoxSelecting)
        {
            _isBoxSelecting = false;
            UpdateBoxSelection();
            MarkNeedsPaint();
        }
    }

    public override void OnRightClick(Offset point)
    {
        // Start tracking a potential pan. Whether it turns into a pan or a
        // context-menu depends on how much the mouse moves before release.
        _isPanning = true;
        _panDragged = false;
        _panMouseX = point.X;
        _panMouseY = point.Y;
        _panMouseStartX = point.X;
        _panMouseStartY = point.Y;
    }

    public override void OnRightPointerUp(Offset point)
    {
        _isPanning = false;

        if (!_panDragged)
            // Stationary RMB → show context menu.
            ShowContextMenu(point);
    }

    public override void OnScroll(float dx, float dy)
    {
        // Two fingers on a trackpad pan; a mouse wheel zooms; Ctrl/Cmd + scroll zooms on either.
        // The native layer says which device scrolled (App.CurrentScrollPrecise). Off macOS that
        // flag is only "the delta was fractional", so the verdict holds for 400 ms: a whole step
        // in the middle of a swipe must not zoom.
        long now = Environment.TickCount64;
        if (Owner?.CurrentScrollPrecise ?? PreciseScroll) _trackpadTick = now;
        bool trackpad = now - _trackpadTick < 400;
        if (trackpad && !Mods.HasCommand())
        {
            // Same directions as a ScrollView: content follows the fingers.
            _panX -= dx * PanPerScroll;
            _panY += dy * PanPerScroll;
            InvalidateView();
            return;
        }

        ZoomAt(factor: 1f + (dy * 0.08f), sx: _mouseX, sy: _mouseY);
    }

    private const float PanPerScroll = 40f; // the ScrollView's speed, so the canvas feels the same

    /// <summary>Scales the view by <paramref name="factor" />, keeping the screen point fixed.</summary>
    private void ZoomAt(float factor, float sx, float sy)
    {
        float oldZoom = _zoom;
        _zoom = Math.Clamp(value: _zoom * factor, min: 0.15f, max: 4f);
        float applied = _zoom / oldZoom;
        _panX = ((sx - Bounds.X) * (1f - applied)) + (_panX * applied);
        _panY = ((sy - Bounds.Y) * (1f - applied)) + (_panY * applied);
        // Relayout so the output preview / glass re-anchors to the zoomed node.
        InvalidateView();
    }

    // Touch screens: two fingers pan (their centroid) and pinch to zoom, as one gesture.
    public override bool CanTouchScale() => true;

    public override void OnTouchScale(float scale, Offset focus) => ZoomAt(factor: scale, sx: focus.X, sy: focus.Y);

    public override bool CanTouchScroll(bool vertical) => true;

    public override void OnTouchScroll(float dx, float dy)
    {
        _panX += dx;
        _panY += dy;
        InvalidateView();
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        const uint scDelete = 76;
        const uint scBackspace = 42;

        _modifiers = mods;
        if (!down) return;
        if (scancode == 41)
        {
            // Escape backs out of whatever is in progress; with nothing in progress it deselects.
            bool busy = _dragPin.HasValue || _dragNodeId.HasValue || _resizeNodeId.HasValue || _editingProp is not null ||
                        _isBoxSelecting || _isPanning || SearchConnection.HasValue || _connectionError is not null;
            OnPointerCancel();
            CancelNodeSearch();
            _connectionError = null;
            if (!busy) _state.ClearSelection();
            return;
        }

        // Arrows nudge the selection: one grid cell, or a single unit with Shift for fine placement.
        if (scancode is >= 79 and <= 82 && _state.SelectedNodes.Count > 0)
        {
            float step = mods.HasFlag(Modifiers.Shift) ? 1f : GridSpacing;
            NudgeSelection(
                dx: scancode == 79 ? step : scancode == 80 ? -step : 0f,
                dy: scancode == 81 ? step : scancode == 82 ? -step : 0f
            );
            return;
        }

        if (mods.HasCommand() && char.ToLowerInvariant(keyChar) == 'd') { DuplicateSelection(); return; }
        if (mods.HasCommand() && char.ToLowerInvariant(keyChar) == 'c') { CopySelection(); return; }
        if (mods.HasCommand() && char.ToLowerInvariant(keyChar) == 'x') { CopySelection(); DeleteSelection(); return; }
        if (mods.HasCommand() && char.ToLowerInvariant(keyChar) == 'v') { PasteClipboard(); return; }
        if (scancode == 74 || (!mods.HasCommand() && char.ToLowerInvariant(keyChar) == 'f')) { FrameAll(selectionOnly: true); InvalidateView(); return; }
        if (char.ToLowerInvariant(keyChar) == 'h' && _state.SelectedNodes.Count > 0)
        {
            var nodes = _state.SelectedNodes.Where(id => _state.Graph.EditorData.NodeLayouts.ContainsKey(id)).ToArray();
            if (nodes.Length == 0) return;
            bool collapse = nodes.Any(id => !_state.Graph.EditorData.NodeLayouts[id].IsCollapsed);
            _state.Commands.Execute(new CollapseNodesCommand(nodes, collapse));
            return;
        }

        if (scancode == scDelete || scancode == scBackspace)
        {
            // Delete selected edge
            if (_state.SelectedEdges.Count > 0)
            {
                DeleteSelectedEdges();
                return;
            }

            DeleteSelection();
            return;
        }

        if (keyChar == ' ' || (mods.HasFlag(Modifiers.Shift) && char.ToLowerInvariant(keyChar) == 'a'))
        {
            SearchConnection = null;
            SearchAnchor = Bounds.Contains(_mouseX, _mouseY) ? new Offset(_mouseX, _mouseY)
                : new Offset(Bounds.X + Bounds.Width * 0.5f, Bounds.Y + Bounds.Height * 0.5f);
            SearchRequested?.Invoke(ScreenToWorldX(SearchAnchor.X), ScreenToWorldY(SearchAnchor.Y));
            return;
        }

        if (mods.HasCommand())
        {
            if (char.ToLower(keyChar) == 'z') { if (mods.HasFlag(Modifiers.Shift)) _state.Commands.Redo(); else _state.Commands.Undo(); }
            if (char.ToLower(keyChar) == 'y') _state.Commands.Redo();
            if (char.ToLower(keyChar) == 'a')
            {
                _state.SetSelection(_state.Graph.Nodes.Select(n => n.Id));
            }
        }
    }

    // ── Hit testing ───────────────────────────────────────────────────────────

    private Guid? HitTestResizeGrip(float wx, float wy)
    {
        for (int i = _state.Graph.Nodes.Count - 1; i >= 0; i--)
        {
            var node = _state.Graph.Nodes[i];
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            float ww = MathF.Max(
                x: layout.Width > 0 ? layout.Width : NodeMinWidth,
                y: NodeMinWidth
            );
            float wh = EffectiveHeight(layout: layout, def: def);

            if (layout.IsCollapsed) continue;
            float gripX = layout.X + ww - NodeResizeGrip;
            float gripY = layout.Y + wh - NodeResizeGrip;
            if (wx >= gripX && wx <= layout.X + ww && wy >= gripY && wy <= layout.Y + wh)
                return node.Id;
        }

        return null;
    }

    private Guid? HitTestNode(float wx, float wy)
    {
        for (int i = _state.Graph.Nodes.Count - 1; i >= 0; i--)
        {
            var node = _state.Graph.Nodes[i];
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            float ww = MathF.Max(
                x: layout.Width > 0 ? layout.Width : NodeMinWidth,
                y: NodeMinWidth
            );
            float wh = EffectiveHeight(layout: layout, def: def);

            if (!layout.IsCollapsed && _outputPreviewWidget is not null && node.Id == _outputNodeId)
                // Preview is fitted to node width; extend height only, not width.
                wh += ((PreviewPad * 2f) + _outputPreviewMeasuredSize.Height) / _zoom;

            if (wx >= layout.X && wx <= layout.X + ww && wy >= layout.Y && wy <= layout.Y + wh)
                return node.Id;
        }

        return null;
    }

    private (GraphPinEndpoint endpoint, PinDirection dir, float wx, float wy)?
        HitTestPin(float wx, float wy)
    {
        float pickRadius = MathF.Max(NodePinRadius, 10f / _zoom);
        foreach (var node in _state.Graph.Nodes)
        {
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            if (def is null) continue;

            float ww = MathF.Max(
                x: layout.Width > 0 ? layout.Width : NodeMinWidth,
                y: NodeMinWidth
            );
            float baseY = layout.Y + NodeHeaderH + 3f;

            for (int i = 0; i < def.Inputs.Count; i++)
            {
                float pinX = layout.X;
                float pinY = PinY(layout, def, i, false);
                if (MathF.Abs(wx - pinX) < pickRadius && MathF.Abs(wy - pinY) < pickRadius)
                {
                    return (new GraphPinEndpoint(NodeId: node.Id, PinId: def.Inputs[i].Id),
                        PinDirection.Input,
                        pinX, pinY);
                }
            }

            for (int i = 0; i < def.Outputs.Count; i++)
            {
                float pinX = layout.X + ww;
                float pinY = PinY(layout, def, i, true);
                if (MathF.Abs(wx - pinX) < pickRadius && MathF.Abs(wy - pinY) < pickRadius)
                {
                    return (new GraphPinEndpoint(NodeId: node.Id, PinId: def.Outputs[i].Id),
                        PinDirection.Output,
                        pinX, pinY);
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Hit-test bezier edges in screen space using 24 sample points per edge.
    ///     Returns the first edge whose curve passes within 6 screen pixels of <paramref name="sx" />/
    ///     <paramref name="sy" />.
    /// </summary>
    private Guid? HitTestEdge(float sx, float sy, float hitRadius = 6f)
    {
        foreach (var edge in _state.Graph.Edges)
            if (SampleEdge(edge, (x, y) => MathF.Abs(sx - x) < hitRadius && MathF.Abs(sy - y) < hitRadius))
                return edge.Id;
        return null;
    }

    /// <summary>True when <paramref name="hit" /> accepts any of 25 screen points along the cable.</summary>
    private bool SampleEdge(GraphEdge edge, Func<float, float, bool> hit)
    {
        var fromPin = GetPinWorldPos(edge.From);
        var toPin = GetPinWorldPos(edge.To);
        if (!fromPin.HasValue || !toPin.HasValue) return false;

        float ex0 = WorldToScreenX(fromPin.Value.wx);
        float ey0 = WorldToScreenY(fromPin.Value.wy);
        float ex1 = WorldToScreenX(toPin.Value.wx);
        float ey1 = WorldToScreenY(toPin.Value.wy);

        float absDx = MathF.Abs(ex1 - ex0);
        float absDy = MathF.Abs(ey1 - ey0);
        float handleLen = MathF.Max(x: (absDx * 0.5f) + (absDy * 0.25f), y: 24f);
        float cx0 = ex0 + handleLen;
        float cx1 = ex1 - handleLen;

        for (int i = 0; i <= 24; i++)
        {
            float t = i / 24f;
            float it = 1f - t;
            float nx = (it * it * it * ex0) + (3 * it * it * t * cx0) + (3 * it * t * t * cx1) +
                       (t * t * t * ex1);
            float ny = (it * it * it * ey0) + (3 * it * it * t * ey0) + (3 * it * t * t * ey1) +
                       (t * t * t * ey1);
            if (hit(nx, ny)) return true;
        }

        return false;
    }

    private (float wx, float wy)? GetPinWorldPos(GraphPinEndpoint ep)
    {
        var node = _state.Graph.FindNode(ep.NodeId);
        if (node is null) return null;
        if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(key: ep.NodeId, value: out var layout))
            return null;
        var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
        if (def is null) return null;

        float ww = MathF.Max(x: layout.Width > 0 ? layout.Width : NodeMinWidth, y: NodeMinWidth);
        float baseY = layout.Y + NodeHeaderH + 3f;

        for (int i = 0; i < def.Inputs.Count; i++)
        {
            if (def.Inputs[i].Id == ep.PinId)
                return (layout.X, PinY(layout, def, i, false));
        }

        for (int i = 0; i < def.Outputs.Count; i++)
        {
            if (def.Outputs[i].Id == ep.PinId)
            {
                return (layout.X + ww,
                    PinY(layout, def, i, true));
            }
        }

        return null;
    }

    // ── Connection compatibility ──────────────────────────────────────────────

    private (PinDirection dir, GraphTypeRef type)? ResolvePin(GraphPinEndpoint ep)
    {
        var node = _state.Graph.FindNode(ep.NodeId);
        if (node is null) return null;
        var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
        if (def is null) return null;
        foreach (var p in def.Inputs)
        {
            if (p.Id == ep.PinId)
                return (PinDirection.Input, p.Type);
        }

        foreach (var p in def.Outputs)
        {
            if (p.Id == ep.PinId)
                return (PinDirection.Output, p.Type);
        }

        return null;
    }

    private static bool TypesCompatible(GraphTypeRef a, GraphTypeRef b) =>
        a == b || a == GraphTypeRef.Any || b == GraphTypeRef.Any;

    /// <summary>
    ///     Whether the in-flight wire from <see cref="_dragPin" /> can legally connect to
    ///     <paramref name="cand" />:
    ///     not the same node, opposite pin direction, compatible types, and accepted by the domain.
    /// </summary>
    private bool IsCompatibleTarget(GraphPinEndpoint cand) => _dragPin is { } source &&
        GraphConnections.CanConnect(_state.Graph, _state.Registry, source, cand, out _, _rerouteEdge?.Id);

    /// <summary>Nearest compatible target pin within <paramref name="worldRadius" /> of (wx, wy), or null.</summary>
    private GraphPinEndpoint? NearestCompatiblePin(float wx, float wy, float worldRadius)
    {
        GraphPinEndpoint? best = null;
        float bestD = worldRadius * worldRadius;

        foreach (var node in _state.Graph.Nodes)
        {
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            if (def is null) continue;
            float ww = MathF.Max(
                x: layout.Width > 0 ? layout.Width : NodeMinWidth,
                y: NodeMinWidth
            );
            float baseY = layout.Y + NodeHeaderH + 3f;

            for (int i = 0; i < def.Inputs.Count; i++)
            {
                Consider(
                    ep: new GraphPinEndpoint(NodeId: node.Id, PinId: def.Inputs[i].Id),
                    px: layout.X,
                    py: PinY(layout, def, i, false)
                );
            }

            for (int i = 0; i < def.Outputs.Count; i++)
            {
                Consider(
                    ep: new GraphPinEndpoint(NodeId: node.Id, PinId: def.Outputs[i].Id),
                    px: layout.X + ww,
                    py: PinY(layout, def, i, true)
                );
            }
        }

        return best;

        void Consider(GraphPinEndpoint ep, float px, float py)
        {
            if (!IsCompatibleTarget(ep)) return;
            float d = ((px - wx) * (px - wx)) + ((py - wy) * (py - wy));
            if (d < bestD)
            {
                bestD = d;
                best = ep;
            }
        }
    }

    private (Guid nodeId, PropertyDefinition prop, int propIdx)? HitTestPropRow(float wx, float wy)
    {
        if (_zoom < ZoomLodFull) return null;
        foreach (var node in _state.Graph.Nodes)
        {
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            if (def is null || def.Properties.Count == 0 || layout.IsCollapsed) continue;
            float ww = MathF.Max(
                x: layout.Width > 0 ? layout.Width : NodeMinWidth,
                y: NodeMinWidth
            );
            if (wx < layout.X || wx > layout.X + ww) continue;
            float propsStartY = layout.Y + TotalBodyH(def) - PropsBodyH(def);
            for (int i = 0, row = 0; i < def.Properties.Count; i++)
            {
                if (def.Properties[i].InspectorOnly) continue;
                float rowY = propsStartY + NodePropSepH + 2f + (row++ * NodePropH);
                if (wy >= rowY && wy < rowY + NodePropH)
                    return (node.Id, def.Properties[i], i);
            }
        }

        return null;
    }

    private void UpdateBoxSelection()
    {
        float wx0 = ScreenToWorldX(MathF.Min(x: _boxStartX, y: _boxEndX));
        float wy0 = ScreenToWorldY(MathF.Min(x: _boxStartY, y: _boxEndY));
        float wx1 = ScreenToWorldX(MathF.Max(x: _boxStartX, y: _boxEndX));
        float wy1 = ScreenToWorldY(MathF.Max(x: _boxStartY, y: _boxEndY));

        // Any overlap with the whole card counts — not just its header band at minimum width.
        var hits = new List<Guid>(_boxBase);
        foreach (var node in _state.Graph.Nodes)
        {
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            float height = EffectiveHeight(layout: layout, def: _state.Registry.GetNodeDefinition(node.DefinitionId));
            if (layout.X < wx1 && layout.X + NodeWidth(layout) > wx0 &&
                layout.Y < wy1 && layout.Y + height > wy0)
                hits.Add(node.Id);
        }

        if (hits.Count > 0)
        {
            _state.SetSelection(hits);
            return;
        }

        // No card under the rectangle: it selects the cables it crosses instead.
        float sx0 = MathF.Min(x: _boxStartX, y: _boxEndX), sx1 = MathF.Max(x: _boxStartX, y: _boxEndX);
        float sy0 = MathF.Min(x: _boxStartY, y: _boxEndY), sy1 = MathF.Max(x: _boxStartY, y: _boxEndY);
        var cables = _state.Graph.Edges
            .Where(e => SampleEdge(e, (x, y) => x >= sx0 && x <= sx1 && y >= sy0 && y <= sy1))
            .Select(e => e.Id).ToArray();
        if (cables.Length > 0) _state.SetEdgeSelection(cables);
        else _state.SetSelection(hits);
    }

    // ── Selection operations ──────────────────────────────────────────────────

    private void DeleteSelection()
    {
        var ids = _state.SelectedNodes.ToArray();
        if (ids.Length == 0) return;
        _state.Commands.Execute(new GraphGesture(ids.Select(id => (IGraphCommand)new DeleteNodeCommand(id)).ToArray()));
    }

    private void DeleteSelectedEdges()
    {
        var cut = _state.SelectedEdges.Select(id => (IGraphCommand)new DeleteEdgeCommand(id)).ToArray();
        if (cut.Length > 0) _state.Commands.Execute(new GraphGesture(cut));
        _hoveredEdgeId = null;
    }

    /// <summary>Nodes lifted off a graph: enough to recreate them, and the cables between them.</summary>
    private sealed record Snapshot(
        string DomainId,
        (string DefinitionId, KeyValuePair<string, GraphValue>[] Properties, float X, float Y, float Width, float Height, bool Collapsed)[] Nodes,
        (int From, string FromPin, int To, string ToPin)[] Edges);

    private Snapshot? Capture()
    {
        var nodes = _state.Graph.Nodes
            .Where(n => _state.IsSelected(n.Id) && _state.Graph.EditorData.NodeLayouts.ContainsKey(n.Id)).ToArray();
        if (nodes.Length == 0) return null;
        var index = nodes.Select((n, i) => (n.Id, i)).ToDictionary(p => p.Id, p => p.i);
        return new Snapshot(
            DomainId: _state.Graph.DomainId,
            Nodes: nodes.Select(n =>
            {
                var layout = _state.Graph.EditorData.NodeLayouts[n.Id];
                return (n.DefinitionId, n.Properties.ToArray(), layout.X, layout.Y, layout.Width, layout.Height, layout.IsCollapsed);
            }).ToArray(),
            Edges: _state.Graph.Edges
                .Where(e => index.ContainsKey(e.From.NodeId) && index.ContainsKey(e.To.NodeId))
                .Select(e => (index[e.From.NodeId], e.From.PinId, index[e.To.NodeId], e.To.PinId)).ToArray());
    }

    /// <summary>
    ///     Recreates a snapshot shifted by (dx, dy) as one undo step and selects the new nodes.
    ///     Node types this graph's registry does not know are skipped, along with their cables.
    /// </summary>
    private void Paste(Snapshot snapshot, float dx, float dy)
    {
        var created = new GraphNode?[snapshot.Nodes.Length];
        var commands = new List<IGraphCommand>();
        for (int i = 0; i < snapshot.Nodes.Length; i++)
        {
            var source = snapshot.Nodes[i];
            if (_state.Registry.GetNodeDefinition(source.DefinitionId) is null) continue;
            var node = new GraphNode { DefinitionId = source.DefinitionId };
            foreach ((string key, var value) in source.Properties) node.Properties[key] = value;
            created[i] = node;
            commands.Add(new AddNodeCommand(node: node, x: source.X + dx, y: source.Y + dy));
        }

        if (commands.Count == 0) return;
        foreach (var edge in snapshot.Edges)
            if (created[edge.From] is { } from && created[edge.To] is { } to)
                commands.Add(new AddEdgeCommand(new GraphEdge {
                    From = new GraphPinEndpoint(from.Id, edge.FromPin),
                    To = new GraphPinEndpoint(to.Id, edge.ToPin),
                }));
        _state.Commands.Execute(new GraphGesture(commands.ToArray()));
        // AddNodeCommand seeds a default card; carry the original's size and fold state over.
        for (int i = 0; i < created.Length; i++)
        {
            if (created[i] is not { } node) continue;
            var target = _state.Graph.EditorData.NodeLayouts[node.Id];
            target.Width = snapshot.Nodes[i].Width;
            target.Height = snapshot.Nodes[i].Height;
            target.IsCollapsed = snapshot.Nodes[i].Collapsed;
        }

        _state.SetSelection(created.Where(n => n is not null).Select(n => n!.Id));
        InvalidateView();
    }

    /// <summary>The selected nodes again, with the cables between them, offset down-right.</summary>
    private void DuplicateSelection()
    {
        if (Capture() is { } snapshot) Paste(snapshot: snapshot, dx: 30f, dy: 30f);
    }

    private void CopySelection()
    {
        if (Capture() is not { } snapshot) return;
        _clipboard = snapshot;
        _pasteCount = 0;
    }

    /// <summary>
    ///     Pastes at the pointer when it is over the canvas (the group's top-left corner lands
    ///     there); otherwise beside the originals, stepping further with each repeat.
    /// </summary>
    private void PasteClipboard()
    {
        // ponytail: an in-process clipboard, not the system one — no pasting between two running
        // apps. That needs a text format for GraphValue, which the document serializers own.
        if (_clipboard is not { } snapshot || snapshot.DomainId != _state.Graph.DomainId) return;
        if (Bounds.Contains(px: _mouseX, py: _mouseY))
        {
            Paste(snapshot: snapshot,
                dx: ScreenToWorldX(_mouseX) - snapshot.Nodes.Min(n => n.X),
                dy: ScreenToWorldY(_mouseY) - snapshot.Nodes.Min(n => n.Y));
            return;
        }

        _pasteCount++;
        Paste(snapshot: snapshot, dx: 30f * _pasteCount, dy: 30f * _pasteCount);
    }

    /// <summary>
    ///     Where dropping the dragged node would splice it into a cable: only for a single node
    ///     with no cables of its own, over a cable it can take on an input and continue from an
    ///     output. Both new connections are checked against the same rules as a hand-drawn cable.
    /// </summary>
    private (GraphEdge Edge, string In, string Out)? InsertTarget(Guid nodeId, float sx, float sy)
    {
        if (_dragOrigins.Count != 1) return null;
        if (_state.Graph.Edges.Any(e => e.From.NodeId == nodeId || e.To.NodeId == nodeId)) return null;
        var node = _state.Graph.FindNode(nodeId);
        var def = node is null ? null : _state.Registry.GetNodeDefinition(node.DefinitionId);
        if (def is null || def.Inputs.Count == 0 || def.Outputs.Count == 0) return null;
        if (HitTestEdge(sx: sx, sy: sy, hitRadius: 14f) is not { } edgeId) return null;
        var edge = _state.Graph.Edges.First(e => e.Id == edgeId);
        var input = def.Inputs.FirstOrDefault(p => GraphConnections.CanConnect(
            _state.Graph, _state.Registry, edge.From, new GraphPinEndpoint(nodeId, p.Id), out _, edge.Id));
        var output = def.Outputs.FirstOrDefault(p => GraphConnections.CanConnect(
            _state.Graph, _state.Registry, new GraphPinEndpoint(nodeId, p.Id), edge.To, out _, edge.Id));
        return input is null || output is null ? null : (edge, input.Id, output.Id);
    }

    private void NudgeSelection(float dx, float dy)
    {
        var commands = new List<IGraphCommand>();
        foreach (var id in _state.SelectedNodes)
            if (_state.Graph.EditorData.NodeLayouts.TryGetValue(id, out var layout))
                commands.Add(new MoveNodeCommand(id, layout.X + dx, layout.Y + dy));
        if (commands.Count > 0) _state.Commands.Execute(new GraphGesture(commands.ToArray()));
    }

    // ── Color helpers ─────────────────────────────────────────────────────────

    internal static Color CategoryColor(string category)
    {
        string lower = category.ToLowerInvariant();
        if (lower.Contains("math")) return new Color(r: 0.25f, g: 0.48f, b: 0.70f);
        if (lower.Contains("texture")) return new Color(r: 0.55f, g: 0.30f, b: 0.65f);
        if (lower.Contains("output")) return new Color(0.32f, 0.17f, 0.22f);
        if (lower.Contains("source")) return new Color(0.23f, 0.34f, 0.42f);
        if (lower.Contains("amp")) return new Color(0.24f, 0.40f, 0.23f);
        if (lower.Contains("drive")) return new Color(0.46f, 0.29f, 0.17f);
        if (lower.Contains("dynamics")) return new Color(0.33f, 0.27f, 0.44f);
        if (lower.Contains("delay")) return new Color(0.18f, 0.37f, 0.40f);
        if (lower.Contains("modulation")) return new Color(0.20f, 0.33f, 0.52f);
        if (lower.Contains("filter")) return new Color(0.40f, 0.36f, 0.16f);
        if (lower.Contains("pitch")) return new Color(0.44f, 0.22f, 0.36f);
        if (lower.Contains("community")) return new Color(0.26f, 0.36f, 0.34f);
        if (lower.Contains("bus")) return new Color(0.37f, 0.31f, 0.19f);
        if (lower.Contains("input")) return new Color(r: 0.50f, g: 0.45f, b: 0.20f);
        if (lower.Contains("const")) return new Color(r: 0.48f, g: 0.38f, b: 0.22f);
        if (lower.Contains("flow")) return new Color(r: 0.35f, g: 0.35f, b: 0.55f);
        if (lower.Contains("event")) return new Color(r: 0.60f, g: 0.25f, b: 0.25f);
        return new Color(r: 0.22f, g: 0.22f, b: 0.28f);
    }

    private Color PinColor(GraphTypeRef type)
    {
        var def = _state.Registry.GetTypeDefinition(type.Id);
        if (def is null) return new Color(r: 0.5f, g: 0.5f, b: 0.5f);
        uint argb = def.WireColor;
        return new Color(
            r: ((argb >> 16) & 0xFF) / 255f,
            g: ((argb >> 8) & 0xFF) / 255f,
            b: (argb & 0xFF) / 255f
        );
    }

    private Color GetEdgeColor(GraphEdge edge)
    {
        var fromNode = _state.Graph.FindNode(edge.From.NodeId);
        if (fromNode is null) return new Color(r: 0.5f, g: 0.5f, b: 0.5f);
        var def = _state.Registry.GetNodeDefinition(fromNode.DefinitionId);
        var pin = def?.Outputs.FirstOrDefault(p => p.Id == edge.From.PinId);
        return pin is null ? new Color(r: 0.5f, g: 0.5f, b: 0.5f) : PinColor(pin.Type);
    }

    // ── Context menu ─────────────────────────────────────────────────────────

    private void ShowContextMenu(Offset screenPos)
    {
        float wx = ScreenToWorldX(screenPos.X);
        float wy = ScreenToWorldY(screenPos.Y);

        var hitEdge = HitTestEdge(sx: screenPos.X, sy: screenPos.Y);
        if (hitEdge.HasValue && HitTestNode(wx, wy) is null)
        {
            ShowEdgeContextMenu(edgeId: hitEdge.Value, screenPos: screenPos);
            return;
        }

        var hitNode = HitTestNode(wx: wx, wy: wy);
        if (hitNode.HasValue)
        {
            ShowNodeContextMenu(nodeId: hitNode.Value, screenPos: screenPos);
            return;
        }

        ShowAddNodeContextMenu(wx: wx, wy: wy, screenPos: screenPos);
    }

    private void ShowEdgeContextMenu(Guid edgeId, Offset screenPos)
    {
        // Like nodes: a right-click on one of several selected cables acts on all of them.
        if (!_state.IsEdgeSelected(edgeId)) _state.SelectEdge(edgeId);
        int count = _state.SelectedEdges.Count;
        new ContextMenu(new ContextMenuItem(
            Label: count > 1 ? $"Disconnect {count} cables" : "Disconnect cable",
            OnSelect: DeleteSelectedEdges
        )).ShowAt(screenPos);
    }

    private void ShowNodeContextMenu(Guid nodeId, Offset screenPos)
    {
        // Right-clicking one of several selected nodes acts on all of them; right-clicking a node
        // outside the selection selects just that one first.
        if (!_state.IsSelected(nodeId)) _state.Select(nodeId);
        int count = _state.SelectedNodes.Count;
        var node = _state.Graph.FindNode(nodeId);
        var def = node is null ? null : _state.Registry.GetNodeDefinition(node.DefinitionId);
        var ids = _state.SelectedNodes.Where(id => _state.Graph.EditorData.NodeLayouts.ContainsKey(id)).ToArray();
        bool collapse = ids.Any(id => !_state.Graph.EditorData.NodeLayouts[id].IsCollapsed);
        bool wired = _state.Graph.Edges.Any(e => _state.IsSelected(e.From.NodeId) || _state.IsSelected(e.To.NodeId));

        var items = new List<ContextMenuItem> {
            new(Label: count > 1 ? $"{count} nodes" : def?.DisplayName ?? "Node", OnSelect: null),
            new(Label: "", OnSelect: null, Separator: true),
            new(Label: "Copy", OnSelect: CopySelection),
            new(Label: "Cut", OnSelect: () => { CopySelection(); DeleteSelection(); }),
            new(Label: "Duplicate", OnSelect: DuplicateSelection),
            new(Label: collapse ? "Collapse" : "Expand",
                OnSelect: () => _state.Commands.Execute(new CollapseNodesCommand(ids, collapse))),
        };
        if (wired)
            items.Add(new ContextMenuItem(Label: "Disconnect cables", OnSelect: () =>
            {
                var cut = _state.Graph.Edges
                    .Where(e => _state.IsSelected(e.From.NodeId) || _state.IsSelected(e.To.NodeId))
                    .Select(e => (IGraphCommand)new DeleteEdgeCommand(e.Id)).ToArray();
                _state.Commands.Execute(new GraphGesture(cut));
            }));
        items.Add(new ContextMenuItem(Label: "", OnSelect: null, Separator: true));
        items.Add(new ContextMenuItem(Label: count > 1 ? $"Delete {count} nodes" : "Delete", OnSelect: DeleteSelection));
        new ContextMenu(items.ToArray()).ShowAt(screenPos);
    }

    private void ShowAddNodeContextMenu(float wx, float wy, Offset screenPos)
    {
        FinishTextEdit(true, true);
        SearchConnection = null;
        SearchAnchor = screenPos;
        SearchRequested?.Invoke(wx, wy);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void OpenNodeSearch()
    {
        SearchConnection = null;
        SearchAnchor = new Offset(Bounds.X + Bounds.Width * 0.5f, Bounds.Y + Bounds.Height * 0.35f);
        SearchRequested?.Invoke(ScreenToWorldX(SearchAnchor.X), ScreenToWorldY(SearchAnchor.Y));
    }

    public void CancelNodeSearch() { SearchConnection = null; MarkNeedsPaint(); }

    public void AddNodeFromSearch(string definitionId, string? pinId, float worldX, float worldY)
    {
        var source = SearchConnection;
        SearchConnection = null;
        if (source is null) { AddNode(definitionId, worldX, worldY); return; }
        if (pinId is null) return;
        var node = new GraphNode { DefinitionId = definitionId };
        var endpoint = new GraphPinEndpoint(node.Id, pinId);
        var preview = GraphConnections.WithCandidate(_state.Graph, node);
        if (!GraphConnections.CanConnect(preview, _state.Registry, source.Value, endpoint, out var reason))
        { _connectionError = reason; MarkNeedsPaint(); return; }
        bool output = GraphConnections.Resolve(_state.Graph, _state.Registry, source.Value)!.Value.Direction == PinDirection.Output;
        var edge = new GraphEdge { From = output ? source.Value : endpoint, To = output ? endpoint : source.Value };
        _state.Commands.Execute(new GraphGesture(new AddNodeCommand(node, output ? worldX : worldX - NodeMinWidth, worldY), new AddEdgeCommand(edge)));
        _state.Select(node.Id);
        InvalidateView();
    }

    public void AddNode(string definitionId, float worldX, float worldY)
    {
        var node = new GraphNode { DefinitionId = definitionId };
        _state.Commands.Execute(new AddNodeCommand(node: node, x: worldX, y: worldY));
        _state.Select(node.Id);
    }

    /// <summary>Fits the view to every node, or only to the selected ones.</summary>
    public void FrameAll(bool selectionOnly = false)
    {
        selectionOnly &= _state.SelectedNodes.Count > 0;
        if (_state.Graph.Nodes.Count == 0)
        {
            _panX = _panY = 0;
            _zoom = 1f;
            InvalidateView();
            return;
        }

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        foreach (var node in _state.Graph.Nodes)
        {
            if (selectionOnly && !_state.IsSelected(node.Id)) continue;
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            float nodeH = EffectiveHeight(layout: layout, def: def);
            if (!layout.IsCollapsed && node.Id == _outputNodeId && _outputPreviewMeasuredSize.Height > 0)
                nodeH += (PreviewPad * 2f) + (_outputPreviewMeasuredSize.Height / _zoom);
            minX = MathF.Min(x: minX, y: layout.X);
            minY = MathF.Min(x: minY, y: layout.Y);
            maxX = MathF.Max(x: maxX, y: layout.X + MathF.Max(NodeMinWidth, layout.Width));
            maxY = MathF.Max(x: maxY, y: layout.Y + nodeH);
        }

        float pad = 60f;
        float gw = maxX - minX + (pad * 2);
        float gh = maxY - minY + (pad * 2);
        _zoom = Math.Clamp(
            value: MathF.Min(x: _size.Width / gw, y: _size.Height / gh),
            min: 0.2f,
            max: 1.25f
        );
        _panX = (_size.Width * 0.5f) - ((minX + (gw * 0.5f) - pad) * _zoom);
        _panY = (_size.Height * 0.5f) - ((minY + (gh * 0.5f) - pad) * _zoom);
        InvalidateView();
    }

    public override IEnumerable<Widget> GetChildren()
    {
        if (_textEditor is not null) yield return _textEditor;
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _textEditor?.HitTest(point) ?? this;
    }

    private void BeginTextEdit(PropEditTarget target)
    {
        var node = _state.Graph.FindNode(target.NodeId);
        if (node is null) return;
        var value = node.Properties.GetValueOrDefault(target.PropId) ?? target.Def.DefaultValue;
        _textTarget = target;
        _textError = null;
        _textEditor = new InlineTextField {
            ShowBackground = false,
            MinWidth = 0f,
            // Checked as it is typed: the border and the hint strip say so before Enter does.
            OnChanged = text => { _textError = LiveError(target.Def, text); MarkNeedsPaint(); },
            Accept = AcceptFor(target.Def),
            // A float at its control's precision, not its storage noise (0.46, not 0.45999998).
            Text = value.IsNull ? "" : target.Def.Type == GraphTypeRef.Float
                ? Math.Round(value.AsFloat(), Decimals(target.Def)).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString(),
            Cancel = () => FinishTextEdit(false),
            OnSubmitted = _ => FinishTextEdit(true),
            OnFocusChange = focused => { if (!focused) FinishTextEdit(true, true); },
        };
        MarkNeedsLayout();
        LayoutTextEditor();
        AppInstance.Active?.RequestFocus(_textEditor);
        _textEditor?.SelectAll();
    }

    private void LayoutTextEditor()
    {
        if (_textEditor is null || _textTarget is not { } target) return;
        if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(target.NodeId, out var layout)) return;
        var node = _state.Graph.FindNode(target.NodeId);
        var def = node is null ? null : _state.Registry.GetNodeDefinition(node.DefinitionId);
        if (def is null) return;
        int index = InlineIndex(def, target.Def);
        float width = MathF.Max(NodeMinWidth, layout.Width);
        float controlWidth = Scale(width - 20f);
        float fontSize = MathF.Max(7f, 11.5f * _zoom);
        float unitWidth = string.IsNullOrEmpty(target.Def.Unit) ? 0f : target.Def.Unit.Length * fontSize * 0.58f + Scale(8f);
        _textEditor.FontSize = fontSize;
        _textEditor.Measure(Constraints.Tight(MathF.Max(0f, controlWidth * 0.55f - unitWidth), Scale(NodePropH - 4f)));
        _textEditor.Layout(new Offset(WorldToScreenX(layout.X + 10f) + controlWidth * 0.45f,
            WorldToScreenY(layout.Y + PinsBodyH(def) + NodePropSepH + 4f + index * NodePropH)));
    }

    /// <summary>Decimals a float control can carry: those of its drag step, three without one.</summary>
    private static int Decimals(PropertyDefinition def)
    {
        if (def.Type != GraphTypeRef.Float) return 0;
        if (def.Step is not { } step || step <= 0f) return 3;
        return Math.Clamp((int)Math.Ceiling(-Math.Log10(step) - 1e-6), 0, 6);
    }

    private static bool TryParseNumber(string text, out float number) =>
        float.TryParse(text.Trim().Replace(',', '.'),
            System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out number) && float.IsFinite(number);

    /// <summary>
    ///     Which typed characters a numeric control lets in: digits, a minus when its range goes
    ///     negative, a decimal separator when it has decimals. Text properties take anything.
    /// </summary>
    private static Func<char, bool>? AcceptFor(PropertyDefinition def)
    {
        if (!IsNumeric(def)) return null;
        bool signed = def.Min is not { } min || min < 0f;
        bool fractional = Decimals(def) > 0;
        return ch => char.IsAsciiDigit(ch) || (ch == '-' && signed) || (ch is '.' or ',' && fractional);
    }

    /// <summary>What is wrong with the text so far, or null. Committing still clamps.</summary>
    private static string? LiveError(PropertyDefinition def, string text)
    {
        if (!IsNumeric(def) || text.Length == 0) return null;
        if (!TryParseNumber(text, out float number))
            return text is "-" or "." or "," or "-." ? null : "Not a number · Esc cancels";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (def.Min is { } min && number < min) return $"Below the minimum of {min.ToString(inv)} · Enter clamps";
        if (def.Max is { } max && number > max) return $"Above the maximum of {max.ToString(inv)} · Enter clamps";
        return null;
    }

    private void FinishTextEdit(bool commit, bool losingFocus = false)
    {
        if (_textEditor is null || _textTarget is not { } target) return;
        string text = _textEditor.Text;
        GraphValue? value = null;
        if (target.Def.Type == GraphTypeRef.String) value = GraphValue.FromString(text);
        else if (TryParseNumber(text, out float number))
        {
            number = Math.Clamp(number, target.Def.Min ?? float.MinValue,
                MathF.Max(target.Def.Min ?? float.MinValue, target.Def.Max ?? float.MaxValue));
            value = target.Def.Type == GraphTypeRef.Int
                ? GraphValue.FromInt((int)Math.Clamp(Math.Round(number), int.MinValue, int.MaxValue))
                : GraphValue.FromFloat(number);
        }
        if (commit && value is null && !losingFocus)
        {
            _textError = "Enter a finite number · Esc cancels";
            MarkNeedsPaint();
            return;
        }
        _textError = null;
        _textEditor = null;
        _textTarget = null;
        var node = _state.Graph.FindNode(target.NodeId);
        if (commit && value is not null && node is not null)
        {
            var old = node.Properties.GetValueOrDefault(target.PropId) ?? GraphValue.Null();
            var effective = old.IsNull ? target.Def.DefaultValue : old;
            if (value.ToString() != effective.ToString())
                _state.Commands.Execute(new ChangeNodePropertyCommand(target.NodeId, target.PropId, old, value));
        }
        if (!losingFocus) AppInstance.Active?.RequestFocus(this);
        MarkNeedsLayout();
    }

    private sealed class InlineTextField : AdwTextField
    {
        public Action? Cancel { get; init; }

        /// <summary>Typed characters that get in; null lets everything through.</summary>
        public Func<char, bool>? Accept { get; init; }

        public override void OnTextInput(string text)
        {
            if (Accept is { } accept)
                text = new string(text.Where(accept).Select(ch => ch == ',' ? '.' : ch).ToArray());
            if (text.Length > 0) base.OnTextInput(text);
        }

        public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
        {
            if (down && scancode == 41) { Cancel?.Invoke(); return; }
            base.OnKey(keyChar, scancode, down, mods);
        }
    }

    private void RestoreProperty(GraphNode node, string key)
    {
        if (_editOriginalValue.IsNull) node.Properties.Remove(key);
        else node.Properties[key] = _editOriginalValue;
    }

    public override void OnPointerCancel()
    {
        if (_editingProp is { } edit)
        {
            var node = _state.Graph.FindNode(edit.NodeId);
            if (node is not null) RestoreProperty(node, edit.PropId);
            _editingProp = null;
            if (_state.AutoCompile) _state.TriggerCompile();
        }
        foreach (var pair in _dragOrigins)
            if (_state.Graph.EditorData.NodeLayouts.TryGetValue(pair.Key, out var layout))
            { layout.X = pair.Value.X; layout.Y = pair.Value.Y; }
        _dragOrigins.Clear();
        _dragNodeId = null;
        if (_resizeNodeId is { } resized && _state.Graph.EditorData.NodeLayouts.TryGetValue(resized, out var size))
        { size.Width = _resizeStartWidth; size.Height = _resizeStartHeight; }
        _resizeNodeId = null;
        _dragPin = null;
        _dragHoverPin = null;
        _dragSnapPin = null;
        _rerouteEdge = null;
        _insert = null;
        _collapseOnRelease = null;
        _isPanning = false;
        if (_isBoxSelecting) _state.SetSelection(_boxBase); // an abandoned marquee selects nothing new
        _isBoxSelecting = false;
        InvalidateView();
    }

    // Inline property editing
    private record struct PropEditTarget(
        Guid NodeId,
        string PropId,
        PropertyDefinition Def,
        Rect ScreenRect);
}
