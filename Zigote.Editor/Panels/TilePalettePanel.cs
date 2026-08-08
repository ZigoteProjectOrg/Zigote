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
    private readonly ViewportPanel _viewport;

    private readonly (TileTool Tool, string Label)[] _tools = [
        (TileTool.Paint, "Paint"),
        (TileTool.Erase, "Erase"),
        (TileTool.Rect, "Rect"),
        (TileTool.Fill, "Fill"),
        (TileTool.Pick, "Pick"),
    ];

    // Cached engine texture for the tileset sheet, keyed by the path we loaded it from.
    private string? _loadedTexturePath;
    private ulong _sheetHandle;
    private uint _sheetW, _sheetH;

    private Rect _sheetRect;
    private Size _size;
    private Rect[] _toolRects = [];
    private Rect[] _layerRects = [];
    private Rect _addLayerRect;

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
        var w = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 260f;
        var h = RowH + Gap + ToolH + Gap; // header + tool row
        h += RowH + Gap; // toggles

        if (ActiveTileset is { } set && set.TileCount > 0)
            h += SheetHeight(w, set) + Gap;

        if (Target is { } node)
            h += RowH + node.TilemapLayers.Count * RowH + Gap; // layer header + rows + add

        _size = new Size(w, MathF.Max(120f, h));
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
    }

    /// <summary>Height the sheet needs when scaled to fit the panel width, preserving aspect.</summary>
    private float SheetHeight(float width, Tileset set)
    {
        var texW = MathF.Max(1, set.EffectiveTextureWidth);
        var texH = MathF.Max(1, set.EffectiveTextureHeight);
        // Never shrink a tile below a clickable size — a big sheet scrolls rather than becoming dust.
        var scale = MathF.Max((width - 8f) / texW, SwatchMin * set.Columns / texW);
        return texH * scale;
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        var y = Bounds.Y;
        var node = Target;

        if (node is null)
        {
            paint.AddText(
                "Select a Tilemap node",
                Bounds.X + 6f,
                y + 16f,
                _theme.TextSecondary,
                _theme.FontSizeBody
            );
            return;
        }

        paint.AddText(
            node.Name,
            Bounds.X + 6f,
            y + 16f,
            _theme.OnSurface,
            _theme.FontSizeBody,
            fontWeight: FontWeight.SemiBold
        );
        y += RowH + Gap;

        y = PaintTools(paint, y);
        y = PaintToggles(paint, y);
        y = PaintSheet(paint, y, node);
        PaintLayers(paint, y, node);
    }

    private float PaintTools(PaintList paint, float y)
    {
        var w = (Bounds.Width - 8f - (_tools.Length - 1) * 3f) / _tools.Length;
        _toolRects = new Rect[_tools.Length];

        for (var i = 0; i < _tools.Length; i++)
        {
            var r = new Rect(
                Bounds.X + 4f + i * (w + 3f),
                y,
                w,
                ToolH
            );
            _toolRects[i] = r;
            var active = _viewport.ActiveTool == _tools[i].Tool;
            paint.AddRect(r, active ? _theme.Accent : _theme.Surface, Radii.Sm);
            if (!active) paint.AddBorder(r, _theme.Border, Radii.Sm);

            var label = _tools[i].Label;
            var tw = label.Length * _theme.FontSizeCaption * 0.54f;
            paint.AddText(
                label,
                r.X + (r.Width - tw) * 0.5f,
                r.Y + r.Height * 0.5f + _theme.FontSizeCaption * 0.36f,
                active ? _theme.OnPrimary : _theme.TextSecondary,
                _theme.FontSizeCaption
            );
        }

        return y + ToolH + Gap;
    }

    private float PaintToggles(PaintList paint, float y)
    {
        var text = $"Grid {OnOff(_viewport.ShowGrid)}   " +
                   $"Snap {OnOff(_viewport.SnapToGrid)}   " +
                   $"Colliders {OnOff(_viewport.ShowColliders2D)}";
        paint.AddText(
            text,
            Bounds.X + 6f,
            y + 15f,
            _theme.TextSecondary,
            _theme.FontSizeCaption
        );
        return y + RowH + Gap;

        static string OnOff(bool b)
        {
            return b ? "on" : "off";
        }
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
                node.TilesetPath is null ? "No tileset assigned" : "Tileset failed to load",
                Bounds.X + 6f,
                y + 15f,
                _theme.TextSecondary,
                _theme.FontSizeCaption
            );
            return y + RowH + Gap;
        }

        EnsureSheetTexture(set);

        var h = SheetHeight(Bounds.Width, set);
        _sheetRect = new Rect(
            Bounds.X + 4f,
            y,
            Bounds.Width - 8f,
            h
        );

        paint.AddRect(
            _sheetRect,
            new Color(
                0f,
                0f,
                0f,
                0.25f
            )
        );
        if (_sheetHandle != 0)
            paint.AddImage(
                _sheetRect,
                (int)_sheetW,
                (int)_sheetH,
                null,
                _sheetHandle
            );

        var cellW = _sheetRect.Width / set.Columns;
        var cellH = _sheetRect.Height / set.Rows;
        var line = new Color(
            1f,
            1f,
            1f,
            0.12f
        );
        for (var c = 1; c < set.Columns; c++)
            paint.AddRect(
                new Rect(
                    _sheetRect.X + c * cellW,
                    _sheetRect.Y,
                    1f,
                    _sheetRect.Height
                ),
                line
            );
        for (var r = 1; r < set.Rows; r++)
            paint.AddRect(
                new Rect(
                    _sheetRect.X,
                    _sheetRect.Y + r * cellH,
                    _sheetRect.Width,
                    1f
                ),
                line
            );

        // Selection highlight.
        var tile = _viewport.ActiveTile;
        if (tile >= 0 && tile < set.TileCount)
        {
            var sel = new Rect(
                _sheetRect.X + tile % set.Columns * cellW,
                _sheetRect.Y + tile / set.Columns * cellH,
                cellW,
                cellH
            );
            paint.AddBorder(sel, _theme.Accent, 0f);
            paint.AddBorder(
                new Rect(
                    sel.X + 1f,
                    sel.Y + 1f,
                    sel.Width - 2f,
                    sel.Height - 2f
                ),
                _theme.Accent,
                0f
            );
        }

        return y + h + Gap;
    }

    private void PaintLayers(PaintList paint, float y, SceneNode node)
    {
        paint.AddText(
            "Layers",
            Bounds.X + 6f,
            y + 15f,
            _theme.TextSecondary,
            _theme.FontSizeCaption,
            fontWeight: FontWeight.SemiBold
        );
        _addLayerRect = new Rect(
            Bounds.X + Bounds.Width - 30f,
            y + 3f,
            26f,
            RowH - 6f
        );
        paint.AddRect(_addLayerRect, _theme.Surface, Radii.Sm);
        paint.AddBorder(_addLayerRect, _theme.Border, Radii.Sm);
        paint.AddText(
            "+",
            _addLayerRect.X + 9f,
            _addLayerRect.Y + 14f,
            _theme.OnSurface,
            _theme.FontSizeCaption
        );
        y += RowH;

        _layerRects = new Rect[node.TilemapLayers.Count];
        for (var i = 0; i < node.TilemapLayers.Count; i++)
        {
            var layer = node.TilemapLayers[i];
            var r = new Rect(
                Bounds.X + 4f,
                y,
                Bounds.Width - 8f,
                RowH
            );
            _layerRects[i] = r;

            if (i == _viewport.ActiveLayerIndex)
                paint.AddRect(r, _theme.Accent.WithAlpha(0.22f), Radii.Sm);

            paint.AddText(
                layer.Visible ? "◉" : "○",
                r.X + 6f,
                r.Y + 17f,
                _theme.TextSecondary,
                _theme.FontSizeCaption
            );
            paint.AddText(
                layer.Name,
                r.X + 24f,
                r.Y + 17f,
                _theme.OnSurface,
                _theme.FontSizeCaption
            );
            y += RowH;
        }
    }

    /// <summary>Upload the sheet through the engine's UI texture cache, reloading when the path changes.</summary>
    private void EnsureSheetTexture(Tileset set)
    {
        var path = set.TexturePath;
        if (string.IsNullOrEmpty(path)) return;
        var abs = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        if (_loadedTexturePath == abs && _sheetHandle != 0) return;

        try
        {
            if (ZigoteEngine.Instance is null || !File.Exists(abs)) return;
            // The outgoing sheet is ours to free: texture handles are caller-owned, so switching
            // tilesets used to strand the previous sheet on the GPU for the editor's lifetime.
            if (_sheetHandle != 0) ZigoteEngine.ReleaseTexture(_sheetHandle);
            _sheetHandle = ZigoteEngine.LoadTexture(abs, out _sheetW, out _sheetH);
            _loadedTexturePath = abs;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            _sheetHandle = 0;
            _loadedTexturePath = null;
        }
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);

        for (var i = 0; i < _toolRects.Length; i++)
            if (_toolRects[i].Contains(point.X, point.Y))
            {
                _viewport.ActiveTool = _tools[i].Tool;
                MarkNeedsPaint();
                return;
            }

        if (Target is not { } node) return;

        if (_addLayerRect.Contains(point.X, point.Y))
        {
            _state.History.Execute(
                TilemapLayerCommand.Add(
                    _state,
                    node,
                    new TilemapLayer { Name = $"Layer {node.TilemapLayers.Count + 1}" }
                )
            );
            _viewport.ActiveLayerIndex = node.TilemapLayers.Count - 1;
            MarkNeedsPaint();
            return;
        }

        for (var i = 0; i < _layerRects.Length; i++)
            if (_layerRects[i].Contains(point.X, point.Y))
            {
                // The eye column toggles visibility; anywhere else selects the layer to paint into.
                if (point.X < _layerRects[i].X + 22f)
                {
                    node.TilemapLayers[i].Visible = !node.TilemapLayers[i].Visible;
                    _state.NotifySceneChanged();
                }
                else
                {
                    _viewport.ActiveLayerIndex = i;
                }

                MarkNeedsPaint();
                return;
            }

        if (_sheetRect.Contains(point.X, point.Y) && ActiveTileset is { } set && set.TileCount > 0)
        {
            var col = (int)((point.X - _sheetRect.X) / (_sheetRect.Width / set.Columns));
            var row = (int)((point.Y - _sheetRect.Y) / (_sheetRect.Height / set.Rows));
            col = Math.Clamp(col, 0, set.Columns - 1);
            row = Math.Clamp(row, 0, set.Rows - 1);
            _viewport.ActiveTile = row * set.Columns + col;
            // Picking a tile means you want to place it.
            if (_viewport.ActiveTool == TileTool.Pick) _viewport.ActiveTool = TileTool.Paint;
            MarkNeedsPaint();
        }
    }
}