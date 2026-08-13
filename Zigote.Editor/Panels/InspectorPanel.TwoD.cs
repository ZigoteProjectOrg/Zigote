using Zigote.Core.Math3D;
using Zigote.Editor.History;
using Zigote.Runtime.Scene;

namespace Zigote.Editor.Panels;

/// <summary>
///     Inspector sections for the 2D authoring types: the tilemap itself (tileset, tile size, tint,
///     material, layers) and the 2D collider any node can carry.
/// </summary>
public sealed partial class InspectorPanel
{
    /// <summary>Tilemap section — only meaningful on a <see cref="NodeKind.Tilemap" /> node.</summary>
    private void BuildTilemapRows(SceneNode node)
    {
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow(title: "Tilemap", theme: _theme));

        _rows.Add(
            PropRow.Path(
                label: "Tileset",
                value: node.TilesetPath ?? "",
                onChange: v => _state.History.Execute(
                    new ChangePropertyCommand<string?>(
                        state: _state,
                        oldValue: node.TilesetPath,
                        newValue: v,
                        setter: val =>
                        {
                            node.TilesetPath = val;
                            // The tileset cache keys on path; a reassignment must re-read from disk.
                            _state.Sprites2D.InvalidateTileset(val);
                        }
                    )
                ),
                rootPath: _state.AssetRoot,
                extensions: [".tileset"],
                theme: _theme,
                app: _app
            )
        );

        _rows.Add(
            PropRow.Float(
                label: "Tile Size",
                bind: NodeBind.To(
                    state: _state,
                    node: node,
                    getter: n => n.TileWorldSize,
                    setter: (n, v) => n.TileWorldSize = MathF.Max(x: 0.001f, y: v)
                ),
                theme: _theme,
                min: 0.01f,
                max: 16f,
                step: 0.01f
            )
        );

        _rows.Add(
            PropRow.Vec3Color(
                label: "Tint",
                bind: NodeBind.To(
                    state: _state,
                    node: node,
                    getter: n => new Vec3(
                        x: n.TilemapColor.X,
                        y: n.TilemapColor.Y,
                        z: n.TilemapColor.Z
                    ),
                    setter: (n, v) => n.TilemapColor = new Vec4(
                        x: v.X,
                        y: v.Y,
                        z: v.Z,
                        w: n.TilemapColor.W
                    )
                ),
                theme: _theme
            )
        );

        _rows.Add(
            PropRow.Float(
                label: "Opacity",
                bind: NodeBind.To(
                    state: _state,
                    node: node,
                    getter: n => n.TilemapColor.W,
                    setter: (n, v) => n.TilemapColor = new Vec4(
                        x: n.TilemapColor.X,
                        y: n.TilemapColor.Y,
                        z: n.TilemapColor.Z,
                        w: Math.Clamp(value: v, min: 0f, max: 1f)
                    )
                ),
                theme: _theme,
                min: 0f,
                max: 1f,
                step: 0.01f
            )
        );

