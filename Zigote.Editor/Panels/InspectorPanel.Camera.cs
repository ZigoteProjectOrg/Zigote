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
        _rows.Add(SectionRow(title: "Camera", theme: _theme));

        // Plain FOV is only meaningful when the physical camera is not driving it.
        if (!capturedNode.PhysEnabled)
        {
            _rows.Add(
                PropRow.Float(
                    label: "FOV°",
                    value: capturedNode.CameraFovDegrees,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<float>(
                            state: _state,
                            oldValue: capturedNode.CameraFovDegrees,
                            newValue: v,
                            setter: val =>
                            {
                                capturedNode.CameraFovDegrees = val;
                                capturedNode.PushCameraParams();
                            }
                        )
                    ),
                    theme: _theme,
                    min: 5f,
                    max: 120f,
                    step: 1f
                )
            );
        }

        _rows.Add(
            PropRow.Float(
                label: "Near",
                value: capturedNode.CameraNear,
                onChange: v => _state.History.Execute(
                    new ChangePropertyCommand<float>(
                        state: _state,
                        oldValue: capturedNode.CameraNear,
                        newValue: v,
                        setter: val =>
                        {
                            capturedNode.CameraNear = val;
                            capturedNode.PushCameraParams();
                        }
                    )
                ),
                theme: _theme,
                min: 0.01f,
                max: 10f,
                step: 0.01f
            )
        );
        _rows.Add(
            PropRow.Float(
                label: "Far",
                value: capturedNode.CameraFar,
                onChange: v => _state.History.Execute(
                    new ChangePropertyCommand<float>(
                        state: _state,
                        oldValue: capturedNode.CameraFar,
                        newValue: v,
                        setter: val =>
                        {
                            capturedNode.CameraFar = val;
                            capturedNode.PushCameraParams();
                        }
                    )
                ),
                theme: _theme,
                min: 10f,
                max: 10000f,
                step: 10f
            )
        );

        // Orthographic mode drives the 2D sprite camera (and RenderView) in play mode. The native
        // 3D mesh pass itself still renders perspective — a documented limit; 2D games don't mind.
        _rows.Add(
            PropRow.DropdownRow(
                label: "Projection",
                items: ["Perspective", "Orthographic (2D)"],
                selectedIndex: Math.Clamp(value: capturedNode.CameraProjection, min: 0, max: 1),
                onChange: i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        state: _state,
                        oldValue: capturedNode.CameraProjection,
                        newValue: i,
                        setter: val =>
                        {
                            capturedNode.CameraProjection = val;
                            Rebuild();
                        }
                    )
                ),
                theme: _theme
            )
        );
        if (capturedNode.CameraProjection == 1)
        {
            _rows.Add(
                PropRow.Float(
                    label: "Ortho Height",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.CameraOrthoSize.Y,
                        setter: (n, v) => n.CameraOrthoSize = new Vec2(
                            x: n.CameraOrthoSize.X,
                            y: MathF.Max(x: 0.01f, y: v)
                        )
                    ),
                    theme: _theme,
                    min: 0.1f,
                    max: 1000f,
                    step: 0.5f
                )
            );
        }

        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(
            PropRow.Toggle(
                label: "Physical Camera",
                value: capturedNode.PhysEnabled,
                onChange: v => _state.History.Execute(
                    new ChangePropertyCommand<bool>(
                        state: _state,
                        oldValue: capturedNode.PhysEnabled,
                        newValue: v,
                        setter: val =>
                        {
                            capturedNode.PhysEnabled = val;
                            capturedNode.PushCameraParams();
                            Rebuild();
                        }
                    )
                ),
                theme: _theme
            )
        );

        if (!capturedNode.PhysEnabled) return;

        // ── Lens ──
        _rows.Add(SectionRow(title: "Lens", theme: _theme));
        _rows.Add(
            PropRow.DropdownRow(
                label: "Sensor",
                items: SensorPresetNames,
                selectedIndex: Math.Clamp(
                    value: capturedNode.PhysSensorPreset,
                    min: 0,
                    max: SensorPresetNames.Length - 1
                ),
                onChange: i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        state: _state,
                        oldValue: capturedNode.PhysSensorPreset,
                        newValue: i,
                        setter: val =>
                        {
                            capturedNode.PhysSensorPreset = val;
                            capturedNode.PushCameraParams();
                            Rebuild();
                        }
                    )
                ),
                theme: _theme
            )
        );
        if (capturedNode.PhysSensorPreset == (int)SensorPreset.Custom)
        {
            _rows.Add(
                PropRow.Float(
                    label: "Sensor W mm",
                    value: capturedNode.PhysSensorWidthMm,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<float>(
                            state: _state,
                            oldValue: capturedNode.PhysSensorWidthMm,
                            newValue: v,
                            setter: val =>
                            {
                                capturedNode.PhysSensorWidthMm = val;
                                capturedNode.PushCameraParams();
                            }
                        )
                    ),
                    theme: _theme,
                    min: 1f,
                    max: 100f,
                    step: 0.5f
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Sensor H mm",
                    value: capturedNode.PhysSensorHeightMm,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<float>(
                            state: _state,
                            oldValue: capturedNode.PhysSensorHeightMm,
                            newValue: v,
                            setter: val =>
                            {
                                capturedNode.PhysSensorHeightMm = val;
                                capturedNode.PushCameraParams();
                            }
                        )
                    ),
                    theme: _theme,
                    min: 1f,
                    max: 100f,
                    step: 0.5f
                )
            );
        }

        _rows.Add(
            PropRow.Float(
                label: "Focal mm",
                value: capturedNode.PhysFocalLengthMm,
                onChange: v => _state.History.Execute(
                    new ChangePropertyCommand<float>(
                        state: _state,
                        oldValue: capturedNode.PhysFocalLengthMm,
                        newValue: v,
                        setter: val =>
                        {
                            capturedNode.PhysFocalLengthMm = val;
                            capturedNode.PushCameraParams();
                        }
                    )
                ),
                theme: _theme,
                min: 8f,
                max: 800f,
                step: 1f
            )
        );
        _rows.Add(
            PropRow.Float(
                label: "f-stop",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.PhysFStop,
                    setter: (n, v) => n.PhysFStop = v
                ),
                theme: _theme,
                min: 1f,
                max: 22f,
                step: 0.1f
            )
        );

        float fov = capturedNode.EffectiveFovDegrees();
        _rows.Add(
            PropRow.Custom(
                new Label(
                    text: $"Field of view: {fov:F1}°",
                    fontSize: _theme.FontSizeCaption,
                    color: _theme.Hint
                )
            )
        );

        // ── Exposure ──
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow(title: "Exposure", theme: _theme));
        _rows.Add(
            PropRow.Float(
                label: "ISO",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.PhysIso,
                    setter: (n, v) => n.PhysIso = v
                ),
                theme: _theme,
                min: 50f,
                max: 25600f,
                step: 50f
            )
        );
        _rows.Add(
            PropRow.Float(
                label: "Shutter s",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.PhysShutterSpeed,
                    setter: (n, v) => n.PhysShutterSpeed = v
                ),
                theme: _theme,
                min: 0.001f,
                max: 0.5f,
                step: 0.001f
            )
        );
        float ev = PhysicalCameraResolver.Ev100(
            fStop: capturedNode.PhysFStop,
            shutterSeconds: capturedNode.PhysShutterSpeed,
            iso: capturedNode.PhysIso
        );
        _rows.Add(
            PropRow.Custom(
                new Label(
                    text: $"Exposure value: EV {ev:F1}",
                    fontSize: _theme.FontSizeCaption,
                    color: _theme.Hint
                )
            )
        );
        _rows.Add(
            PropRow.Toggle(
                label: "Affect Exposure",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.PhysAffectExposure,
                    setter: (n, v) => n.PhysAffectExposure = v
                ),
                theme: _theme
            )
        );

        // ── Focus ──
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow(title: "Focus", theme: _theme));
        _rows.Add(
            PropRow.DropdownRow(
                label: "Mode",
                items: FocusModeNames,
                selectedIndex: Math.Clamp(
                    value: capturedNode.PhysFocusMode,
                    min: 0,
                    max: FocusModeNames.Length - 1
                ),
                onChange: i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        state: _state,
                        oldValue: capturedNode.PhysFocusMode,
                        newValue: i,
                        setter: val =>
                        {
                            capturedNode.PhysFocusMode = val;
                            Rebuild();
                        }
                    )
                ),
                theme: _theme
            )
        );
        if (capturedNode.PhysFocusMode == (int)FocusModeKind.Manual)
        {
            _rows.Add(
                PropRow.Float(
                    label: "Distance m",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.PhysManualFocusM,
                        setter: (n, v) => n.PhysManualFocusM = v
                    ),
                    theme: _theme,
                    min: 0.1f,
                    max: 200f,
                    step: 0.1f
                )
            );
        }
        else
        {
            _rows.Add(
                PropRow.Float(
                    label: "AF Speed",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.PhysFocusSpeed,
                        setter: (n, v) => n.PhysFocusSpeed = v
                    ),
                    theme: _theme,
                    min: 0f,
                    max: 20f,
                    step: 0.5f
                )
            );
        }

        if (capturedNode.PhysFocusMode == (int)FocusModeKind.Subject)
            BuildFocusTargetRow(capturedNode);

        // ── Film ──
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow(title: "Film", theme: _theme));
        _rows.Add(
            PropRow.DropdownRow(
                label: "Stock",
                items: FilmStockNames,
                selectedIndex: Math.Clamp(
                    value: capturedNode.PhysFilmStock,
                    min: 0,
                    max: FilmStockNames.Length - 1
                ),
                onChange: i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        state: _state,
                        oldValue: capturedNode.PhysFilmStock,
                        newValue: i,
                        setter: val =>
                        {
                            capturedNode.PhysFilmStock = val;
                            Rebuild();
                        }
                    )
                ),
                theme: _theme
            )
        );
        _rows.Add(
            PropRow.Float(
                label: "Strength",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.PhysFilmStrength,
                    setter: (n, v) => n.PhysFilmStrength = v
                ),
                theme: _theme
            )
        );
        _rows.Add(
            PropRow.Toggle(
                label: "Affect Grade",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.PhysAffectGrade,
                    setter: (n, v) => n.PhysAffectGrade = v
                ),
                theme: _theme
            )
        );
        _rows.Add(
            PropRow.Toggle(
                label: "Affect DoF",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.PhysAffectDof,
                    setter: (n, v) => n.PhysAffectDof = v
                ),
                theme: _theme
            )
        );

        // ── Lens FX (native-effect phase; authored now, rendered once the ABI carries them) ──
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow(title: "Lens FX", theme: _theme));
        _rows.Add(
            PropRow.DropdownRow(
                label: "Aperture",
                items: ApertureBladeNames,
                selectedIndex: capturedNode.PhysApertureBlades == 0
                    ? 0
                    : Math.Clamp(value: capturedNode.PhysApertureBlades - 4, min: 0, max: 5),
                onChange: i => _state.History.Execute(
                    new ChangePropertyCommand<int>(
                        state: _state,
                        oldValue: capturedNode.PhysApertureBlades,
                        newValue: i == 0 ? 0 : i + 4,
                        setter: val => capturedNode.PhysApertureBlades = val
                    )
                ),
                theme: _theme
            )
        );
        _rows.Add(
            PropRow.Float(
                label: "Anamorphic",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.PhysAnamorphic,
                    setter: (n, v) => n.PhysAnamorphic = v
                ),
                theme: _theme,
                min: 1f,
                max: 2f,
                step: 0.01f
            )
        );
        _rows.Add(
            PropRow.Float(
                label: "Distortion",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.PhysDistortionK1,
                    setter: (n, v) => n.PhysDistortionK1 = v
                ),
                theme: _theme,
                min: -0.5f,
                max: 0.5f,
                step: 0.01f
            )
        );
    }

    /// <summary>Subject-autofocus target picker: a dropdown of the scene's nameable nodes (by Id).</summary>
    private void BuildFocusTargetRow(SceneNode capturedNode)
    {
        var targets = new List<SceneNode>();
        CollectTargetNodes(node: _state.Scene.Root, exclude: capturedNode, into: targets);
        string[] names = new string[targets.Count + 1];
        names[0] = "None";
        for (int i = 0; i < targets.Count; i++) names[i + 1] = targets[i].Name;
        int selected = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            if (capturedNode.PhysFocusTargetNodeId == targets[i].Id)
            {
                selected = i + 1;
                break;
            }
        }

        _rows.Add(
            PropRow.DropdownRow(
                label: "Target",
                items: names,
                selectedIndex: selected,
                onChange: i =>
                {
                    int? id = i == 0 ? null : targets[i - 1].Id;
                    _state.History.Execute(
                        new ChangePropertyCommand<int?>(
                            state: _state,
                            oldValue: capturedNode.PhysFocusTargetNodeId,
                            newValue: id,
                            setter: val => capturedNode.PhysFocusTargetNodeId = val
                        )
                    );
                },
                theme: _theme
            )
        );
    }

    private static void CollectTargetNodes(SceneNode node, SceneNode exclude, List<SceneNode> into)
    {
        if (!node.IsInternal && node != exclude && node.Kind != NodeKind.Camera)
            into.Add(node);
        foreach (var c in node.Children) CollectTargetNodes(node: c, exclude: exclude, into: into);
    }
}
