using Zigote.Editor.Scene;
using Zigote.Render2D;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.History;

/// <summary>
///     One tile-painting stroke on a single layer, recorded as (x, y, before, after) per changed cell.
///     <para>
///         Cell-diff rather than a layer snapshot: a stroke touches a handful of cells out of a map
///         that can be tens of thousands, and dragging across a large map would otherwise push a full
///         copy of the grid onto the undo stack per pointer-move.
///     </para>
///     <para>
///         Consecutive edits from the same drag merge (<see cref="TryMergeWith" />) so one stroke is
///         one undo step. The editor closes the stroke on pointer-up by pushing a fresh command.
///     </para>
/// </summary>
public sealed class PaintTilesCommand(EditorState state, TilemapLayer layer) : ICommand
{
    private readonly List<(int X, int Y, int Before, int After)> _edits = [];

    /// <summary>Set false on pointer-up so the next stroke starts its own undo entry.</summary>
    public bool Open { get; set; } = true;

    /// <summary>The layer this stroke writes — strokes only merge within one layer.</summary>
    public TilemapLayer Layer => layer;

    /// <summary>Did this stroke change anything? Empty strokes are not worth an undo entry.</summary>
    public bool HasEdits => _edits.Count > 0;

    public void Execute()
    {
        // Replaying after an undo: apply in recorded order so overlapping cells end on the last value.
        foreach ((int x, int y, int _, int after) in _edits) layer.SetTile(x: x, y: y, tile: after);
        Changed();
    }

    public void Undo()
    {
        // Reverse order so a cell painted twice in one stroke unwinds to its original value.
        for (int i = _edits.Count - 1; i >= 0; i--)
        {
            (int x, int y, int before, _) = _edits[i];
            layer.SetTile(x: x, y: y, tile: before);
        }

        layer.Trim(); // an undone stroke may have been what stretched the rect
        Changed();
    }

    public bool TryMergeWith(ICommand other)
    {
        if (!Open || other is not PaintTilesCommand p ||
            !ReferenceEquals(objA: p.Layer, objB: layer))
            return false;
        _edits.AddRange(p._edits);
        return true;
    }

    /// <summary>
    ///     Paint one cell, recording its previous value. Returns false when the cell already held
    ///     <paramref name="tile" /> — nothing is recorded, so dragging within one cell stays free.
    /// </summary>
    public bool Paint(int x, int y, int tile)
    {
        int before = layer.GetTile(x: x, y: y);
        if (before == tile) return false;
        if (!layer.SetTile(x: x, y: y, tile: tile)) return false;
        _edits.Add((x, y, before, tile));
        return true;
    }

    // The renderer walks TilemapLayers live every frame, so a repaint is all an edit needs to show up.
    private void Changed() => state.NotifySceneChanged();
}

/// <summary>Add or remove a tilemap layer, preserving its position in the stack for undo.</summary>
public sealed class TilemapLayerCommand : ICommand
{
    private readonly bool _adding;
    private readonly int _index;
    private readonly TilemapLayer _layer;
    private readonly SceneNode _node;
    private readonly EditorState _state;

    private TilemapLayerCommand(EditorState state, SceneNode node, TilemapLayer layer, int index,
        bool adding)
    {
        _state = state;
        _node = node;
        _layer = layer;
        _index = index;
        _adding = adding;
    }

    public void Execute()
    {
        if (_adding) Insert();
        else Detach();
    }

    public void Undo()
    {
        if (_adding) Detach();
        else Insert();
    }

    public bool TryMergeWith(ICommand other) => false;

    public static TilemapLayerCommand Add(EditorState state, SceneNode node, TilemapLayer layer)
    {
        return new TilemapLayerCommand(
            state: state,
            node: node,
            layer: layer,
            index: node.TilemapLayers.Count,
            adding: true
        );
    }

    public static TilemapLayerCommand Remove(EditorState state, SceneNode node, TilemapLayer layer)
    {
        return new TilemapLayerCommand(
            state: state,
            node: node,
            layer: layer,
            index: node.TilemapLayers.IndexOf(layer),
            adding: false
        );
    }

    private void Insert()
    {
        _node.TilemapLayers.Insert(
            index: Math.Clamp(value: _index, min: 0, max: _node.TilemapLayers.Count),
            item: _layer
        );
        _state.NotifySceneChanged();
    }

    private void Detach()
    {
        _node.TilemapLayers.Remove(_layer);
        _state.NotifySceneChanged();
    }
}

/// <summary>Which way a tile-paint drag writes cells.</summary>
public enum TileTool
{
    /// <summary>Stamp the palette selection.</summary>
    Paint,

    /// <summary>Clear cells back to <see cref="Tileset.EmptyTile" />.</summary>
    Erase,

    /// <summary>Drag a filled rectangle of the palette selection.</summary>
    Rect,

    /// <summary>Flood-fill the contiguous region sharing the clicked cell's tile.</summary>
    Fill,

    /// <summary>Adopt the clicked cell's tile as the palette selection.</summary>
    Pick,
}
