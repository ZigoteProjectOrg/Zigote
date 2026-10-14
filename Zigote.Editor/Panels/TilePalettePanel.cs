using Zigote.Core;
using Zigote.Core.Engine;
using Zigote.Core.Paint;
using Zigote.Editor.History;
using Zigote.Editor.Scene;
using Zigote.Render2D;
using Zigote.Runtime.Scene;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.TextShaping;

namespace Zigote.Editor.Panels;

/// <summary>
///     Tile authoring surface: pick the tool, pick the tile, manage the tilemap's layers.
///     <para>
///         Self-painting (like <see cref="InfoPanel" />) because the body is a texture blit with a
///         hit-tested grid over it — a retained widget per tile would mean hundreds of widgets for a
///         medium sheet, rebuilt whenever the tileset changes.
///     </para>
///     <para>
///         Drives <see cref="ViewportPanel" />'s tile-tool properties; the viewport owns the actual
///         painting so undo, snapping and the tile cursor live with the rest of the viewport input.
///     </para>
/// </summary>
public sealed class TilePalettePanel : Widget
{
    private const float RowH = 26f;
    private const float Gap = 6f;
    private const float ToolH = 28f;
    private const float SwatchMin = 16f;

    private readonly EditorState _state;
    private readonly ThemeData _theme;

    private readonly (TileTool Tool, string Label)[] _tools = [
        (TileTool.Paint, "Paint"),
        (TileTool.Erase, "Erase"),
        (TileTool.Rect, "Rect"),
        (TileTool.Fill, "Fill"),
        (TileTool.Pick, "Pick"),
    ];

    private readonly ViewportPanel _viewport;
    private Rect _addLayerRect;
    private Rect _createRect;
    private int _hoverTool = -1;
    private readonly Rect[] _toggleRects = new Rect[3];
    private Rect[] _layerRects = [];

    // Cached engine texture for the tileset sheet, keyed by the path we loaded it from.
    private string? _loadedTexturePath;
    private ulong _sheetHandle;

    private Rect _sheetRect;
    private uint _sheetW, _sheetH;
    private Size _size;
    private Rect[] _toolRects = [];

    public TilePalettePanel(EditorState state, ThemeData theme, ViewportPanel viewport)
    {
        _state = state;
        _theme = theme;
        _viewport = viewport;
        // The Pick tool adopts a tile from the canvas — reflect it in the palette highlight.
        _viewport.OnTilePicked = _ => MarkNeedsPaint();
    }

    /// <summary>The tilemap being edited: the selection when it is one, else the last one seen.</summary>
    private SceneNode? Target
    {
        get
        {
            if (_state.Selected is { Kind: NodeKind.Tilemap } sel) _viewport.ActiveTilemap = sel;
            return _viewport.ActiveTilemap;
        }
    }

    private Tileset? ActiveTileset =>
        Target is { } n ? _state.Sprites2D.GetTileset(n.TilesetPath)?.Set : null;

    // ── Layout ────────────────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        float w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 260f;
        float h = RowH + Gap + ToolH + Gap; // header + tool row
        h += RowH + Gap; // toggles

        if (ActiveTileset is { } set && set.TileCount > 0)
            h += SheetHeight(width: w, set: set) + Gap;

        if (Target is { } node)
            h += RowH + (node.TilemapLayers.Count * RowH) + Gap; // layer header + rows + add

        _size = new Size(width: w, height: MathF.Max(x: 120f, y: h));
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

