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

    private const float NodeHeaderH = 22f;
    private const float NodePinH = 18f;
    private const float NodePinRadius = 5f;
    private const float NodePropH = 15f; // height per inline property row
    private const float NodePropSepH = 1f; // separator between pins and properties
    private const float NodeMinWidth = 150f;
    private const float NodePadX = 10f;
    private const float GridSpacing = 40f;
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

    public GraphCanvas(GraphEditorState state, ThemeData theme)
    {
        _state = state;
        _theme = theme;

        _state.CompileChanged += OnCompileChanged;
        OnCompileChanged(); // seed from any compile already done
    }

    /// <summary>
    ///     Whether the bottom-right minimap overview is drawn. Off by default; toggled from the
    ///     toolbar.
    /// </summary>
    public bool MinimapVisible { get; set; }

    public override bool Focusable => true;

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

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );

        if (_outputPreviewWidget is null || !_outputNodeId.HasValue ||
            !_state.Graph.EditorData.NodeLayouts.TryGetValue(
                key: _outputNodeId.Value,
                value: out var outLayout
            )) return;
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
        DrawNodes(paint);
        // Green halos on every compatible target pin (and a red ring on an invalid hovered pin) while
        // dragging a wire — shows valid/invalid connections live.
        if (_dragPin.HasValue) DrawConnectionBeacons(paint);
        // Render the output node's live widget preview on top of the node card.
        _outputPreviewWidget?.Paint(paint);
        if (_isBoxSelecting) DrawBoxSelection(paint);

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
        if (spacing < 8f) return;

        // Two-tier grid: faint minor lines, stronger major lines every 5th world cell (which stay
        // anchored to the graph origin as you pan). Theme-derived so the grid flips with appearance
        // (white hairline in dark, black in light) instead of vanishing on a light canvas.
        var major = _theme.Separator;
        var minor = _theme.Separator.WithAlpha(_theme.Separator.A * 0.5f);
        const int majorEvery = 5;

        float startX = ((_panX % spacing) + Bounds.X) % spacing;
        float startY = ((_panY % spacing) + Bounds.Y) % spacing;
        if (startX > Bounds.X) startX -= spacing;
        if (startY > Bounds.Y) startY -= spacing;

        for (float x = startX; x < Bounds.Right; x += spacing)
        {
            int cell = (int)MathF.Round(ScreenToWorldX(x) / GridSpacing);
            paint.AddRect(
                bounds: new Rect(
                    x: x,
                    y: Bounds.Y,
                    width: 1f,
                    height: Bounds.Height
                ),
                color: cell % majorEvery == 0 ? major : minor
            );
        }

        for (float y = startY; y < Bounds.Bottom; y += spacing)
        {
            int cell = (int)MathF.Round(ScreenToWorldY(y) / GridSpacing);
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X,
                    y: y,
                    width: Bounds.Width,
                    height: 1f
                ),
                color: cell % majorEvery == 0 ? major : minor
            );
        }
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
        int c = def?.Properties.Count ?? 0;
        return c > 0 ? NodePropSepH + (c * NodePropH) + 4f : 0f;
    }

    // Full node body height including inline property rows (world units).
    private static float TotalBodyH(NodeDefinition? def) => PinsBodyH(def) + PropsBodyH(def);

    // Effective node body height: the user-set layout.Height when larger than the auto content height,
    // else the auto height (so pins/props never clip; extra height is empty space below props).
    private static float EffectiveHeight(NodeLayoutData layout, NodeDefinition? def)
    {
        float min = TotalBodyH(def);
        return layout.Height > min ? layout.Height : min;
    }

    /// <summary>
    ///     Request a relayout so view-derived widgets (e.g. the output preview / LiquidGlass) track
    ///     pan/zoom/drag.
    /// </summary>
    private void InvalidateView() => MarkNeedsLayout();

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

        bool hasPreview = _outputPreviewWidget is not null && node.Id == _outputNodeId;

        float sx = WorldToScreenX(layout.X);
        float sy = WorldToScreenY(layout.Y);
        float sw = Scale(ww);
        // Preview is constrained to node card width (set in Layout), so sw never needs expanding.
        float sh = hasPreview
            ? Scale(totalBodyH) + (PreviewPad * 2f) + _outputPreviewMeasuredSize.Height
            : Scale(totalBodyH);

        // Feature 2: LOD level based on zoom
        int lod = _zoom >= ZoomLodFull ? 2 : _zoom >= ZoomLodMin ? 1 : 0;

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

        // Neutral raised header with a category-coloured status dot + title (the modern node look),
        // closed off by a hairline so the header reads as a distinct band over the body.
        var accent = CategoryColor(def?.Category ?? "");
        var headerRect = new Rect(
            x: sx,
            y: sy,
            width: sw,
            height: Scale(NodeHeaderH)
        );
        paint.AddRect(bounds: headerRect, color: _theme.PanelRaised, radius: radius);
        paint.AddRect(
            bounds: new Rect(
                x: sx,
                y: sy + Scale(NodeHeaderH) - (radius * 0.5f),
                width: sw,
                height: radius * 0.5f
            ),
            color: _theme.PanelRaised
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

        float fs = MathF.Max(x: 7f, y: 11f * _zoom);
        float dotR = MathF.Max(x: 2f, y: 3f * _zoom);
        float dotCx = sx + Scale(NodePadX) + dotR;
        float dotCy = sy + (Scale(NodeHeaderH) * 0.5f);
        paint.AddRect(
            bounds: new Rect(
                x: dotCx - dotR,
                y: dotCy - dotR,
                width: dotR * 2f,
                height: dotR * 2f
            ),
            color: accent,
            radius: dotR
        );

        string label = def?.DisplayName ?? node.DefinitionId;
        paint.AddText(
            text: label,
            baselineX: dotCx + dotR + Scale(5f),
            baselineY: dotCy + (fs * 0.36f),
            color: _theme.OnSurface,
            fontSize: fs
        );

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
            if (def.Properties.Count > 0 && lod >= 2)
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
        if (_zoom > 0.4f)
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
                color: _theme.Border,
                radius: radius,
                width: 0.75f
            );
        }
    }

    private void DrawPins(PaintList paint, GraphNode node, NodeDefinition def,
        float sx, float sy, float sw, int lod)
    {
        // LOD 0: skip pins entirely
        if (lod < 1) return;

        float fs = MathF.Max(x: 6f, y: 9.5f * _zoom);
        float pinR = NodePinRadius * _zoom;
        float rowH = NodePinH * _zoom;
        float baseY = sy + Scale(NodeHeaderH) + Scale(3f);

        for (int i = 0; i < def.Inputs.Count; i++)
        {
            var pin = def.Inputs[i];
            float pinY = baseY + (i * rowH) + (rowH * 0.5f);
            var col = PinColor(pin.Type);
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
            if (lod >= 2)
            {
                paint.AddText(
                    text: pin.DisplayName,
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
            float pinY = baseY + ((def.Inputs.Count + i) * rowH) + (rowH * 0.5f);
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
            if (lod >= 2)
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
        float pinsBottom = sy + Scale(PinsBodyH(def));

        // Subtle separator between pins area and properties area
        paint.AddRect(
            bounds: new Rect(
                x: sx + Scale(4f),
                y: pinsBottom,
                width: sw - Scale(8f),
                height: Scale(NodePropSepH)
            ),
            color: _theme.Separator
        );

        float fs = MathF.Max(x: 5.5f, y: 8f * _zoom);
        float rowH = Scale(NodePropH);
        float py = pinsBottom + Scale(NodePropSepH) + Scale(2f);

        for (int i = 0; i < def.Properties.Count; i++)
        {
            var prop = def.Properties[i];
            var val = node.Properties.TryGetValue(key: prop.Id, value: out var storedVal) &&
                      !storedVal.IsNull
                ? storedVal
                : prop.DefaultValue;
            string valueStr = FormatPropValue(prop: prop, val: val);
            string nameStr = prop.DisplayName;

            // Feature 5: highlight active editing row
            if (_editingProp.HasValue
                && _editingProp.Value.NodeId == node.Id
                && _editingProp.Value.PropId == prop.Id)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: sx,
                        y: py - Scale(1f),
                        width: sw,
                        height: rowH
                    ),
                    color: _theme.Primary.WithAlpha(0.18f)
                );
            }

            // Name — dim
            paint.AddText(
                text: nameStr,
                baselineX: sx + Scale(NodePadX),
                baselineY: py + (fs * 0.85f),
                color: _theme.TextMuted,
                fontSize: fs
            );

            // Feature 5: scrub indicator for float properties
            if (prop.Type == GraphTypeRef.Float)
            {
                paint.AddText(
                    text: "◀▶",
                    baselineX: sx + sw - Scale(NodePadX) - (fs * 2.5f),
                    baselineY: py + (fs * 0.85f),
                    color: _theme.Info.WithAlpha(0.6f),
                    fontSize: fs * 0.75f
                );
            }

            // Value — right-aligned, brighter; offset left for float to avoid overlapping arrows
            float valW = valueStr.Length * fs * 0.54f;
            float valOffX = prop.Type == GraphTypeRef.Float ? fs * 2.5f : 0f;
            paint.AddText(
                text: valueStr,
                baselineX: sx + sw - valW - Scale(NodePadX) - valOffX,
                baselineY: py + (fs * 0.85f),
                color: _theme.OnSurface,
                fontSize: fs
            );

            py += rowH;
        }
    }

    private static string FormatPropValue(PropertyDefinition prop, GraphValue val)
    {
        if (val.IsNull) return "—";
        if (prop.Type == GraphTypeRef.Float)
        {
            float f = val.AsFloat();
            return f == MathF.Floor(f) ? ((int)f).ToString() : f.ToString("F1");
        }

        if (prop.Type == GraphTypeRef.Bool) return val.AsBool() ? "on" : "off";
        if (prop.Type == GraphTypeRef.String)
        {
            string s = val.AsString();
            return s.Length > 12 ? s[..12] + "…" : s;
        }

        return val.ToString() ?? "—";
    }

    // ── Edges ─────────────────────────────────────────────────────────────────

    private void DrawEdges(PaintList paint)
    {
        foreach (var edge in _state.Graph.Edges)
        {
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
                    wy: baseY + (i * NodePinH) + (NodePinH * 0.5f)
                );
            }

            for (int i = 0; i < def.Outputs.Count; i++)
            {
                Beacon(
                    ep: new GraphPinEndpoint(NodeId: node.Id, PinId: def.Outputs[i].Id),
                    wx: layout.X + ww,
                    wy: baseY + ((def.Inputs.Count + i) * NodePinH) + (NodePinH * 0.5f)
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
        float handleLen = MathF.Max(x: (absDx * 0.5f) + (absDy * 0.25f), y: 80f);
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
        float fs = _theme.FontSizeCaption;
        if (_state.Graph.Nodes.Count == 0)
        {
            const string hint = "[Space] add node  ·  [Scroll] zoom  ·  [RMB] pan";
            float w = hint.Length * fs * 0.53f;
            paint.AddText(
                text: hint,
                baselineX: Bounds.X + ((Bounds.Width - w) * 0.5f),
                baselineY: Bounds.Y + (Bounds.Height * 0.5f) + 14f,
                color: _theme.Hint.WithAlpha(0.5f),
                fontSize: fs
            );
        }
        else if (_state.SelectedEdgeId.HasValue)
        {
            const string hint = "[Del] disconnect edge";
            paint.AddText(
                text: hint,
                baselineX: Bounds.X + 8f,
                baselineY: Bounds.Bottom - 6f,
                color: _theme.Hint,
                fontSize: fs
            );
        }

        string zoomText = $"{_zoom * 100f:F0}%";
        float zw = zoomText.Length * fs * 0.56f;
        paint.AddText(
            text: zoomText,
            baselineX: Bounds.Right - zw - 8f,
            baselineY: Bounds.Bottom - 6f,
            color: _theme.Hint,
            fontSize: fs
        );

        string edgeHint = "RMB edge: disconnect";
        paint.AddText(
            text: edgeHint,
            baselineX: Bounds.Right - (edgeHint.Length * fs * 0.53f) - 8f,
            baselineY: Bounds.Bottom - 20f,
            color: _theme.Hint.WithAlpha(0.35f),
            fontSize: fs - 1f
        );
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    public override void OnPointerDown(Offset point)
    {
        AppInstance.Active?.RequestFocus(this);

        float wx = ScreenToWorldX(point.X);
        float wy = ScreenToWorldY(point.Y);

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
            if (prop.Type == GraphTypeRef.Float)
            {
                var node = _state.Graph.FindNode(nid);
                float currentVal = node is not null
                                   && node.Properties.TryGetValue(
                                       key: prop.Id,
                                       value: out var sv
                                   ) && !sv.IsNull
                    ? sv.AsFloat()
                    : prop.DefaultValue.IsNull
                        ? 0f
                        : prop.DefaultValue.AsFloat();
                _editingProp = new PropEditTarget(
                    NodeId: nid,
                    PropId: prop.Id,
                    Def: prop,
                    ScreenRect: default
                );
                _editDragStartX = point.X;
                _editDragStartFloat = currentVal;
                return;
            }

            if (prop.Type == GraphTypeRef.Bool)
            {
                // Record intent; commit the toggle on pointer-up to avoid accidental toggles on drag
                _editingProp = new PropEditTarget(
                    NodeId: nid,
                    PropId: prop.Id,
                    Def: prop,
                    ScreenRect: default
                );
                return;
            }
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
            return;
        }

        // 2) Edge click → select edge
        var hitEdge = HitTestEdge(sx: point.X, sy: point.Y);
        if (hitEdge.HasValue)
        {
            _state.SelectEdge(hitEdge.Value);
            return;
        }

        // 3) Node click
        var hitNode = HitTestNode(wx: wx, wy: wy);
        if (hitNode.HasValue)
        {
            _state.Select(hitNode.Value);
            _dragNodeId = hitNode.Value;
            var layout = _state.Graph.EditorData.NodeLayouts[hitNode.Value];
            _dragNodeStartX = layout.X;
            _dragNodeStartY = layout.Y;
            _dragOffsetX = wx - layout.X;
            _dragOffsetY = wy - layout.Y;
            return;
        }

        // 4) Empty space → box select
        _state.ClearSelection();
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

        // Feature 5: float scrub feedback
        if (_editingProp.HasValue && _editingProp.Value.Def.Type == GraphTypeRef.Float)
        {
            float delta = (point.X - _editDragStartX) / _zoom * 0.5f;
            var propDef = _editingProp.Value.Def;
            float newVal = _editDragStartFloat + delta;
            if (propDef.Min.HasValue || propDef.Max.HasValue)
            {
                newVal = Math.Clamp(
                    value: newVal,
                    min: propDef.Min ?? float.MinValue,
                    max: propDef.Max ?? float.MaxValue
                );
            }

            var editNode = _state.Graph.FindNode(_editingProp.Value.NodeId);
            if (editNode is not null)
            {
                editNode.Properties[_editingProp.Value.PropId] = GraphValue.FromFloat(newVal);
                _state.TriggerCompile();
            }

            return;
        }

        if (_dragNodeId.HasValue)
        {
            var layout = _state.Graph.EditorData.NodeLayouts[_dragNodeId.Value];
            layout.X = wx - _dragOffsetX;
            layout.Y = wy - _dragOffsetY;
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

            _dragSnapPin = NearestCompatiblePin(wx: wx, wy: wy, worldRadius: NodePinRadius * 3f);
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

        // Feature 5: commit property edit
        if (_editingProp.HasValue)
        {
            var target = _editingProp.Value;
            _editingProp = null;

            if (target.Def.Type == GraphTypeRef.Float)
            {
                var editNode = _state.Graph.FindNode(target.NodeId);
                if (editNode is not null
                    && editNode.Properties.TryGetValue(key: target.PropId, value: out var curVal)
                    && !curVal.IsNull)
                {
                    float finalFloat = curVal.AsFloat();
                    editNode.Properties[target.PropId] = GraphValue.FromFloat(_editDragStartFloat);
                    _state.Commands.Execute(
                        new ChangeNodePropertyCommand(
                            nodeId: target.NodeId,
                            propertyKey: target.PropId,
                            oldValue: GraphValue.FromFloat(_editDragStartFloat),
                            newValue: GraphValue.FromFloat(finalFloat)
                        )
                    );
                }
            }
            else if (target.Def.Type == GraphTypeRef.Bool)
            {
                var editNode = _state.Graph.FindNode(target.NodeId);
                if (editNode is not null)
                {
                    var curVal = editNode.Properties.TryGetValue(
                                     key: target.PropId,
                                     value: out var sv
                                 ) &&
                                 !sv.IsNull
                        ? sv
                        : target.Def.DefaultValue;
                    bool oldBool = !curVal.IsNull && curVal.AsBool();
                    _state.Commands.Execute(
                        new ChangeNodePropertyCommand(
                            nodeId: target.NodeId,
                            propertyKey: target.PropId,
                            oldValue: GraphValue.FromBool(oldBool),
                            newValue: GraphValue.FromBool(!oldBool)
                        )
                    );
                }
            }

            return;
        }

        if (_dragNodeId.HasValue &&
            _state.Graph.EditorData.NodeLayouts.TryGetValue(
                key: _dragNodeId.Value,
                value: out var nodeLayout
            ))
        {
            float finalX = nodeLayout.X;
            float finalY = nodeLayout.Y;
            nodeLayout.X = _dragNodeStartX;
            nodeLayout.Y = _dragNodeStartY;
            _state.Commands.Execute(
                new MoveNodeCommand(nodeId: _dragNodeId.Value, newX: finalX, newY: finalY)
            );
        }

        _dragNodeId = null;

        if (_dragPin.HasValue)
        {
            // Prefer the snapped compatible pin; otherwise whatever pin is directly under the cursor.
            var target = _dragSnapPin;
            if (target is null)
            {
                var hitPin = HitTestPin(wx: wx, wy: wy);
                if (hitPin.HasValue && hitPin.Value.endpoint != _dragPin.Value)
                    target = hitPin.Value.endpoint;
            }

            // Only create when direction + type + domain all agree (matches the red/green drag feedback).
            if (target is { } t && IsCompatibleTarget(t))
            {
                var from = _dragPinDir == PinDirection.Output ? _dragPin.Value : t;
                var to = _dragPinDir == PinDirection.Input ? _dragPin.Value : t;
                _state.Commands.Execute(
                    new AddEdgeCommand(
                        new GraphEdge {
                            From = from,
                            To = to,
                        }
                    )
                );
            }

            _dragPin = null;
            _dragHoverPin = null;
            _dragSnapPin = null;
        }

        if (_isBoxSelecting)
        {
            _isBoxSelecting = false;
            UpdateBoxSelection();
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
        float oldZoom = _zoom;
        _zoom = Math.Clamp(value: _zoom + (dy * 0.08f * _zoom), min: 0.15f, max: 4f);
        float factor = _zoom / oldZoom;

        // Zoom toward cursor: the world point under _mouseX/_mouseY stays fixed.
        _panX = ((_mouseX - Bounds.X) * (1f - factor)) + (_panX * factor);
        _panY = ((_mouseY - Bounds.Y) * (1f - factor)) + (_panY * factor);
        // Relayout so the output preview / glass re-anchors to the zoomed node (explicit, not reliant on
        // the frame loop's incidental discrete-event relayout).
        InvalidateView();
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        const uint scDelete = 76;
        const uint scBackspace = 42;

        if (!down) return;

        if (scancode == scDelete || scancode == scBackspace)
        {
            // Delete selected edge
            if (_state.SelectedEdgeId.HasValue)
            {
                _state.Commands.Execute(new DeleteEdgeCommand(_state.SelectedEdgeId.Value));
                _state.SelectEdge(null);
                return;
            }

            // Delete selected nodes
            foreach (var id in _state.SelectedNodes.ToList())
                _state.Commands.Execute(new DeleteNodeCommand(id));
            _state.ClearSelection();
            return;
        }

        if (keyChar == ' ')
        {
            SearchRequested?.Invoke(
                arg1: ScreenToWorldX(Bounds.X + (Bounds.Width * 0.5f)),
                arg2: ScreenToWorldY(Bounds.Y + (Bounds.Height * 0.5f))
            );
            return;
        }

        if (mods.HasFlag(Modifiers.Ctrl))
        {
            if (char.ToLower(keyChar) == 'z') _state.Commands.Undo();
            if (char.ToLower(keyChar) == 'y') _state.Commands.Redo();
            if (char.ToLower(keyChar) == 'a')
            {
                // Select all nodes
                _state.ClearSelection();
                foreach (var n in _state.Graph.Nodes)
                    _state.AddToSelection(n.Id);
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

            if (_outputPreviewWidget is not null && node.Id == _outputNodeId)
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
        const float pickRadius = NodePinRadius * 2f;
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
                float pinY = baseY + (i * NodePinH) + (NodePinH * 0.5f);
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
                float pinY = baseY + ((def.Inputs.Count + i) * NodePinH) + (NodePinH * 0.5f);
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
    private Guid? HitTestEdge(float sx, float sy)
    {
        const float hitRadius = 6f;
        foreach (var edge in _state.Graph.Edges)
        {
            var fromPin = GetPinWorldPos(edge.From);
            var toPin = GetPinWorldPos(edge.To);
            if (!fromPin.HasValue || !toPin.HasValue) continue;

            float ex0 = WorldToScreenX(fromPin.Value.wx);
            float ey0 = WorldToScreenY(fromPin.Value.wy);
            float ex1 = WorldToScreenX(toPin.Value.wx);
            float ey1 = WorldToScreenY(toPin.Value.wy);

            float absDx = MathF.Abs(ex1 - ex0);
            float absDy = MathF.Abs(ey1 - ey0);
            float handleLen = MathF.Max(x: (absDx * 0.5f) + (absDy * 0.25f), y: 80f);
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
                if (MathF.Abs(sx - nx) < hitRadius && MathF.Abs(sy - ny) < hitRadius)
                    return edge.Id;
            }
        }

        return null;
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
                return (layout.X, baseY + (i * NodePinH) + (NodePinH * 0.5f));
        }

        for (int i = 0; i < def.Outputs.Count; i++)
        {
            if (def.Outputs[i].Id == ep.PinId)
            {
                return (layout.X + ww,
                    baseY + ((def.Inputs.Count + i) * NodePinH) + (NodePinH * 0.5f));
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
    private bool IsCompatibleTarget(GraphPinEndpoint cand)
    {
        if (!_dragPin.HasValue) return false;
        if (cand.NodeId == _dragPin.Value.NodeId) return false; // self-connection
        var src = ResolvePin(_dragPin.Value);
        var dst = ResolvePin(cand);
        if (src is null || dst is null) return false;
        if (dst.Value.dir == _dragPinDir) return false; // must connect output→input
        if (!TypesCompatible(a: src.Value.type, b: dst.Value.type)) return false;

        var from = _dragPinDir == PinDirection.Output ? _dragPin.Value : cand;
        var to = _dragPinDir == PinDirection.Input ? _dragPin.Value : cand;
        if (_state.Registry.TryGetDomain(domainId: _state.Graph.DomainId, domain: out var d) &&
            d is not null)
        {
            return d.CanCreateEdge(
                graph: _state.Graph,
                from: from,
                to: to,
                reason: out _
            );
        }

        return true;
    }

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
                    py: baseY + (i * NodePinH) + (NodePinH * 0.5f)
                );
            }

            for (int i = 0; i < def.Outputs.Count; i++)
            {
                Consider(
                    ep: new GraphPinEndpoint(NodeId: node.Id, PinId: def.Outputs[i].Id),
                    px: layout.X + ww,
                    py: baseY + ((def.Inputs.Count + i) * NodePinH) + (NodePinH * 0.5f)
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
        foreach (var node in _state.Graph.Nodes)
        {
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            if (def is null || def.Properties.Count == 0) continue;
            float ww = MathF.Max(
                x: layout.Width > 0 ? layout.Width : NodeMinWidth,
                y: NodeMinWidth
            );
            if (wx < layout.X || wx > layout.X + ww) continue;
            float propsStartY = layout.Y + TotalBodyH(def) - PropsBodyH(def);
            for (int i = 0; i < def.Properties.Count; i++)
            {
                float rowY = propsStartY + NodePropSepH + 2f + (i * NodePropH);
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

        _state.ClearSelection();
        foreach (var node in _state.Graph.Nodes)
        {
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            if (layout.X < wx1 && layout.X + NodeMinWidth > wx0 &&
                layout.Y < wy1 && layout.Y + NodeHeaderH > wy0)
                _state.AddToSelection(node.Id);
        }
    }

    // ── Color helpers ─────────────────────────────────────────────────────────

    private static Color CategoryColor(string category)
    {
        string lower = category.ToLowerInvariant();
        if (lower.Contains("math")) return new Color(r: 0.25f, g: 0.48f, b: 0.70f);
        if (lower.Contains("texture")) return new Color(r: 0.55f, g: 0.30f, b: 0.65f);
        if (lower.Contains("output")) return new Color(r: 0.20f, g: 0.55f, b: 0.35f);
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
        if (hitEdge.HasValue)
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
        var menu = new ContextMenu(
            new ContextMenuItem(
                Label: "Disconnect edge",
                OnSelect: () =>
                {
                    _state.Commands.Execute(new DeleteEdgeCommand(edgeId));
                    _state.SelectEdge(null);
                    _hoveredEdgeId = null;
                }
            )
        );
        menu.ShowAt(screenPos);
    }

    private void ShowNodeContextMenu(Guid nodeId, Offset screenPos)
    {
        _state.Select(nodeId);
        var node = _state.Graph.FindNode(nodeId);
        var def = node is null ? null : _state.Registry.GetNodeDefinition(node.DefinitionId);

        var menu = new ContextMenu(
            new ContextMenuItem(Label: def?.DisplayName ?? "Node", OnSelect: null),
            new ContextMenuItem(Label: "", OnSelect: null, Separator: true),
            new ContextMenuItem(
                Label: "Delete",
                OnSelect: () =>
                {
                    _state.Commands.Execute(new DeleteNodeCommand(nodeId));
                    _state.ClearSelection();
                }
            ),
            new ContextMenuItem(
                Label: "Duplicate",
                OnSelect: () =>
                {
                    if (node is null) return;
                    var copy = new GraphNode { DefinitionId = node.DefinitionId };
                    foreach ((string k, var v) in node.Properties) copy.Properties[k] = v;
                    if (_state.Graph.EditorData.NodeLayouts.TryGetValue(
                            key: nodeId,
                            value: out var ol
                        ))
                    {
                        _state.Commands.Execute(
                            new AddNodeCommand(node: copy, x: ol.X + 30f, y: ol.Y + 30f)
                        );
                    }
                    else
                    {
                        _state.Commands.Execute(
                            new AddNodeCommand(
                                node: copy,
                                x: ScreenToWorldX(screenPos.X),
                                y: ScreenToWorldY(screenPos.Y)
                            )
                        );
                    }

                    _state.Select(copy.Id);
                }
            )
        );
        menu.ShowAt(screenPos);
    }

    // Feature 7: hierarchical "add node" menu — categories as submenu parents, nodes as leaves.
    private void ShowAddNodeContextMenu(float wx, float wy, Offset screenPos)
    {
        var items = new List<ContextMenuItem>();

        var defs = _state.Registry
            .NodeDefinitionsForDomain(_state.Graph.DomainId)
            .GroupBy(d => d.Category ?? "Other")
            .OrderBy(g => g.Key);

        foreach (var group in defs)
        {
            var categoryChildren = group.Select(def =>
                {
                    string defId = def.Id;
                    float spawnX = wx;
                    float spawnY = wy;
                    return new ContextMenuItem(
                        Label: def.DisplayName,
                        OnSelect: () =>
                        {
                            var node = new GraphNode { DefinitionId = defId };
                            _state.Commands.Execute(
                                new AddNodeCommand(node: node, x: spawnX, y: spawnY)
                            );
                            _state.Select(node.Id);
                        }
                    );
                }
            ).ToArray();

            items.Add(
                new ContextMenuItem(Label: group.Key, OnSelect: null, Children: categoryChildren)
            );
        }

        if (items.Count == 0)
            items.Add(new ContextMenuItem(Label: "No nodes available", OnSelect: null));

        new ContextMenu(items.ToArray()).ShowAt(screenPos);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void AddNode(string definitionId, float worldX, float worldY)
    {
        var node = new GraphNode { DefinitionId = definitionId };
        _state.Commands.Execute(new AddNodeCommand(node: node, x: worldX, y: worldY));
        _state.Select(node.Id);
    }

    public void FrameAll()
    {
        if (_state.Graph.Nodes.Count == 0)
        {
            _panX = _panY = 0;
            _zoom = 1f;
            return;
        }

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        foreach (var node in _state.Graph.Nodes)
        {
            if (!_state.Graph.EditorData.NodeLayouts.TryGetValue(
                    key: node.Id,
                    value: out var layout
                )) continue;
            var def = _state.Registry.GetNodeDefinition(node.DefinitionId);
            float nodeH = EffectiveHeight(layout: layout, def: def);
            if (node.Id == _outputNodeId && _outputPreviewMeasuredSize.Height > 0)
                nodeH += (PreviewPad * 2f) + (_outputPreviewMeasuredSize.Height / _zoom);
            minX = MathF.Min(x: minX, y: layout.X);
            minY = MathF.Min(x: minY, y: layout.Y);
            maxX = MathF.Max(x: maxX, y: layout.X + NodeMinWidth);
            maxY = MathF.Max(x: maxY, y: layout.Y + nodeH);
        }

        float pad = 60f;
        float gw = maxX - minX + (pad * 2);
        float gh = maxY - minY + (pad * 2);
        _zoom = Math.Clamp(
            value: MathF.Min(x: _size.Width / gw, y: _size.Height / gh),
            min: 0.2f,
            max: 2f
        );
        _panX = (_size.Width * 0.5f) - ((minX + (gw * 0.5f) - pad) * _zoom);
        _panY = (_size.Height * 0.5f) - ((minY + (gh * 0.5f) - pad) * _zoom);
    }

    // Inline property editing
    private record struct PropEditTarget(
        Guid NodeId,
        string PropId,
        PropertyDefinition Def,
        Rect ScreenRect);
}
