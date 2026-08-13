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
        _rows.Add(SectionRow("Tilemap", _theme));

        _rows.Add(
            PropRow.Path(
                "Tileset",
                node.TilesetPath ?? "",
                v => _state.History.Execute(
                    new ChangePropertyCommand<string?>(
                        _state,
                        node.TilesetPath,
                        v,
                        val =>
                        {
                            node.TilesetPath = val;
                            // The tileset cache keys on path; a reassignment must re-read from disk.
                            _state.Sprites2D.InvalidateTileset(val);
                        }
                    )
                ),
                _state.AssetRoot,
                [".tileset"],
                _theme,
                _app
            )
        );

        _rows.Add(
            PropRow.Float(
                "Tile Size",
                NodeBind.To(
                    _state,
                    node,
                    n => n.TileWorldSize,
                    (n, v) => n.TileWorldSize = MathF.Max(0.001f, v)
                ),
                _theme,
                0.01f,
                16f,
                0.01f
            )
        );

        _rows.Add(
            PropRow.Vec3Color(
                "Tint",
                NodeBind.To(
                    _state,
                    node,
                    n => new Vec3(n.TilemapColor.X, n.TilemapColor.Y, n.TilemapColor.Z),
                    (n, v) => n.TilemapColor = new Vec4(
                        v.X,
                        v.Y,
                        v.Z,
                        n.TilemapColor.W
                    )
                ),
                _theme
            )
        );

        _rows.Add(
            PropRow.Float(
                "Opacity",
                NodeBind.To(
                    _state,
                    node,
                    n => n.TilemapColor.W,
                    (n, v) => n.TilemapColor = new Vec4(
                        n.TilemapColor.X,
                        n.TilemapColor.Y,
                        n.TilemapColor.Z,
                        Math.Clamp(v, 0f, 1f)
                    )
                ),
                _theme,
                0f,
                1f,
                0.01f
            )
        );

        _rows.Add(
            PropRow.DropdownRow(
                "Blend",
                ["Alpha", "Additive", "Opaque"],
                Math.Clamp(node.TilemapBlend, 0, 2),
                i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        _state,
                        node.TilemapBlend,
                        i,
                        val => node.TilemapBlend = val
                    )
                ),
                _theme
            )
        );

        _rows.Add(
            PropRow.DropdownRow(
                "Stage",
                ["Scene (HDR)", "Overlay (exact)"],
                Math.Clamp(node.TilemapStage, 0, 1),
                i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        _state,
                        node.TilemapStage,
                        i,
                        val => node.TilemapStage = val
                    )
                ),
                _theme
            )
        );

        _rows.Add(
            PropRow.Toggle(
                "Tile Collision",
                NodeBind.To(
                    _state,
                    node,
                    n => n.TilemapCollision,
                    (n, v) => n.TilemapCollision = v
                ),
                _theme
            )
        );

        // A read-only summary beats a second layer editor — the Tiles panel owns layer management.
        var set = _state.Sprites2D.GetTileset(node.TilesetPath)?.Set;
        var painted = node.TilemapLayers.Sum(l => l.Width * l.Height);
        _rows.Add(
            PropRow.StatusLine(
                $"{node.TilemapLayers.Count} layer(s) · {painted} cells" +
                (set is null ? " · no tileset" : $" · {set.Columns}×{set.Rows} tiles"),
                _theme.TextSecondary,
                _theme
            )
        );
    }

    /// <summary>2D collider section — available on any node that can take part in the 2D world.</summary>
    private void BuildCollider2DRows(SceneNode node)
    {
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow("Collider 2D", _theme));

        _rows.Add(
            PropRow.Toggle(
                "Enabled",
                node.Collider2DEnabled,
                v => _state.History.Execute(
                    new ChangePropertyCommand<bool>(
                        _state,
                        node.Collider2DEnabled,
                        v,
                        val =>
                        {
                            node.Collider2DEnabled = val;
                            Rebuild(); // show/hide the shape rows
                        }
                    )
                ),
                _theme
            )
        );

        if (!node.Collider2DEnabled) return;

        // Physics2D is an axis-aligned box/circle world — no polygons, no rotation. These are all
        // the shapes it can actually simulate.
        _rows.Add(
            PropRow.DropdownRow(
                "Shape",
                ["Box", "Circle"],
                Math.Clamp(node.Collider2DShape, 0, 1),
                i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        _state,
                        node.Collider2DShape,
                        i,
                        val =>
                        {
                            node.Collider2DShape = val;
                            Rebuild();
                        }
                    )
                ),
                _theme
            )
        );

        _rows.Add(
            PropRow.Float(
                "Offset X",
                NodeBind.To(
                    _state,
                    node,
                    n => n.Collider2DOffset.X,
                    (n, v) => n.Collider2DOffset = new Vec2(v, n.Collider2DOffset.Y)
                ),
                _theme,
                -50f,
                50f,
                0.01f
            )
        );
        _rows.Add(
            PropRow.Float(
                "Offset Y",
                NodeBind.To(
                    _state,
                    node,
                    n => n.Collider2DOffset.Y,
                    (n, v) => n.Collider2DOffset = new Vec2(n.Collider2DOffset.X, v)
                ),
                _theme,
                -50f,
                50f,
                0.01f
            )
        );

        if (node.Collider2DShape == 1)
        {
            _rows.Add(
                PropRow.Float(
                    "Radius",
                    NodeBind.To(
                        _state,
                        node,
                        n => n.Collider2DRadius,
                        (n, v) => n.Collider2DRadius = MathF.Max(0.001f, v)
                    ),
                    _theme,
                    0.01f,
                    50f,
                    0.01f
                )
            );
        }
        else
        {
            _rows.Add(
                PropRow.Float(
                    "Half Width",
                    NodeBind.To(
                        _state,
                        node,
                        n => n.Collider2DSize.X,
                        (n, v) => n.Collider2DSize =
                            new Vec2(MathF.Max(0.001f, v), n.Collider2DSize.Y)
                    ),
                    _theme,
                    0.01f,
                    50f,
                    0.01f
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Half Height",
                    NodeBind.To(
                        _state,
                        node,
                        n => n.Collider2DSize.Y,
                        (n, v) => n.Collider2DSize =
                            new Vec2(n.Collider2DSize.X, MathF.Max(0.001f, v))
                    ),
                    _theme,
                    0.01f,
                    50f,
                    0.01f
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    "One-Way (up)",
                    NodeBind.To(
                        _state,
                        node,
                        n => n.Collider2DOneWayUp,
                        (n, v) => n.Collider2DOneWayUp = v
                    ),
                    _theme
                )
            );
        }

        _rows.Add(
            PropRow.Toggle(
                "Trigger",
                NodeBind.To(
                    _state,
                    node,
                    n => n.Collider2DIsTrigger,
                    (n, v) => n.Collider2DIsTrigger = v
                ),
                _theme
            )
        );

        _rows.Add(
            PropRow.Float(
                "Layer Mask",
                node.Collider2DLayer,
                v => _state.History.Execute(
                    new ChangePropertyCommand<uint>(
                        _state,
                        node.Collider2DLayer,
                        (uint)MathF.Max(0f, v),
                        val => node.Collider2DLayer = val
                    )
                ),
                _theme,
                0f,
                65535f,
                1f
            )
        );
    }
}