    /// <summary>Height the sheet needs when scaled to fit the panel width, preserving aspect.</summary>
    private float SheetHeight(float width, Tileset set)
    {
        float texW = MathF.Max(x: 1, y: set.EffectiveTextureWidth);
        float texH = MathF.Max(x: 1, y: set.EffectiveTextureHeight);
        // Never shrink a tile below a clickable size — a big sheet scrolls rather than becoming dust.
        float scale = MathF.Max(x: (width - 8f) / texW, y: SwatchMin * set.Columns / texW);
        return texH * scale;
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        float y = Bounds.Y;
        var node = Target;

        if (node is null)
        {
            paint.AddText(
                text: "Select a Tilemap node to paint tiles,",
                baselineX: Bounds.X + 6f,
                baselineY: y + 16f,
                color: _theme.TextSecondary,
                fontSize: _theme.FontSizeCaption
            );
            paint.AddText(
                text: "or start a new one:",
                baselineX: Bounds.X + 6f,
                baselineY: y + 32f,
                color: _theme.TextSecondary,
                fontSize: _theme.FontSizeCaption
            );
            const string create = "Create Tilemap";
            float cw = TextMeasure.Width(text: create, fontSize: _theme.FontSizeCaption) + 24f;
            _createRect = new Rect(
                x: Bounds.X + 6f,
                y: y + 44f,
                width: cw,
                height: ToolH
            );
            paint.AddRect(bounds: _createRect, color: _theme.Accent, radius: Radii.Sm);
            paint.AddText(
                text: create,
                baselineX: _createRect.X + 12f,
                baselineY: _createRect.Y + (ToolH * 0.5f) + (_theme.FontSizeCaption * 0.36f),
                color: _theme.OnPrimary,
                fontSize: _theme.FontSizeCaption
            );
            return;
        }

        _createRect = default;
        paint.AddText(
            text: node.Name,
            baselineX: Bounds.X + 6f,
            baselineY: y + 16f,
            color: _theme.OnSurface,
            fontSize: _theme.FontSizeBody,
            fontWeight: FontWeight.SemiBold
        );
        y += RowH + Gap;

        y = PaintTools(paint: paint, y: y);
        y = PaintToggles(paint: paint, y: y);
        y = PaintSheet(paint: paint, y: y, node: node);
        PaintLayers(paint: paint, y: y, node: node);
    }

    private float PaintTools(PaintList paint, float y)
    {
        float w = (Bounds.Width - 8f - ((_tools.Length - 1) * 3f)) / _tools.Length;
        _toolRects = new Rect[_tools.Length];

        for (int i = 0; i < _tools.Length; i++)
        {
            var r = new Rect(
                x: Bounds.X + 4f + (i * (w + 3f)),
                y: y,
                width: w,
                height: ToolH
            );
            _toolRects[i] = r;
            bool active = _viewport.ActiveTool == _tools[i].Tool;
            paint.AddRect(
                bounds: r,
                color: active ? _theme.Accent :
                i == _hoverTool ? _theme.ControlHover : _theme.Surface,
                radius: Radii.Sm
            );
            if (!active) paint.AddBorder(bounds: r, color: _theme.Border, radius: Radii.Sm);

            string label = _tools[i].Label;
            float tw = TextMeasure.Width(text: label, fontSize: _theme.FontSizeCaption);
            paint.AddText(
                text: label,
                baselineX: r.X + ((r.Width - tw) * 0.5f),
                baselineY: r.Y + (r.Height * 0.5f) + (_theme.FontSizeCaption * 0.36f),
                color: active ? _theme.OnPrimary : _theme.TextSecondary,
                fontSize: _theme.FontSizeCaption
            );
        }

        return y + ToolH + Gap;
    }

    private float PaintToggles(PaintList paint, float y)
    {
        // Clickable chips, filled while on — these used to be a read-only "Grid on  Snap off" line.
        (string Label, bool On)[] toggles = [
            ("Grid", _viewport.ShowGrid), ("Snap", _viewport.SnapToGrid),
            ("Colliders", _viewport.ShowColliders2D),
        ];
        float fs = _theme.FontSizeCaption;
        float x = Bounds.X + 4f;
        for (int i = 0; i < toggles.Length; i++)
        {
            float w = TextMeasure.Width(text: toggles[i].Label, fontSize: fs) + 16f;
            var r = new Rect(
                x: x,
                y: y + 2f,
                width: w,
                height: RowH - 4f
            );
            _toggleRects[i] = r;
            if (toggles[i].On)
                paint.AddRect(bounds: r, color: _theme.Accent.WithAlpha(0.22f), radius: Radii.Sm);
            else paint.AddBorder(bounds: r, color: _theme.Border, radius: Radii.Sm);
            paint.AddText(
                text: toggles[i].Label,
                baselineX: r.X + 8f,
                baselineY: r.Y + (r.Height * 0.5f) + (fs * 0.36f),
                color: toggles[i].On ? _theme.OnSurface : _theme.TextSecondary,
                fontSize: fs
            );
            x += w + 4f;
        }

        return y + RowH + Gap;
    }

