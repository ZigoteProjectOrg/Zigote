using Zigote.Cinematics;
using Zigote.Core.Math3D;
using Zigote.Editor.History;
using Zigote.Runtime.Scene;
using Zigote.UI.Widgets.Controls;
// Dropdown<T> must be referenced with a concrete type — alias for clarity:

namespace Zigote.Editor.Panels;

public sealed partial class InspectorPanel
{
    /// <summary>Camera-node inspector: plain projection + the optional physical-camera photographic model.</summary>
    private void BuildCameraSection(SceneNode capturedNode)
    {
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow("Camera", _theme));

        // Plain FOV is only meaningful when the physical camera is not driving it.
        if (!capturedNode.PhysEnabled)
            _rows.Add(
                PropRow.Float(
                    "FOV°",
                    capturedNode.CameraFovDegrees,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<float>(
                            _state,
                            capturedNode.CameraFovDegrees,
                            v,
                            val =>
                            {
                                capturedNode.CameraFovDegrees = val;
                                capturedNode.PushCameraParams();
                            }
                        )
                    ),
                    _theme,
                    5f,
                    120f,
                    1f
                )
            );

        _rows.Add(
            PropRow.Float(
                "Near",
                capturedNode.CameraNear,
                v => _state.History.Execute(
                    new ChangePropertyCommand<float>(
                        _state,
                        capturedNode.CameraNear,
                        v,
                        val =>
                        {
                            capturedNode.CameraNear = val;
                            capturedNode.PushCameraParams();
                        }
                    )
                ),
                _theme,
                0.01f,
                10f,
                0.01f
            )
        );
        _rows.Add(
            PropRow.Float(
                "Far",
                capturedNode.CameraFar,
                v => _state.History.Execute(
                    new ChangePropertyCommand<float>(
                        _state,
                        capturedNode.CameraFar,
                        v,
                        val =>
                        {
                            capturedNode.CameraFar = val;
                            capturedNode.PushCameraParams();
                        }
                    )
                ),
                _theme,
                10f,
                10000f,
                10f
            )
        );

        // Orthographic mode drives the 2D sprite camera (and RenderView) in play mode. The native
        // 3D mesh pass itself still renders perspective — a documented limit; 2D games don't mind.
        _rows.Add(
            PropRow.DropdownRow(
                "Projection",
                ["Perspective", "Orthographic (2D)"],
                Math.Clamp(capturedNode.CameraProjection, 0, 1),
                i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        _state,
                        capturedNode.CameraProjection,
                        i,
                        val =>
                        {
                            capturedNode.CameraProjection = val;
                            Rebuild();
                        }
                    )
                ),
                _theme
            )
        );
        if (capturedNode.CameraProjection == 1)
            _rows.Add(
                PropRow.Float(
                    "Ortho Height",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.CameraOrthoSize.Y,
                        (n, v) => n.CameraOrthoSize = new Vec2(
                            n.CameraOrthoSize.X,
                            MathF.Max(0.01f, v)
                        )
                    ),
                    _theme,
                    0.1f,
                    1000f,
                    0.5f
                )
            );

        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(
            PropRow.Toggle(
                "Physical Camera",
                capturedNode.PhysEnabled,
                v => _state.History.Execute(
                    new ChangePropertyCommand<bool>(
                        _state,
                        capturedNode.PhysEnabled,
                        v,
                        val =>
                        {
                            capturedNode.PhysEnabled = val;
                            capturedNode.PushCameraParams();
                            Rebuild();
                        }
                    )
                ),
                _theme
            )
        );

        if (!capturedNode.PhysEnabled) return;

        // ── Lens ──
        _rows.Add(SectionRow("Lens", _theme));
        _rows.Add(
            PropRow.DropdownRow(
                "Sensor",
                SensorPresetNames,
                Math.Clamp(capturedNode.PhysSensorPreset, 0, SensorPresetNames.Length - 1),
                i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        _state,
                        capturedNode.PhysSensorPreset,
                        i,
                        val =>
                        {
                            capturedNode.PhysSensorPreset = val;
                            capturedNode.PushCameraParams();
                            Rebuild();
                        }
                    )
                ),
                _theme
            )
        );
        if (capturedNode.PhysSensorPreset == (int)SensorPreset.Custom)
        {
            _rows.Add(
                PropRow.Float(
                    "Sensor W mm",
                    capturedNode.PhysSensorWidthMm,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<float>(
                            _state,
                            capturedNode.PhysSensorWidthMm,
                            v,
                            val =>
                            {
                                capturedNode.PhysSensorWidthMm = val;
                                capturedNode.PushCameraParams();
                            }
                        )
                    ),
                    _theme,
                    1f,
                    100f,
                    0.5f
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Sensor H mm",
                    capturedNode.PhysSensorHeightMm,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<float>(
                            _state,
                            capturedNode.PhysSensorHeightMm,
                            v,
                            val =>
                            {
                                capturedNode.PhysSensorHeightMm = val;
                                capturedNode.PushCameraParams();
                            }
                        )
                    ),
                    _theme,
                    1f,
                    100f,
                    0.5f
                )
            );
        }

        _rows.Add(
            PropRow.Float(
                "Focal mm",
                capturedNode.PhysFocalLengthMm,
                v => _state.History.Execute(
                    new ChangePropertyCommand<float>(
                        _state,
                        capturedNode.PhysFocalLengthMm,
                        v,
                        val =>
                        {
                            capturedNode.PhysFocalLengthMm = val;
                            capturedNode.PushCameraParams();
                        }
                    )
                ),
                _theme,
                8f,
                800f,
                1f
            )
        );
        _rows.Add(
            PropRow.Float(
                "f-stop",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.PhysFStop,
                    (n, v) => n.PhysFStop = v
                ),
                _theme,
                1f,
                22f,
                0.1f
            )
        );

        var fov = capturedNode.EffectiveFovDegrees();
        _rows.Add(
            PropRow.Custom(
                new Label($"Field of view: {fov:F1}°", _theme.FontSizeCaption, _theme.Hint)
            )
        );

        // ── Exposure ──
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow("Exposure", _theme));
        _rows.Add(
            PropRow.Float(
                "ISO",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.PhysIso,
                    (n, v) => n.PhysIso = v
                ),
                _theme,
                50f,
                25600f,
                50f
            )
        );
        _rows.Add(
            PropRow.Float(
                "Shutter s",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.PhysShutterSpeed,
                    (n, v) => n.PhysShutterSpeed = v
                ),
                _theme,
                0.001f,
                0.5f,
                0.001f
            )
        );
        var ev = PhysicalCameraResolver.Ev100(
            capturedNode.PhysFStop,
            capturedNode.PhysShutterSpeed,
            capturedNode.PhysIso
        );
        _rows.Add(
            PropRow.Custom(
                new Label($"Exposure value: EV {ev:F1}", _theme.FontSizeCaption, _theme.Hint)
            )
        );
        _rows.Add(
            PropRow.Toggle(
                "Affect Exposure",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.PhysAffectExposure,
                    (n, v) => n.PhysAffectExposure = v
                ),
                _theme
            )
        );

        // ── Focus ──
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow("Focus", _theme));
        _rows.Add(
            PropRow.DropdownRow(
                "Mode",
                FocusModeNames,
                Math.Clamp(capturedNode.PhysFocusMode, 0, FocusModeNames.Length - 1),
                i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        _state,
                        capturedNode.PhysFocusMode,
                        i,
                        val =>
                        {
                            capturedNode.PhysFocusMode = val;
                            Rebuild();
                        }
                    )
                ),
                _theme
            )
        );
        if (capturedNode.PhysFocusMode == (int)FocusModeKind.Manual)
            _rows.Add(
                PropRow.Float(
                    "Distance m",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.PhysManualFocusM,
                        (n, v) => n.PhysManualFocusM = v
                    ),
                    _theme,
                    0.1f,
                    200f,
                    0.1f
                )
            );
        else
            _rows.Add(
                PropRow.Float(
                    "AF Speed",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.PhysFocusSpeed,
                        (n, v) => n.PhysFocusSpeed = v
                    ),
                    _theme,
                    0f,
                    20f,
                    0.5f
                )
            );

        if (capturedNode.PhysFocusMode == (int)FocusModeKind.Subject)
            BuildFocusTargetRow(capturedNode);

        // ── Film ──
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow("Film", _theme));
        _rows.Add(
            PropRow.DropdownRow(
                "Stock",
                FilmStockNames,
                Math.Clamp(capturedNode.PhysFilmStock, 0, FilmStockNames.Length - 1),
                i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        _state,
                        capturedNode.PhysFilmStock,
                        i,
                        val =>
                        {
                            capturedNode.PhysFilmStock = val;
                            Rebuild();
                        }
                    )
                ),
                _theme
            )
        );
        _rows.Add(
            PropRow.Float(
                "Strength",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.PhysFilmStrength,
                    (n, v) => n.PhysFilmStrength = v
                ),
                _theme
            )
        );
        _rows.Add(
            PropRow.Toggle(
                "Affect Grade",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.PhysAffectGrade,
                    (n, v) => n.PhysAffectGrade = v
                ),
                _theme
            )
        );
        _rows.Add(
            PropRow.Toggle(
                "Affect DoF",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.PhysAffectDof,
                    (n, v) => n.PhysAffectDof = v
                ),
                _theme
            )
        );

        // ── Lens FX (native-effect phase; authored now, rendered once the ABI carries them) ──
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow("Lens FX", _theme));
        _rows.Add(
            PropRow.DropdownRow(
                "Aperture",
                ApertureBladeNames,
                capturedNode.PhysApertureBlades == 0
                    ? 0
                    : Math.Clamp(capturedNode.PhysApertureBlades - 4, 0, 5),
                i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        _state,
                        capturedNode.PhysApertureBlades,
                        i == 0 ? 0 : i + 4,
                        val => capturedNode.PhysApertureBlades = val
                    )
                ),
                _theme
            )
        );
        _rows.Add(
            PropRow.Float(
                "Anamorphic",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.PhysAnamorphic,
                    (n, v) => n.PhysAnamorphic = v
                ),
                _theme,
                1f,
                2f,
                0.01f
            )
        );
        _rows.Add(
            PropRow.Float(
                "Distortion",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.PhysDistortionK1,
                    (n, v) => n.PhysDistortionK1 = v
                ),
                _theme,
                -0.5f,
                0.5f,
                0.01f
            )
        );
    }

    /// <summary>Subject-autofocus target picker: a dropdown of the scene's nameable nodes (by Id).</summary>
    private void BuildFocusTargetRow(SceneNode capturedNode)
    {
        var targets = new List<SceneNode>();
        CollectTargetNodes(_state.Scene.Root, capturedNode, targets);
        var names = new string[targets.Count + 1];
        names[0] = "None";
        for (var i = 0; i < targets.Count; i++) names[i + 1] = targets[i].Name;
        var selected = 0;
        for (var i = 0; i < targets.Count; i++)
            if (capturedNode.PhysFocusTargetNodeId == targets[i].Id)
            {
                selected = i + 1;
                break;
            }

        _rows.Add(
            PropRow.DropdownRow(
                "Target",
                names,
                selected,
                i =>
                {
                    int? id = i == 0 ? null : targets[i - 1].Id;
                    _state.History.Execute(
                        new ChangePropertyCommand<int?>(
                            _state,
                            capturedNode.PhysFocusTargetNodeId,
                            id,
                            val => capturedNode.PhysFocusTargetNodeId = val
                        )
                    );
                },
                _theme
            )
        );
    }

    private static void CollectTargetNodes(SceneNode node, SceneNode exclude, List<SceneNode> into)
    {
        if (!node.IsInternal && node != exclude && node.Kind != NodeKind.Camera)
            into.Add(node);
        foreach (var c in node.Children) CollectTargetNodes(c, exclude, into);
    }
}