        _rows.Add(
            PropRow.DropdownRow(
                label: "Blend",
                items: ["Alpha", "Additive", "Opaque"],
                selectedIndex: Math.Clamp(value: node.TilemapBlend, min: 0, max: 2),
                onChange: i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        state: _state,
                        oldValue: node.TilemapBlend,
                        newValue: i,
                        setter: val => node.TilemapBlend = val
                    )
                ),
                theme: _theme
            )
        );

        _rows.Add(
            PropRow.DropdownRow(
                label: "Stage",
                items: ["Scene (HDR)", "Overlay (exact)"],
                selectedIndex: Math.Clamp(value: node.TilemapStage, min: 0, max: 1),
                onChange: i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        state: _state,
                        oldValue: node.TilemapStage,
                        newValue: i,
                        setter: val => node.TilemapStage = val
                    )
                ),
                theme: _theme
            )
        );

        _rows.Add(
            PropRow.Toggle(
                label: "Tile Collision",
                bind: NodeBind.To(
                    state: _state,
                    node: node,
                    getter: n => n.TilemapCollision,
                    setter: (n, v) => n.TilemapCollision = v
                ),
                theme: _theme
            )
        );

        // A read-only summary beats a second layer editor — the Tiles panel owns layer management.
        var set = _state.Sprites2D.GetTileset(node.TilesetPath)?.Set;
        int painted = node.TilemapLayers.Sum(l => l.Width * l.Height);
        _rows.Add(
            PropRow.StatusLine(
                text: $"{node.TilemapLayers.Count} layer(s) · {painted} cells" +
                      (set is null ? " · no tileset" : $" · {set.Columns}×{set.Rows} tiles"),
                color: _theme.TextSecondary,
                theme: _theme
            )
        );
    }

    /// <summary>2D collider section — available on any node that can take part in the 2D world.</summary>
    private void BuildCollider2DRows(SceneNode node)
    {
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow(title: "Collider 2D", theme: _theme));

        _rows.Add(
            PropRow.Toggle(
                label: "Enabled",
                value: node.Collider2DEnabled,
                onChange: v => _state.History.Execute(
                    new ChangePropertyCommand<bool>(
                        state: _state,
                        oldValue: node.Collider2DEnabled,
                        newValue: v,
                        setter: val =>
                        {
                            node.Collider2DEnabled = val;
                            Rebuild(); // show/hide the shape rows
                        }
                    )
                ),
                theme: _theme
            )
        );

        if (!node.Collider2DEnabled) return;

        // Physics2D is an axis-aligned box/circle world — no polygons, no rotation. These are all
        // the shapes it can actually simulate.
        _rows.Add(
            PropRow.DropdownRow(
                label: "Shape",
                items: ["Box", "Circle"],
                selectedIndex: Math.Clamp(value: node.Collider2DShape, min: 0, max: 1),
                onChange: i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        state: _state,
                        oldValue: node.Collider2DShape,
                        newValue: i,
                        setter: val =>
                        {
                            node.Collider2DShape = val;
                            Rebuild();
                        }
                    )
                ),
                theme: _theme
            )
        );

        _rows.Add(
            PropRow.Float(
                label: "Offset X",
                bind: NodeBind.To(
                    state: _state,
                    node: node,
                    getter: n => n.Collider2DOffset.X,
                    setter: (n, v) => n.Collider2DOffset = new Vec2(x: v, y: n.Collider2DOffset.Y)
                ),
                theme: _theme,
                min: -50f,
                max: 50f,
                step: 0.01f
            )
        );
        _rows.Add(
            PropRow.Float(
                label: "Offset Y",
                bind: NodeBind.To(
                    state: _state,
                    node: node,
                    getter: n => n.Collider2DOffset.Y,
                    setter: (n, v) => n.Collider2DOffset = new Vec2(x: n.Collider2DOffset.X, y: v)
                ),
                theme: _theme,
                min: -50f,
                max: 50f,
                step: 0.01f
            )
        );

        if (node.Collider2DShape == 1)
        {
            _rows.Add(
                PropRow.Float(
                    label: "Radius",
                    bind: NodeBind.To(
                        state: _state,
                        node: node,
                        getter: n => n.Collider2DRadius,
                        setter: (n, v) => n.Collider2DRadius = MathF.Max(x: 0.001f, y: v)
                    ),
                    theme: _theme,
                    min: 0.01f,
                    max: 50f,
                    step: 0.01f
                )
            );
        }
        else
        {
            _rows.Add(
                PropRow.Float(
                    label: "Half Width",
                    bind: NodeBind.To(
                        state: _state,
                        node: node,
                        getter: n => n.Collider2DSize.X,
                        setter: (n, v) => n.Collider2DSize =
                            new Vec2(x: MathF.Max(x: 0.001f, y: v), y: n.Collider2DSize.Y)
                    ),
                    theme: _theme,
                    min: 0.01f,
                    max: 50f,
                    step: 0.01f
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Half Height",
                    bind: NodeBind.To(
                        state: _state,
                        node: node,
                        getter: n => n.Collider2DSize.Y,
                        setter: (n, v) => n.Collider2DSize =
                            new Vec2(x: n.Collider2DSize.X, y: MathF.Max(x: 0.001f, y: v))
                    ),
                    theme: _theme,
                    min: 0.01f,
                    max: 50f,
                    step: 0.01f
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    label: "One-Way (up)",
                    bind: NodeBind.To(
                        state: _state,
                        node: node,
                        getter: n => n.Collider2DOneWayUp,
                        setter: (n, v) => n.Collider2DOneWayUp = v
                    ),
                    theme: _theme
                )
            );
        }

        _rows.Add(
            PropRow.Toggle(
                label: "Trigger",
                bind: NodeBind.To(
                    state: _state,
                    node: node,
                    getter: n => n.Collider2DIsTrigger,
                    setter: (n, v) => n.Collider2DIsTrigger = v
                ),
                theme: _theme
            )
        );

        _rows.Add(
            PropRow.Float(
                label: "Layer Mask",
                value: node.Collider2DLayer,
                onChange: v => _state.History.Execute(
                    new ChangePropertyCommand<uint>(
                        state: _state,
                        oldValue: node.Collider2DLayer,
                        newValue: (uint)MathF.Max(x: 0f, y: v),
                        setter: val => node.Collider2DLayer = val
                    )
                ),
                theme: _theme,
                min: 0f,
                max: 65535f,
                step: 1f
            )
        );
    }
}