    /// <summary>
    ///     Blit the tileset sheet and overlay its grid. Tiles are picked by index from the click
    ///     position, so no per-tile widget or sub-image blit is needed.
    /// </summary>
    private float PaintSheet(PaintList paint, float y, SceneNode node)
    {
        _sheetRect = default;
        if (ActiveTileset is not { } set || set.TileCount == 0)
        {
            paint.AddText(
                text: node.TilesetPath is null
                    ? "No tileset — assign one in the Inspector"
                    : "Tileset failed to load — check its path in the Inspector",
                baselineX: Bounds.X + 6f,
                baselineY: y + 15f,
                color: _theme.TextSecondary,
                fontSize: _theme.FontSizeCaption
            );
            return y + RowH + Gap;
        }

        EnsureSheetTexture(set);

        float h = SheetHeight(width: Bounds.Width, set: set);
        _sheetRect = new Rect(
            x: Bounds.X + 4f,
            y: y,
            width: Bounds.Width - 8f,
            height: h
        );

        paint.AddRect(
            bounds: _sheetRect,
            color: new Color(
                r: 0f,
                g: 0f,
                b: 0f,
                a: 0.25f
            )
        );
        if (_sheetHandle != 0)
        {
            paint.AddImage(
                bounds: _sheetRect,
                pixelWidth: (int)_sheetW,
                pixelHeight: (int)_sheetH,
                pixels: null,
                cacheKey: _sheetHandle
            );
        }

        float cellW = _sheetRect.Width / set.Columns;
        float cellH = _sheetRect.Height / set.Rows;
        var line = new Color(
            r: 1f,
            g: 1f,
            b: 1f,
            a: 0.12f
        );
        for (int c = 1; c < set.Columns; c++)
        {
            paint.AddRect(
                bounds: new Rect(
                    x: _sheetRect.X + (c * cellW),
                    y: _sheetRect.Y,
                    width: 1f,
                    height: _sheetRect.Height
                ),
                color: line
            );
        }

        for (int r = 1; r < set.Rows; r++)
        {
            paint.AddRect(
                bounds: new Rect(
                    x: _sheetRect.X,
                    y: _sheetRect.Y + (r * cellH),
                    width: _sheetRect.Width,
                    height: 1f
                ),
                color: line
            );
        }

        // Selection highlight.
        int tile = _viewport.ActiveTile;
        if (tile >= 0 && tile < set.TileCount)
        {
            var sel = new Rect(
                x: _sheetRect.X + (tile % set.Columns * cellW),
                y: _sheetRect.Y + (tile / set.Columns * cellH),
                width: cellW,
                height: cellH
            );
            paint.AddBorder(bounds: sel, color: _theme.Accent, radius: 0f);
            paint.AddBorder(
                bounds: new Rect(
                    x: sel.X + 1f,
                    y: sel.Y + 1f,
                    width: sel.Width - 2f,
                    height: sel.Height - 2f
                ),
                color: _theme.Accent,
                radius: 0f
            );
        }

        return y + h + Gap;
    }

    private void PaintLayers(PaintList paint, float y, SceneNode node)
    {
        paint.AddText(
            text: "Layers",
            baselineX: Bounds.X + 6f,
            baselineY: y + 15f,
            color: _theme.TextSecondary,
            fontSize: _theme.FontSizeCaption,
            fontWeight: FontWeight.SemiBold
        );
        _addLayerRect = new Rect(
            x: Bounds.X + Bounds.Width - 30f,
            y: y + 3f,
            width: 26f,
            height: RowH - 6f
        );
        paint.AddRect(bounds: _addLayerRect, color: _theme.Surface, radius: Radii.Sm);
        paint.AddBorder(bounds: _addLayerRect, color: _theme.Border, radius: Radii.Sm);
        paint.AddText(
            text: "+",
            baselineX: _addLayerRect.X + 9f,
            baselineY: _addLayerRect.Y + 14f,
            color: _theme.OnSurface,
            fontSize: _theme.FontSizeCaption
        );
        y += RowH;

        _layerRects = new Rect[node.TilemapLayers.Count];
        for (int i = 0; i < node.TilemapLayers.Count; i++)
        {
            var layer = node.TilemapLayers[i];
            var r = new Rect(
                x: Bounds.X + 4f,
                y: y,
                width: Bounds.Width - 8f,
                height: RowH
            );
            _layerRects[i] = r;

            if (i == _viewport.ActiveLayerIndex)
                paint.AddRect(bounds: r, color: _theme.Accent.WithAlpha(0.22f), radius: Radii.Sm);

            Icons.Draw(
                paint: paint,
                glyph: layer.Visible ? Icons.Visibility : Icons.VisibilityOff,
                box: new Rect(
                    x: r.X + 2f,
                    y: r.Y,
                    width: 20f,
                    height: RowH
                ),
                color: _theme.TextSecondary,
                size: 14f
            );
            paint.AddText(
                text: layer.Name,
                baselineX: r.X + 24f,
                baselineY: r.Y + 17f,
                color: _theme.OnSurface,
                fontSize: _theme.FontSizeCaption
            );
            y += RowH;
        }
    }

    /// <summary>Upload the sheet through the engine's UI texture cache, reloading when the path changes.</summary>
    private void EnsureSheetTexture(Tileset set)
    {
        string path = set.TexturePath;
        if (string.IsNullOrEmpty(path)) return;
        string abs = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        if (_loadedTexturePath == abs && _sheetHandle != 0) return;

        try
        {
            if (ZigoteEngine.Instance is null || !File.Exists(abs)) return;
            // The outgoing sheet is ours to free: texture handles are caller-owned, so switching
            // tilesets used to strand the previous sheet on the GPU for the editor's lifetime.
            if (_sheetHandle != 0) ZigoteEngine.ReleaseTexture(_sheetHandle);
            _sheetHandle = ZigoteEngine.LoadTexture(
                path: abs,
                outW: out _sheetW,
                outH: out _sheetH
            );
            _loadedTexturePath = abs;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            _sheetHandle = 0;
            _loadedTexturePath = null;
        }
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    public override void OnPointerMove(Offset point)
    {
        int hover = Array.FindIndex(
            array: _toolRects,
            match: r => r.Contains(px: point.X, py: point.Y)
        );
        if (hover == _hoverTool) return;
        _hoverTool = hover;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        _hoverTool = -1;
        MarkNeedsPaint();
    }

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);

        if (_createRect.Contains(px: point.X, py: point.Y))
        {
            // Seed one layer so there is somewhere to paint immediately (as Hierarchy ▸ Add Tilemap).
            var map = new SceneNode(name: "Tilemap", kind: NodeKind.Tilemap);
            map.TilemapLayers.Add(new TilemapLayer { Name = "Ground" });
            _state.History.Execute(
                new AddNodeCommand(state: _state, parent: _state.Scene.Root, node: map)
            );
            _state.Select(map);
            MarkNeedsLayout();
            return;
        }

        if (Target is not null)
        {
            for (int i = 0; i < _toggleRects.Length; i++)
            {
                if (!_toggleRects[i].Contains(px: point.X, py: point.Y)) continue;
                if (i == 0) _viewport.ShowGrid = !_viewport.ShowGrid;
                else if (i == 1) _viewport.SnapToGrid = !_viewport.SnapToGrid;
                else _viewport.ShowColliders2D = !_viewport.ShowColliders2D;
                _viewport.MarkNeedsPaint();
                MarkNeedsPaint();
                return;
            }
        }

        for (int i = 0; i < _toolRects.Length; i++)
        {
            if (_toolRects[i].Contains(px: point.X, py: point.Y))
            {
                _viewport.ActiveTool = _tools[i].Tool;
                MarkNeedsPaint();
                return;
            }
        }

        if (Target is not { } node) return;

        if (_addLayerRect.Contains(px: point.X, py: point.Y))
        {
            _state.History.Execute(
                TilemapLayerCommand.Add(
                    state: _state,
                    node: node,
                    layer: new TilemapLayer { Name = $"Layer {node.TilemapLayers.Count + 1}" }
                )
            );
            _viewport.ActiveLayerIndex = node.TilemapLayers.Count - 1;
            MarkNeedsPaint();
            return;
        }

        for (int i = 0; i < _layerRects.Length; i++)
        {
            if (_layerRects[i].Contains(px: point.X, py: point.Y))
            {
                // The eye column toggles visibility; anywhere else selects the layer to paint into.
                if (point.X < _layerRects[i].X + 22f)
                {
                    node.TilemapLayers[i].Visible = !node.TilemapLayers[i].Visible;
                    _state.NotifySceneChanged();
                }
                else
                    _viewport.ActiveLayerIndex = i;

                MarkNeedsPaint();
                return;
            }
        }

        if (_sheetRect.Contains(px: point.X, py: point.Y) && ActiveTileset is { } set &&
            set.TileCount > 0)
        {
            int col = (int)((point.X - _sheetRect.X) / (_sheetRect.Width / set.Columns));
            int row = (int)((point.Y - _sheetRect.Y) / (_sheetRect.Height / set.Rows));
            col = Math.Clamp(value: col, min: 0, max: set.Columns - 1);
            row = Math.Clamp(value: row, min: 0, max: set.Rows - 1);
            _viewport.ActiveTile = (row * set.Columns) + col;
            // Picking a tile means you want to place it.
            if (_viewport.ActiveTool == TileTool.Pick) _viewport.ActiveTool = TileTool.Paint;
            MarkNeedsPaint();
        }
    }
}
