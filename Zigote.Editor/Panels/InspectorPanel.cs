using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zigote.Cinematics;
using Zigote.Core;
using Zigote.Core.Assets;
using Zigote.Core.Engine;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Core.Physics;
using Zigote.Editor.History;
using Zigote.Editor.Prefab;
using Zigote.Editor.Scene;
using Zigote.Editor.Shading;
using Zigote.Editor.Vfx;
using Zigote.Game.Resources;
using Zigote.Graphs.Editor;
using Zigote.Graphs.Shading;
using Zigote.Graphs.Vfx;
using Zigote.Modules.UI.CodeEditor;
using Zigote.Runtime.Prefab;
using Zigote.Runtime.Scene;
using Zigote.Scripting.Compilation;
using Zigote.Scripting.Metadata;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
// Dropdown<T> must be referenced with a concrete type — alias for clarity:
using StringDropdown = Zigote.UI.Material.Dropdown<string>;

namespace Zigote.Editor.Panels;

/// <summary>
///     Inspector panel: shows an editable name header, transform, and per-kind properties
///     for the selected SceneNode. Section headers use a colored accent bar.
/// </summary>
public sealed class InspectorPanel : Widget
{
    private static readonly string[] LightPresetNames = [
        "Candle 1900K", "Tungsten 3200K", "Warm 4000K", "Daylight 5600K",
        "Neutral 6500K", "Overcast 7000K", "Shade 8000K", "Blue Sky 10000K",
    ];

    private static readonly float[] LightPresetKelvin =
        [1900f, 3200f, 4000f, 5600f, 6500f, 7000f, 8000f, 10000f];

    // Physical-camera inspector labels (index-aligned with the Zigote.Cinematics enums).
    private static readonly string[] SensorPresetNames =
        ["Full Frame", "APS-C", "Micro 4/3", "Super 35", "IMAX", "Custom"];

    private static readonly string[] FocusModeNames = ["Manual", "Center AF", "Subject AF"];

    private static readonly string[] FilmStockNames =
        ["Neutral", "Kodak 2383", "Vision3", "Fuji Eterna", "Ektachrome", "Cineon Log", "B&W"];

    private static readonly string[] ApertureBladeNames = ["Circular", "5", "6", "7", "8", "9"];

    private readonly App _app;
    private readonly HashSet<string> _collapsedSections = [];
    private readonly List<PropRow> _rows = [];
    private readonly EditorState _state;
    private readonly ThemeData _theme;
    private bool _applyToSubMeshes;
    private Widget _content;
    private float _lastPlayRefresh;

    // Cached .prefab template for the shown instance, so override-detection doesn't re-read disk each rebuild.
    private PrefabDocument? _prefabDoc;
    private AssetId _prefabDocId = AssetId.Empty;
    private int _previewNodeId = -1;
    private bool _previewPlaying;

    // Edit-mode AudioSource audition (a single handle-based preview source, flat/non-spatial).
    private uint _previewSound;
    private SceneNode? _shown;
    private Size _size;

    public InspectorPanel(EditorState state, ThemeData theme, App app)
    {
        _state = state;
        _theme = theme;
        _app = app;
        _state.SelectionSignal.Changed += _ => Rebuild();
        _state.ScriptBuildStatusChanged += Rebuild;
        _state.SceneChanged += OnSceneChanged;
        _content = Placeholder;
        Rebuild();
    }

    private Widget Placeholder => new Padding(
        EdgeInsets.All(12f),
        new Label("Nothing selected", _theme.FontSizeBody, _theme.Hint)
    );

    /// <summary>
    ///     While playing, refresh so the inspector reflects the live (physics/script-driven) transform
    ///     values instead of a frozen snapshot. Throttled to a few times a second — a full rebuild per
    ///     simulated frame would thrash layout and fight field focus. Ignored when not playing: ordinary
    ///     edits already rebuild via <see cref="EditorState.SelectionSignal" />.
    /// </summary>
    private void OnSceneChanged()
    {
        if (!_state.IsPlaying) return;
        if (_app.Time - _lastPlayRefresh < 0.2f) return;
        Rebuild();
    }

    /// <summary>Swap the displayed content, keeping it attached to the widget tree.</summary>
    private void SetContent(Widget content)
    {
        if (ReferenceEquals(_content, content)) return;
        if (Owner != null) _content.Detach();
        _content = content;
        if (Owner != null) _content.Attach(Owner, this);
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return [_content];
    }

    /// <summary>
    ///     Open the node-based shader editor (Task 2) on a mesh node's material. The graph is
    ///     seeded from the node's current PBR values; edits recompile live and apply back to the node.
    /// </summary>
    private void OpenShaderEditor(SceneNode node)
    {
        var registry = ShaderMaterialDomain.CreateRegistry();
        var graph = ShaderMaterialDomain.CreateGraphFromNode(node);
        var preview = new MaterialPreviewWidget(_theme);
        var wgslView = new CodeEditor {
            ReadOnly = true,
            Tokenizer = Highlighting.ForExtension(".wgsl"),
        };

        var panel = new GraphEditorPanel(
            graph,
            registry,
            _theme,
            _app,
            inspectorHeader: preview,
            onCompiled: artifact =>
            {
                if (artifact is not CompiledShaderGraph cg) return;
                preview.Compiled =
                    cg; // live, per-pixel material-ball preview reflects the compiled graph
                wgslView.Text = cg.Wgsl; // the "Generated WGSL" tab tracks the graph
                ShaderMaterialDomain.ApplyTo(ShaderMaterialDomain.ToMaterial(cg), node);
                _state.NotifySceneChanged();
            }
        );

        // Seed both views so they show content before the first edit.
        var initial = ShaderGraphCompiler.Compile(graph);
        preview.Compiled = initial;
        wgslView.Text = initial.Wgsl;

        // Two tabs: the node canvas and a read-only view of the WGSL the graph generates.
        var tabs = new TabView {
            Children = {
                panel,
                wgslView,
            },
            SelectedIndex = 0,
        };
        var tabBar = new TabBar(
            [new Tab("Nodes"), new Tab("Generated WGSL")],
            0,
            i => tabs.SelectedIndex = i
        );
        var root = new Column {
            CrossAxisAlign = CrossAxisAlignment.Stretch,
            Children = {
                tabBar,
                new Expanded(tabs),
            },
        };
        new Dialog(root, _app) {
            Dismissible = true,
            WidthFraction = 0.92f,
            HeightFraction = 0.9f,
        }.Show();
    }

    /// <summary>
    ///     Open the node-based VFX editor on a VfxEmitter node. The graph is loaded from the node (or the
    ///     default preset); edits recompile live, drive the preview header, and persist back onto the
    ///     node.
    /// </summary>
    private void OpenVfxEditor(SceneNode node)
    {
        var registry = VfxNodeEditor.CreateRegistry();
        var graph = VfxNodeEditor.LoadGraph(node);
        var preview = new VfxPreviewWidget(_theme, 200f);

        var panel = new GraphEditorPanel(
            graph,
            registry,
            _theme,
            _app,
            inspectorHeader: preview,
            onCompiled: artifact =>
            {
                if (artifact is not CompiledVfxGraph cvfx) return;
                preview.Asset = cvfx.Asset; // live CPU-sim preview reflects the compiled graph
                VfxNodeEditor.SaveGraph(node, graph); // persist edits back onto the node
                _state.NotifySceneChanged();
            }
        );

        preview.Asset = VfxGraphCompiler.Compile(graph).Asset; // seed before the first edit
        new Dialog(panel, _app) {
            Dismissible = true,
            WidthFraction = 0.92f,
            HeightFraction = 0.9f,
        }.Show();
    }

    /// <summary>
    ///     Audition the selected AudioSource in edit mode: a single flat (non-spatial) preview source so
    ///     it's always audible regardless of camera. Toggles play/stop; the source is freed on stop, on a
    ///     selection change, or when play mode begins (see <see cref="Rebuild" />).
    /// </summary>
    private void TogglePreview(SceneNode node)
    {
        var engine = ZigoteEngine.Instance;
        if (engine == null) return;

        if (_previewPlaying)
        {
            StopPreview();
            Rebuild();
            return;
        }

        uint id;
        if (node.AudioUseFile)
        {
            if (string.IsNullOrEmpty(node.AudioClipPath)) return;
            var path = Path.IsPathRooted(node.AudioClipPath)
                ? node.AudioClipPath
                : Path.GetFullPath(node.AudioClipPath);
            if (!File.Exists(path)) return;
            id = engine.AudioSoundCreateFile(path, node.AudioStreaming);
        }
        else
        {
            id = engine.AudioSoundCreateTone(
                node.AudioFrequency,
                Math.Clamp(node.AudioWaveform, 0, 4)
            );
        }

        if (id == 0) return;
        engine.AudioSoundSetSpatial(id, false);
        engine.AudioSoundSetVolume(id, node.AudioVolume);
        engine.AudioSoundSetPitch(id, node.AudioPitch);
        engine.AudioSoundSetLooping(id, node.AudioLoop);
        engine.AudioSoundPlay(id);

        _previewSound = id;
        _previewPlaying = true;
        _previewNodeId = node.Id;
        Rebuild();
    }

    private void StopPreview()
    {
        if (_previewSound != 0)
        {
            var engine = ZigoteEngine.Instance;
            engine?.AudioSoundStop(_previewSound);
            engine?.AudioSoundDestroy(_previewSound);
        }

        _previewSound = 0;
        _previewPlaying = false;
        _previewNodeId = -1;
    }

    private void Rebuild()
    {
        _shown = _state.Selected;

        // Drop an edit-mode audio preview when the selection changes or play starts.
        if (_previewPlaying && (_state.IsPlaying || _shown is null || _shown.Id != _previewNodeId))
            StopPreview();

        _rows.Clear();
        _lastPlayRefresh = _app.Time;
        PropRow.History = _state.History;

        if (_shown is null)
        {
            SetContent(Placeholder);
            RequestLayout();
            return;
        }

        // Multi-select: show a summary banner above the primary node's properties
        if (_state.SelectedNodes.Count > 1)
        {
            SetContent(BuildWithMultiSelectBanner(_state.SelectedNodes.Count));
            RequestLayout();
            return;
        }

        // ── Node header: editable name + kind badge ───────────────────────────
        var nameField = new TextField {
            Text = _shown.Name,
            Height = 26f,
        };
        var capturedNode = _shown;
        nameField.OnChanged = name =>
        {
            var trimmed = name.Trim();
            if (trimmed.Length > 0 && trimmed != capturedNode.Name)
                _state.History.Execute(
                    new ChangePropertyCommand<string>(
                        _state,
                        capturedNode.Name,
                        trimmed,
                        v => capturedNode.Name = v
                    )
                );
        };

        _rows.Add(PropRow.NodeHeader(nameField, _shown.Kind, _theme));
        _rows.Add(PropRow.Spacer(6f));

        // Gameplay tag — queried at play time via the World scripting API (FindAllByTag/OverlapSphere).
        _rows.Add(
            PropRow.Text(
                "Tag",
                capturedNode.Tag ?? "",
                v =>
                {
                    var tag = v.Trim();
                    var newTag = tag.Length == 0 ? null : tag;
                    if (newTag != capturedNode.Tag)
                        _state.History.Execute(
                            new ChangePropertyCommand<string?>(
                                _state,
                                capturedNode.Tag,
                                newTag,
                                val => capturedNode.Tag = val
                            )
                        );
                },
                _theme,
                _app
            )
        );

        if (capturedNode.IsPrefabInstance) BuildPrefabBanner(capturedNode);

        // ── Transform ─────────────────────────────────────────────────────────
        _rows.Add(SectionRow("Transform", _theme));
        _rows.Add(
            PropRow.Vec3(
                "Position",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.Position,
                    (n, v) => n.Position = v
                ),
                _theme
            )
        );
        var eulerRad = _shown.Rotation.ToEulerRadians();
        var eulerDeg = new Vec3(
            eulerRad.X * (180f / MathF.PI),
            eulerRad.Y * (180f / MathF.PI),
            eulerRad.Z * (180f / MathF.PI)
        );
        _rows.Add(
            PropRow.Vec3(
                "Rotation (deg)",
                eulerDeg,
                v =>
                {
                    var newRot = Quat.FromEuler(
                        v.X * (MathF.PI / 180f),
                        v.Y * (MathF.PI / 180f),
                        v.Z * (MathF.PI / 180f)
                    );
                    _state.History.Execute(
                        new ChangePropertyCommand<Quat>(
                            _state,
                            capturedNode.Rotation,
                            newRot,
                            val => capturedNode.Rotation = val
                        )
                    );
                },
                _theme
            )
        );
        _rows.Add(
            PropRow.Vec3(
                "Scale",
                NodeBind.To(
                    _state,
                    capturedNode,
                    n => n.Scale,
                    (n, v) => n.Scale = v
                ),
                _theme
            )
        );

        // ── Kind-specific properties ──────────────────────────────────────────
        if (_shown.Kind == NodeKind.Mesh)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow("Mesh", _theme));
            _rows.Add(
                PropRow.Path(
                    "Mesh Path",
                    _shown.MeshPath ?? "",
                    v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            _state,
                            capturedNode.MeshPath,
                            v,
                            val => capturedNode.MeshPath = val
                        )
                    ),
                    _state.AssetRoot,
                    [".glb", ".fbx", ".obj"],
                    _theme,
                    _app
                )
            );
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow("Material", _theme));
            // One-click finish presets (Car Paint / Chrome / Glass / …) + apply-to-all-sub-meshes.
            _rows.Add(PropRow.Custom(BuildPresetRow()));
            _rows.Add(
                PropRow.Toggle(
                    "All sub-meshes",
                    _applyToSubMeshes,
                    v => _applyToSubMeshes = v,
                    _theme
                )
            );
            _rows.Add(
                PropRow.ColorSwatch(
                    "Color",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.MeshColor,
                        (n, v) => n.MeshColor = v
                    ),
                    _theme,
                    _app
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Metallic",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.MeshMetallic,
                        (n, v) => n.MeshMetallic = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Roughness",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.MeshRoughness,
                        (n, v) => n.MeshRoughness = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Clearcoat",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.MeshClearcoat,
                        (n, v) => n.MeshClearcoat = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Coat Rough",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.MeshClearcoatRoughness,
                        (n, v) => n.MeshClearcoatRoughness = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Specular",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.MeshSpecular,
                        (n, v) => n.MeshSpecular = v
                    ),
                    _theme,
                    0f,
                    2f
                )
            );
            _rows.Add(
                PropRow.Float(
                    "IOR",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.MeshIor,
                        (n, v) => n.MeshIor = v
                    ),
                    _theme,
                    1f,
                    3f
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Transmission",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.MeshTransmission,
                        (n, v) => n.MeshTransmission = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    "Double-Sided",
                    _shown.MeshDoubleSided,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<bool>(
                            _state,
                            capturedNode.MeshDoubleSided,
                            v,
                            val => capturedNode.MeshDoubleSided = val
                        )
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Vec3Color(
                    "Emissive",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.MeshEmissive,
                        (n, v) => n.MeshEmissive = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.DropdownRow(
                    "Alpha",
                    [
                        "Opaque", "Mask", "Blend", "Glass",
                    ], // 3 = glass (refractive + reflective), set by the glTF importer
                    (int)_shown.MeshAlphaMode,
                    i => _state.History.Execute(
                        new ChangePropertyCommand<uint>(
                            _state,
                            capturedNode.MeshAlphaMode,
                            (uint)i,
                            val => capturedNode.MeshAlphaMode = val
                        )
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Alpha Cutoff",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.MeshAlphaCutoff,
                        (n, v) => n.MeshAlphaCutoff = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Path(
                    "Texture Path",
                    _shown.TexturePath ?? "",
                    v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            _state,
                            capturedNode.TexturePath,
                            v,
                            val => capturedNode.TexturePath = val
                        )
                    ),
                    _state.AssetRoot,
                    [".png", ".jpg", ".jpeg", ".webp", ".gif"],
                    _theme,
                    _app
                )
            );
            _rows.Add(
                PropRow.Path(
                    "Normal Map",
                    _shown.NormalTexturePath ?? "",
                    v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            _state,
                            capturedNode.NormalTexturePath,
                            v,
                            val => capturedNode.NormalTexturePath = val
                        )
                    ),
                    _state.AssetRoot,
                    [".png", ".jpg", ".jpeg", ".webp"],
                    _theme,
                    _app
                )
            );
            _rows.Add(
                PropRow.Path(
                    "Emissive Map",
                    _shown.EmissiveTexturePath ?? "",
                    v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            _state,
                            capturedNode.EmissiveTexturePath,
                            v,
                            val => capturedNode.EmissiveTexturePath = val
                        )
                    ),
                    _state.AssetRoot,
                    [".png", ".jpg", ".jpeg", ".webp"],
                    _theme,
                    _app
                )
            );
            _rows.Add(
                PropRow.DropdownRow(
                    "Effect",
                    ["Standard", "CrtTv", "Unlit"],
                    (int)_shown.MeshEffect,
                    i => _state.History.Execute(
                        new ChangePropertyCommand<RenderEffect>(
                            _state,
                            capturedNode.MeshEffect,
                            (RenderEffect)i,
                            val => { capturedNode.MeshEffect = val; }
                        )
                    ),
                    _theme
                )
            );
            _rows.Add(PropRow.ActionButton("Edit as Nodes…", () => OpenShaderEditor(capturedNode)));
        }
        else if (_shown.Kind == NodeKind.Light)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow("Light", _theme));
            _rows.Add(
                PropRow.DropdownRow(
                    "Type",
                    ["Directional", "Point", "Spot"],
                    (int)_shown.LightKind,
                    i => _state.History.Execute(
                        new ChangePropertyCommand<LightType>(
                            _state,
                            capturedNode.LightKind,
                            (LightType)i,
                            val =>
                            {
                                capturedNode.LightKind = val;
                                Rebuild();
                            }
                        )
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.ColorSwatch(
                    "Color",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.LightColor,
                        (n, v) => n.LightColor = v
                    ),
                    _theme,
                    _app
                )
            );
            _rows.Add(
                PropRow.DropdownRow(
                    "Preset",
                    LightPresetNames,
                    NearestLightPreset(_shown.LightTemperature),
                    i => _state.History.Execute(
                        new ChangePropertyCommand<float>(
                            _state,
                            capturedNode.LightTemperature,
                            LightPresetKelvin[i],
                            val =>
                            {
                                capturedNode.LightTemperature = val;
                                Rebuild();
                            }
                        )
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Temp (K)",
                    _shown.LightTemperature,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<float>(
                            _state,
                            capturedNode.LightTemperature,
                            v,
                            val => capturedNode.LightTemperature = val
                        )
                    ),
                    _theme,
                    1500f,
                    12000f,
                    100f
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Intensity",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.LightIntensity,
                        (n, v) => n.LightIntensity = v
                    ),
                    _theme,
                    0f,
                    20f,
                    0.1f
                )
            );
            if (_shown.LightKind != LightType.Directional)
                _rows.Add(
                    PropRow.Float(
                        "Range",
                        NodeBind.To(
                            _state,
                            capturedNode,
                            n => n.LightRange,
                            (n, v) => n.LightRange = v
                        ),
                        _theme,
                        0f,
                        200f,
                        1f
                    )
                );
            if (_shown.LightKind == LightType.Spot)
            {
                _rows.Add(
                    PropRow.Float(
                        "Inner°",
                        _shown.SpotInnerAngleDeg,
                        v => _state.History.Execute(
                            new ChangePropertyCommand<float>(
                                _state,
                                capturedNode.SpotInnerAngleDeg,
                                v,
                                val => capturedNode.SpotInnerAngleDeg = MathF.Min(
                                    val,
                                    capturedNode.SpotOuterAngleDeg
                                )
                            )
                        ),
                        _theme,
                        1f,
                        88f,
                        1f
                    )
                );
                _rows.Add(
                    PropRow.Float(
                        "Outer°",
                        _shown.SpotOuterAngleDeg,
                        v => _state.History.Execute(
                            new ChangePropertyCommand<float>(
                                _state,
                                capturedNode.SpotOuterAngleDeg,
                                v,
                                val => capturedNode.SpotOuterAngleDeg = MathF.Max(
                                    val,
                                    capturedNode.SpotInnerAngleDeg
                                )
                            )
                        ),
                        _theme,
                        1f,
                        89f,
                        1f
                    )
                );
            }

            _rows.Add(
                PropRow.Toggle(
                    "Cast Shadows",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.LightCastShadows,
                        (n, v) => n.LightCastShadows = v
                    ),
                    _theme
                )
            );
        }
        else if (_shown.Kind == NodeKind.Camera)
        {
            BuildCameraSection(capturedNode);
        }
        else if (_shown.Kind == NodeKind.ReflectionProbe)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow("Reflection Probe", _theme));
            _rows.Add(
                PropRow.Vec3(
                    "Box Extents",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.ProbeExtents,
                        (n, v) => n.ProbeExtents = v
                    ),
                    _theme
                )
            );
        }
        else if (_shown.Kind == NodeKind.AudioSource)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow("Audio Source", _theme));
            _rows.Add(
                PropRow.Toggle(
                    "Use File",
                    _shown.AudioUseFile,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<bool>(
                            _state,
                            capturedNode.AudioUseFile,
                            v,
                            val =>
                            {
                                capturedNode.AudioUseFile = val;
                                Rebuild();
                            }
                        )
                    ),
                    _theme
                )
            );
            if (_shown.AudioUseFile)
            {
                _rows.Add(
                    PropRow.Path(
                        "Clip",
                        _shown.AudioClipPath ?? "",
                        v => _state.History.Execute(
                            new ChangePropertyCommand<string?>(
                                _state,
                                capturedNode.AudioClipPath,
                                v,
                                val => capturedNode.AudioClipPath = val
                            )
                        ),
                        _state.AssetRoot,
                        [".wav", ".ogg", ".mp3", ".flac"],
                        _theme,
                        _app
                    )
                );
                _rows.Add(
                    PropRow.Toggle(
                        "Stream",
                        NodeBind.To(
                            _state,
                            capturedNode,
                            n => n.AudioStreaming,
                            (n, v) => n.AudioStreaming = v
                        ),
                        _theme
                    )
                );
            }
            else
            {
                _rows.Add(
                    PropRow.DropdownRow(
                        "Waveform",
                        ["Sine", "Square", "Triangle", "Sawtooth", "Noise"],
                        Math.Clamp(_shown.AudioWaveform, 0, 4),
                        i => _state.History.Execute(
                            new ChangePropertyCommand<int>(
                                _state,
                                capturedNode.AudioWaveform,
                                i,
                                val => capturedNode.AudioWaveform = val
                            )
                        ),
                        _theme
                    )
                );
                _rows.Add(
                    PropRow.Float(
                        "Frequency",
                        NodeBind.To(
                            _state,
                            capturedNode,
                            n => n.AudioFrequency,
                            (n, v) => n.AudioFrequency = v
                        ),
                        _theme,
                        20f,
                        4000f,
                        10f
                    )
                );
            }

            _rows.Add(
                PropRow.Float(
                    "Volume",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.AudioVolume,
                        (n, v) => n.AudioVolume = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Pitch",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.AudioPitch,
                        (n, v) => n.AudioPitch = v
                    ),
                    _theme,
                    0.25f,
                    4f
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    "Loop",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.AudioLoop,
                        (n, v) => n.AudioLoop = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    "Auto Play",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.AudioAutoPlay,
                        (n, v) => n.AudioAutoPlay = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    "Spatial (3D)",
                    _shown.AudioSpatial,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<bool>(
                            _state,
                            capturedNode.AudioSpatial,
                            v,
                            val =>
                            {
                                capturedNode.AudioSpatial = val;
                                Rebuild();
                            }
                        )
                    ),
                    _theme
                )
            );
            if (_shown.AudioSpatial)
            {
                _rows.Add(
                    PropRow.Float(
                        "Min Dist",
                        NodeBind.To(
                            _state,
                            capturedNode,
                            n => n.AudioMinDistance,
                            (n, v) => n.AudioMinDistance = v
                        ),
                        _theme,
                        0.1f,
                        100f,
                        0.5f
                    )
                );
                _rows.Add(
                    PropRow.Float(
                        "Max Dist",
                        NodeBind.To(
                            _state,
                            capturedNode,
                            n => n.AudioMaxDistance,
                            (n, v) => n.AudioMaxDistance = v
                        ),
                        _theme,
                        1f,
                        1000f,
                        1f
                    )
                );
                _rows.Add(
                    PropRow.Float(
                        "Rolloff",
                        NodeBind.To(
                            _state,
                            capturedNode,
                            n => n.AudioRolloff,
                            (n, v) => n.AudioRolloff = v
                        ),
                        _theme,
                        0f,
                        4f,
                        0.1f
                    )
                );
            }

            _rows.Add(
                PropRow.ActionButton(
                    _previewPlaying ? "Stop Preview" : "Preview",
                    () => TogglePreview(capturedNode)
                )
            );
        }
        else if (_shown.Kind == NodeKind.VfxEmitter)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow("VFX Emitter", _theme));

            // Live CPU-sim preview of the node's current graph.
            _rows.Add(
                PropRow.Custom(
                    new VfxPreviewWidget(_theme) {
                        Asset = VfxNodeEditor.Compile(capturedNode).Asset,
                    }
                )
            );

            _rows.Add(
                PropRow.DropdownRow(
                    "Preset",
                    VfxPresets.Names.ToArray(),
                    0,
                    i =>
                    {
                        var json = VfxGraphSerializer.Serialize(
                            VfxPresets.Create(VfxPresets.Names[i], capturedNode.Name)
                        );
                        _state.History.Execute(
                            new ChangePropertyCommand<string?>(
                                _state,
                                capturedNode.VfxGraphJson,
                                json,
                                val => capturedNode.VfxGraphJson = val
                            )
                        );
                        Rebuild();
                    },
                    _theme
                )
            );

            _rows.Add(
                PropRow.Toggle(
                    "Play On Start",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.VfxPlayOnStart,
                        (n, v) => n.VfxPlayOnStart = v
                    ),
                    _theme
                )
            );

            _rows.Add(PropRow.ActionButton("Edit as Nodes…", () => OpenVfxEditor(capturedNode)));
        }
        else if (_shown.Kind == NodeKind.Sprite)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow("Sprite", _theme));
            _rows.Add(
                PropRow.Path(
                    "Texture",
                    _shown.TexturePath ?? "",
                    v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            _state,
                            capturedNode.TexturePath,
                            v,
                            val => capturedNode.TexturePath = val
                        )
                    ),
                    _state.AssetRoot,
                    [".png", ".jpg", ".jpeg", ".webp", ".gif"],
                    _theme,
                    _app
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Pixels/Unit",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.SpritePixelsPerUnit,
                        (n, v) => n.SpritePixelsPerUnit = MathF.Max(0.001f, v)
                    ),
                    _theme,
                    1f,
                    1024f,
                    1f
                )
            );
            _rows.Add(
                PropRow.Vec3Color(
                    "Tint",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => new Vec3(n.SpriteColor.X, n.SpriteColor.Y, n.SpriteColor.Z),
                        (n, v) => n.SpriteColor = new Vec4(
                            v.X,
                            v.Y,
                            v.Z,
                            n.SpriteColor.W
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
                        capturedNode,
                        n => n.SpriteColor.W,
                        (n, v) => n.SpriteColor = new Vec4(
                            n.SpriteColor.X,
                            n.SpriteColor.Y,
                            n.SpriteColor.Z,
                            Math.Clamp(v, 0f, 1f)
                        )
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    "Flip X",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.SpriteFlipX,
                        (n, v) => n.SpriteFlipX = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    "Flip Y",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.SpriteFlipY,
                        (n, v) => n.SpriteFlipY = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Pivot X",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.SpritePivotX,
                        (n, v) => n.SpritePivotX = v
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Pivot Y",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.SpritePivotY,
                        (n, v) => n.SpritePivotY = v
                    ),
                    _theme
                )
            );

            _rows.Add(SectionRow("Sprite Sheet", _theme));
            _rows.Add(
                PropRow.Float(
                    "Columns",
                    capturedNode.SpriteCols,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            _state,
                            capturedNode.SpriteCols,
                            Math.Max(1, (int)v),
                            val => capturedNode.SpriteCols = val
                        )
                    ),
                    _theme,
                    1f,
                    64f,
                    1f
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Rows",
                    capturedNode.SpriteRows,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            _state,
                            capturedNode.SpriteRows,
                            Math.Max(1, (int)v),
                            val => capturedNode.SpriteRows = val
                        )
                    ),
                    _theme,
                    1f,
                    64f,
                    1f
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Frame",
                    capturedNode.SpriteFrame,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            _state,
                            capturedNode.SpriteFrame,
                            Math.Max(0, (int)v),
                            val => capturedNode.SpriteFrame = val
                        )
                    ),
                    _theme,
                    0f,
                    4095f,
                    1f
                )
            );
            _rows.Add(
                PropRow.Float(
                    "FPS",
                    NodeBind.To(
                        _state,
                        capturedNode,
                        n => n.SpriteFps,
                        (n, v) => n.SpriteFps = MathF.Max(0f, v)
                    ),
                    _theme,
                    0f,
                    60f,
                    1f
                )
            );

            _rows.Add(SectionRow("Material", _theme));
            _rows.Add(
                PropRow.DropdownRow(
                    "Blend",
                    ["Alpha", "Additive", "Opaque"],
                    Math.Clamp(_shown.SpriteBlend, 0, 2),
                    i => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            _state,
                            capturedNode.SpriteBlend,
                            i,
                            val => capturedNode.SpriteBlend = val
                        )
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.DropdownRow(
                    "Stage",
                    ["Scene (HDR)", "Overlay (exact)"],
                    Math.Clamp(_shown.SpriteStage, 0, 1),
                    i => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            _state,
                            capturedNode.SpriteStage,
                            i,
                            val => capturedNode.SpriteStage = val
                        )
                    ),
                    _theme
                )
            );
            _rows.Add(
                PropRow.Path(
                    "Shader (.wgsl)",
                    _shown.SpriteShaderPath ?? "",
                    v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            _state,
                            capturedNode.SpriteShaderPath,
                            v,
                            val => capturedNode.SpriteShaderPath = val
                        )
                    ),
                    _state.AssetRoot,
                    [".wgsl"],
                    _theme,
                    _app
                )
            );

            _rows.Add(SectionRow("Sorting", _theme));
            _rows.Add(
                PropRow.Float(
                    "Layer",
                    capturedNode.SpriteSortingLayer,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            _state,
                            capturedNode.SpriteSortingLayer,
                            (int)v,
                            val => capturedNode.SpriteSortingLayer = val
                        )
                    ),
                    _theme,
                    -100f,
                    100f,
                    1f
                )
            );
            _rows.Add(
                PropRow.Float(
                    "Order",
                    capturedNode.SpriteOrderInLayer,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            _state,
                            capturedNode.SpriteOrderInLayer,
                            (int)v,
                            val => capturedNode.SpriteOrderInLayer = val
                        )
                    ),
                    _theme,
                    -100f,
                    100f,
                    1f
                )
            );
        }

        if (_shown.Kind is NodeKind.Mesh or NodeKind.Empty)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow("Physics", _theme));
            _rows.Add(
                PropRow.Toggle(
                    "Use Physics",
                    _shown.UsePhysics,
                    v => _state.History.Execute(
                        new ChangePropertyCommand<bool>(
                            _state,
                            capturedNode.UsePhysics,
                            v,
                            val =>
                            {
                                capturedNode.UsePhysics = val;
                                Rebuild();
                            }
                        )
                    ),
                    _theme
                )
            );

            if (_shown.UsePhysics)
            {
                _rows.Add(
                    PropRow.Toggle(
                        "Static",
                        _shown.IsStatic,
                        v => _state.History.Execute(
                            new ChangePropertyCommand<bool>(
                                _state,
                                capturedNode.IsStatic,
                                v,
                                val =>
                                {
                                    capturedNode.IsStatic = val;
                                    Rebuild();
                                }
                            )
                        ),
                        _theme
                    )
                );
                _rows.Add(
                    PropRow.Toggle(
                        "Use Gravity",
                        NodeBind.To(
                            _state,
                            capturedNode,
                            n => n.UseGravity,
                            (n, v) => n.UseGravity = v
                        ),
                        _theme
                    )
                );
                _rows.Add(
                    PropRow.DropdownRow(
                        "Shape",
                        ["Box", "Sphere", "Capsule", "Cylinder"],
                        (int)_shown.PhysicsShape,
                        i => _state.History.Execute(
                            new ChangePropertyCommand<PhysicsShapeType>(
                                _state,
                                capturedNode.PhysicsShape,
                                (PhysicsShapeType)i,
                                val => capturedNode.PhysicsShape = val
                            )
                        ),
                        _theme
                    )
                );
                _rows.Add(
                    PropRow.Vec3(
                        "Half Extents",
                        NodeBind.To(
                            _state,
                            capturedNode,
                            n => n.PhysicsHalfExtents,
                            (n, v) => n.PhysicsHalfExtents = v
                        ),
                        _theme
                    )
                );
                if (!_shown.IsStatic)
                    _rows.Add(
                        PropRow.Float(
                            "Mass",
                            NodeBind.To(
                                _state,
                                capturedNode,
                                n => n.PhysicsMass,
                                (n, v) => n.PhysicsMass = v
                            ),
                            _theme,
                            0.01f,
                            1000f,
                            0.5f
                        )
                    );
                _rows.Add(
                    PropRow.Float(
                        "Friction",
                        NodeBind.To(
                            _state,
                            capturedNode,
                            n => n.PhysicsFriction,
                            (n, v) => n.PhysicsFriction = v
                        ),
                        _theme
                    )
                );
                _rows.Add(
                    PropRow.Float(
                        "Restitution",
                        NodeBind.To(
                            _state,
                            capturedNode,
                            n => n.PhysicsRestitution,
                            (n, v) => n.PhysicsRestitution = v
                        ),
                        _theme
                    )
                );
            }
        }

        // ── Script (available on every node kind) ─────────────────────────────
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow("Script", _theme));
        _rows.Add(
            PropRow.Suggest(
                "Class",
                _shown.ScriptClass ?? "",
                q => _state.ScriptRegistry.All
                    .Where(m => string.IsNullOrEmpty(q)
                                || m.FullName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                || m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    )
                    .OrderByDescending(m => m.DisplayName.StartsWith(
                            q,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .ThenBy(m => m.DisplayName)
                    .Select(m => (m.FullName, m.DisplayName))
                    .Take(12)
                    .ToList(),
                v => _state.History.Execute(
                    new ChangePropertyCommand<string?>(
                        _state,
                        capturedNode.ScriptClass,
                        v,
                        val =>
                        {
                            capturedNode.ScriptClass = val;
                            Rebuild();
                        }
                    )
                ),
                _theme,
                _app
            )
        );
        _rows.Add(
            PropRow.Path(
                "Path",
                _shown.ScriptPath ?? "",
                v => _state.History.Execute(
                    new ChangePropertyCommand<string?>(
                        _state,
                        capturedNode.ScriptPath,
                        v,
                        val => capturedNode.ScriptPath = val
                    )
                ),
                _state.AssetRoot,
                [".csproj", ".cs"],
                _theme,
                _app
            )
        );
        _rows.Add(
            PropRow.ActionButton(
                "Build & Reload",
                () =>
                {
                    var path = ResolveProjectPath(capturedNode.ScriptPath);
                    if (path != null) _ = _state.BuildScriptsAsync(path);
                }
            )
        );

        // Build status
        if (_state.IsScriptBuilding)
        {
            _rows.Add(PropRow.StatusLine("Building...", _theme.Hint, _theme));
        }
        else if (_state.ScriptDiagnostics.Count > 0)
        {
            var errors =
                _state.ScriptDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnings =
                _state.ScriptDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            var summary = errors > 0 ? $"{errors} error{(errors != 1 ? "s" : "")}" : "";
            if (warnings > 0)
                summary += (summary.Length > 0 ? ", " : "") +
                           $"{warnings} warning{(warnings != 1 ? "s" : "")}";
            _rows.Add(
                PropRow.StatusLine(summary, errors > 0 ? _theme.Error : _theme.Accent, _theme)
            );
            foreach (var d in _state.ScriptDiagnostics.Take(8))
                _rows.Add(PropRow.DiagnosticLine(d, _theme));
        }

        if (!string.IsNullOrEmpty(_shown.ScriptClass))
        {
            var meta = _state.ScriptRegistry.Find(_shown.ScriptClass);
            if (meta?.ExportedFields.Length > 0)
            {
                _rows.Add(PropRow.Spacer(4f));
                _rows.Add(SectionRow("Properties", _theme));
                foreach (var field in meta.ExportedFields)
                    _rows.Add(BuildExportedFieldRow(field, meta, capturedNode));
            }
        }

        // Drop rows belonging to a collapsed section (everything between a collapsed header and the
        // next header). Headers always show so the section can be re-expanded.
        var visible = new List<PropRow>(_rows.Count);
        var collapsing = false;
        foreach (var r in _rows)
            if (r.IsSectionHeader)
            {
                collapsing = r.SectionTitle != null && _collapsedSections.Contains(r.SectionTitle);
                visible.Add(r);
            }
            else if (!collapsing)
            {
                visible.Add(r);
            }

        var col = new Column {
            MainAxisAlign = MainAxisAlignment.Start,
            CrossAxisAlign = CrossAxisAlignment.Start,
        };
        col.Children.AddRange(visible);
        SetContent(col);
        RequestLayout();
    }

    private static int NearestLightPreset(float kelvin)
    {
        var best = 0;
        var bd = float.MaxValue;
        for (var i = 0; i < LightPresetKelvin.Length; i++)
        {
            var d = MathF.Abs(LightPresetKelvin[i] - kelvin);
            if (d < bd)
            {
                bd = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>A grid of one-click material-finish preset buttons (Car Paint / Chrome / Glass / …).</summary>
    private Widget BuildPresetRow()
    {
        var presets = MaterialPresets.All;
        var col = new Column {
            CrossAxisAlign = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Min,
        };
        for (var r = 0; r < presets.Count; r += 3)
        {
            var row = new Row {
                MainAxisAlign = MainAxisAlignment.Start,
                CrossAxisAlign = CrossAxisAlignment.Center,
            };
            for (var i = r; i < Math.Min(r + 3, presets.Count); i++)
            {
                var p = presets[i];
                row.Children.Add(
                    new SizedBox(
                        74f,
                        22f,
                        new Button(p.Name, () => ApplyMaterialPreset(p)) {
                            FontSize = _theme.FontSizeCaption - 1f,
                        }
                    )
                );
                row.Children.Add(new SizedBox(4f));
            }

            col.Children.Add(row);
            col.Children.Add(new SizedBox(height: 4f));
        }

        return col;
    }

    /// <summary>
    ///     Apply a finish preset to the selected mesh (and, when toggled, all its mesh descendants)
    ///     as one undo step.
    /// </summary>
    private void ApplyMaterialPreset(MaterialPreset preset)
    {
        if (_shown is null) return;
        var root = _shown;
        var scope = _applyToSubMeshes
            ? root.Descendants().Prepend(root)
            : new[] { root }.AsEnumerable();
        var targets = scope.Where(n => n.Kind == NodeKind.Mesh).ToList();
        if (targets.Count == 0) return;

        var before = targets.Select(MeshMaterialSnapshot.Of).ToList();
        _state.History.Execute(
            new CompositeCommand(
                _state,
                () =>
                {
                    foreach (var t in targets) preset.ApplyTo(t);
                },
                () =>
                {
                    for (var i = 0; i < targets.Count; i++) before[i].RestoreTo(targets[i]);
                }
            )
        );
        Rebuild();
    }

    // ── Prefab instance banner (override indicators + revert) ──────────────────

    /// <summary>
    ///     Header shown above a prefab instance's properties: the source prefab name, how many component
    ///     groups are overridden, and a per-component (+ "Revert All") revert button. Override state is a
    ///     diff of the instance's authorable POD components against the <c>.prefab</c> template — the same
    ///     per-component model as flecs <c>EcsPrefab</c>'s <c>Owns</c>.
    /// </summary>
    private void BuildPrefabBanner(SceneNode node)
    {
        if (node.PrefabSource != _prefabDocId)
        {
            _prefabDoc = _state.Prefabs.Load(node.PrefabSource);
            _prefabDocId = node.PrefabSource;
        }

        if (_prefabDoc is not { } doc)
        {
            _rows.Add(
                PropRow.StatusLine("◆ Prefab instance (template missing)", _theme.Hint, _theme)
            );
            _rows.Add(PropRow.Spacer(6f));
            return;
        }

        var overridden = PrefabOverrides.ApplicableTo(node)
            .Where(c => PrefabOverrides.IsOverridden(c, node, doc.Template))
            .ToList();

        _rows.Add(
            PropRow.StatusLine(
                overridden.Count == 0
                    ? $"◆ Prefab · {doc.Name}"
                    : $"◆ Prefab · {doc.Name}  ({overridden.Count} overridden)",
                _theme.Accent,
                _theme
            )
        );

        foreach (var c in overridden)
        {
            var component = c;
            _rows.Add(
                PropRow.ActionButton(
                    $"Revert {component}",
                    () => RevertPrefabComponent(node, component)
                )
            );
        }

        if (overridden.Count > 1)
            _rows.Add(
                PropRow.ActionButton(
                    "Revert All",
                    () =>
                    {
                        foreach (var c in overridden) RevertPrefabComponent(node, c);
                    }
                )
            );

        _rows.Add(PropRow.Spacer(6f));
    }

    private void RevertPrefabComponent(SceneNode node, PrefabComponent component)
    {
        if (_prefabDoc is not { } doc) return;
        var before = PrefabOverrides.Capture(component, node);
        _state.History.Execute(
            new CompositeCommand(
                _state,
                () => PrefabOverrides.Revert(component, node, doc.Template),
                () => PrefabOverrides.Restore(component, node, before)
            )
        );
        Rebuild(); // refresh the override indicators
    }

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

    /// <summary>A collapsible section header row; clicking it toggles the section's rows.</summary>
    private PropRow SectionRow(string title, ThemeData theme)
    {
        var collapsed = _collapsedSections.Contains(title);
        var header = new SectionHeader(
            title,
            theme,
            collapsed,
            () =>
            {
                if (!_collapsedSections.Remove(title)) _collapsedSections.Add(title);
                Rebuild();
                RequestLayout();
            }
        );
        return PropRow.Section(header, title);
    }

    private Widget BuildWithMultiSelectBanner(int count)
    {
        // Show a compact summary listing all selected node names.
        var col = new Column {
            MainAxisAlign = MainAxisAlignment.Start,
            CrossAxisAlign = CrossAxisAlignment.Start,
        };
        col.Children.Add(
            new Padding(
                EdgeInsets.All(8f),
                new ColoredBox(
                    _theme.Primary.WithAlpha(0.12f),
                    new Padding(
                        EdgeInsets.Symmetric(8f, 6f),
                        new Label($"{count} nodes selected", _theme.FontSizeCaption, _theme.Primary)
                    )
                )
            )
        );
        col.Children.Add(
            new Padding(
                EdgeInsets.Symmetric(12f, 4f),
                new Label(
                    "Ctrl+click to toggle, Shift+click to range-select.",
                    _theme.FontSizeCaption,
                    _theme.Hint
                )
            )
        );
        foreach (var n in _state.SelectedNodes.Take(20))
            col.Children.Add(
                new Padding(
                    new EdgeInsets(
                        12f,
                        2f,
                        4f,
                        2f
                    ),
                    new Label($"  • {n.Name}", _theme.FontSizeCaption, _theme.OnSurface)
                )
            );
        if (count > 20)
            col.Children.Add(
                new Padding(
                    EdgeInsets.Symmetric(12f, 2f),
                    new Label($"  … and {count - 20} more", _theme.FontSizeCaption, _theme.Hint)
                )
            );
        RequestLayout();
        return col;
    }

    private PropRow BuildExportedFieldRow(ExportedField field, ScriptMetadata meta, SceneNode node)
    {
        // Fall back to the script's compiled-in default when the node has no stored override, so a
        // freshly attached script shows its real defaults (e.g. Speed = 90) instead of zeros.
        if (!node.ScriptExports.TryGetValue(field.Name, out var currentJson))
            currentJson = meta.DefaultExports.GetValueOrDefault(field.Name);

        void SaveJson(string json)
        {
            node.ScriptExports[field.Name] = json;
            _state.ApplyLiveScriptExport(
                node,
                field,
                json
            ); // live-tune the running component in play mode
            _state.NotifySceneChanged();
        }

        switch (field.Kind)
        {
            case ExportedFieldKind.Bool:
            {
                var cur = "true".Equals(currentJson, StringComparison.OrdinalIgnoreCase);
                return PropRow.Toggle(
                    field.DisplayName,
                    cur,
                    v => SaveJson(v ? "true" : "false"),
                    _theme
                );
            }
            case ExportedFieldKind.Int:
            {
                var cur = int.TryParse(currentJson, out var i) ? i : 0f;
                return PropRow.Float(
                    field.DisplayName,
                    cur,
                    v => SaveJson(((int)v).ToString()),
                    _theme,
                    (float)(field.RangeMin ?? 0),
                    (float)(field.RangeMax ?? 1000),
                    1f
                );
            }
            case ExportedFieldKind.Float:
            {
                var cur = float.TryParse(
                    currentJson,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var f
                )
                    ? f
                    : 0f;
                return PropRow.Float(
                    field.DisplayName,
                    cur,
                    v => SaveJson(v.ToString(CultureInfo.InvariantCulture)),
                    _theme,
                    (float)(field.RangeMin ?? 0f),
                    (float)(field.RangeMax ?? 100f),
                    0.1f
                );
            }
            case ExportedFieldKind.String:
            {
                var cur = currentJson != null
                    ? JsonSerializer.Deserialize<string>(currentJson) ?? ""
                    : "";
                return PropRow.Text(
                    field.DisplayName,
                    cur,
                    v => SaveJson(JsonSerializer.Serialize(v)),
                    _theme,
                    _app
                );
            }
            case ExportedFieldKind.Vec3:
            {
                var cur = Vec3.Zero;
                if (currentJson != null)
                    try
                    {
                        var n = JsonNode.Parse(currentJson)!;
                        cur = new Vec3(
                            n["x"]!.GetValue<float>(),
                            n["y"]!.GetValue<float>(),
                            n["z"]!.GetValue<float>()
                        );
                    }
                    catch
                    {
                        /* use default */
                    }

                return field.IsColor
                    ? PropRow.Vec3Color(
                        field.DisplayName,
                        cur,
                        v => SaveJson(
                            $"{{\"x\":{v.X.ToString("G", CultureInfo.InvariantCulture)},\"y\":{v.Y.ToString("G", CultureInfo.InvariantCulture)},\"z\":{v.Z.ToString("G", CultureInfo.InvariantCulture)}}}"
                        ),
                        _theme
                    )
                    : PropRow.Vec3(
                        field.DisplayName,
                        cur,
                        v => SaveJson(
                            $"{{\"x\":{v.X.ToString("G", CultureInfo.InvariantCulture)},\"y\":{v.Y.ToString("G", CultureInfo.InvariantCulture)},\"z\":{v.Z.ToString("G", CultureInfo.InvariantCulture)}}}"
                        ),
                        _theme
                    );
            }
            default:
                return PropRow.Text(
                    field.DisplayName,
                    currentJson ?? "",
                    v => SaveJson(v),
                    _theme,
                    _app
                );
        }
    }

    private static string? ResolveProjectPath(string? scriptPath)
    {
        if (string.IsNullOrEmpty(scriptPath)) return null;
        if (scriptPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return scriptPath;
        var dir = Path.GetDirectoryName(scriptPath);
        return dir != null
            ? Directory.GetFiles(dir, "*.csproj").FirstOrDefault()
            : null;
    }

    public override Size Measure(Constraints c)
    {
        _size = _content.Measure(c);
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
        _content.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _content.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _content.HitTest(point);
    }

    // ── Property row ──────────────────────────────────────────────────────────

    private sealed class PropRow : Widget
    {
        /// <summary>
        ///     Command history used by scrub-capable rows (Float) to coalesce a drag into one undo entry.
        ///     Set at the top of <see cref="InspectorPanel.Rebuild" />.
        /// </summary>
        internal static CommandHistory? History;

        private readonly Widget _inner;
        private Size _size;

        private PropRow(Widget inner)
        {
            _inner = inner;
        }

        /// <summary>True when this row is a section header (used by collapse filtering).</summary>
        public bool IsSectionHeader { get; private init; }

        /// <summary>The section title this header toggles (set for section headers only).</summary>
        public string? SectionTitle { get; private init; }

        /// <summary>Spacer between sections.</summary>
        public static PropRow Spacer(float height)
        {
            return new PropRow(new SizedBox(height: height));
        }

        /// <summary>Full-width action button row.</summary>
        public static PropRow ActionButton(string label, Action onClick)
        {
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new SizedBox(height: 26f, child: new Button(label, onClick))
                )
            );
        }

        /// <summary>Single-line status text (build result summary, "Building...", etc.).</summary>
        public static PropRow StatusLine(string text, Color color, ThemeData theme)
        {
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        2f
                    ),
                    new Label(text, theme.FontSizeCaption, color)
                )
            );
        }

        /// <summary>One compiler diagnostic displayed as a small indented row.</summary>
        public static PropRow DiagnosticLine(ScriptDiagnostic d, ThemeData theme)
        {
            var color = d.Severity == DiagnosticSeverity.Error ? theme.Error : theme.Accent;
            var file = d.File != null ? System.IO.Path.GetFileName(d.File) : "";
            var loc = file.Length > 0 ? $"{file}({d.Line}): " : "";
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        8f,
                        0f,
                        0f,
                        1f
                    ),
                    new Label(
                        $"{loc}{d.Message}",
                        theme.FontSizeCaption - 1f,
                        color.WithAlpha(0.85f)
                    )
                )
            );
        }

        /// <summary>Node name field + kind badge at the top of the inspector.</summary>
        public static PropRow NodeHeader(TextField nameField, NodeKind kind, ThemeData theme)
        {
            var kindColor = kind switch {
                NodeKind.Mesh => new Color(0.4f, 0.75f, 1f),
                NodeKind.Light => new Color(1f, 0.88f, 0.3f),
                NodeKind.Camera => new Color(0.35f, 0.9f, 0.5f),
                NodeKind.Script => new Color(0.75f, 0.45f, 1f),
                _ => theme.Hint,
            };
            var kindLabel = kind.ToString().ToUpper();

            return new PropRow(
                new Column {
                    MainAxisAlign = MainAxisAlignment.Start,
                    CrossAxisAlign = CrossAxisAlignment.Start,
                    Children = {
                        // Kind badge
                        new Padding(
                            new EdgeInsets(
                                0f,
                                0f,
                                0f,
                                4f
                            ),
                            new Label(kindLabel, theme.FontSizeCaption - 1f, kindColor) {
                                FontWeight = FontWeight.Bold,
                            }
                        ),
                        // Editable name
                        new SizedBox(height: 28f, child: nameField),
                    },
                }
            );
        }

        /// <summary>Wrap a section-header widget, tagged so collapse can hide the section's rows.</summary>
        public static PropRow Section(Widget header, string title)
        {
            return new PropRow(header) {
                IsSectionHeader = true,
                SectionTitle = title,
            };
        }

        public static PropRow Text(string label, string value,
            Action<string> onChange, ThemeData theme, App app)
        {
            var tf = new TextField {
                Text = value,
                OnChanged = onChange,
            };
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child:
                                new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new SizedBox(height: 24f, child: tf),
                        },
                    }
                )
            );
        }

        /// <summary>Free-text field with a type-ahead suggestion popup (commits on pick/Enter).</summary>
        public static PropRow Suggest(string label, string value,
            Func<string, IReadOnlyList<(string Value, string Display)>> suggest,
            Action<string> onCommit, ThemeData theme, App app)
        {
            var f = new AutoSuggestField(
                app,
                value,
                suggest,
                onCommit
            ) { Height = 24f };
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new Expanded(new SizedBox(height: 24f, child: f)),
                        },
                    }
                )
            );
        }

        /// <summary>Label + a clickable colour swatch that opens a preset/RGB picker.</summary>
        public static PropRow ColorSwatch(string label, Vec3 current, Action<Vec3> setter,
            ThemeData theme, App app)
        {
            var sw = new ColorSwatchField(new Color(current.X, current.Y, current.Z), app) {
                OnChanged = c => setter(new Vec3(c.R, c.G, c.B)),
            };
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            sw,
                        },
                    }
                )
            );
        }

        /// <summary>Wrap an arbitrary widget as a property row.</summary>
        public static PropRow Custom(Widget inner)
        {
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    inner
                )
            );
        }

        public static PropRow Path(string label, string value,
            Action<string> onChange, string rootPath, string[] extensions, ThemeData theme, App app)
        {
            var tf = new TextField {
                Text = value,
                OnChanged = onChange,
            };
            var pickBtn = new SizedBox(
                24f,
                24f,
                new Button(
                    "...",
                    () =>
                    {
                        FilePickerDialog.Show(
                            app,
                            "Select " + label,
                            rootPath,
                            extensions,
                            selectedPath =>
                            {
                                tf.Text = selectedPath;
                                onChange(selectedPath);
                            }
                        );
                    }
                ) {
                    Padding = EdgeInsets.Zero,
                }
            );
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child:
                                new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new Expanded(new SizedBox(height: 24f, child: tf)),
                            new SizedBox(4f),
                            pickBtn,
                        },
                    }
                )
            );
        }

        public static PropRow DropdownRow(string label, string[] items, int selectedIndex,
            Action<int> onChange, ThemeData theme)
        {
            var dd = new StringDropdown(
                items,
                selectedIndex,
                s => s,
                (i, _) => onChange(i)
            );
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new SizedBox(height: 24f, width: 130f, child: dd),
                        },
                    }
                )
            );
        }

        public static PropRow Toggle(string label, bool value, Action<bool> onChange,
            ThemeData theme)
        {
            var cb = new Checkbox(value, onChange);
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            cb,
                        },
                    }
                )
            );
        }

        public static PropRow Float(string label, float value, Action<float> onChange,
            ThemeData theme,
            float min = 0f, float max = 1f, float step = 0.05f)
        {
            var ni = new NumberInput(
                value,
                step,
                min,
                max
            ) { Decimals = 2 };
            ni.OnChanged = onChange;
            ni.OnScrubStart = () => History?.BeginInteraction();
            ni.OnScrubEnd = () => History?.EndInteraction();
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new SizedBox(height: 26f, width: 110f, child: ni),
                        },
                    }
                )
            );
        }

        // ── NodeBind<T> overloads — route mutations through the command history ──

        public static PropRow Float(string label, NodeBind<float> bind, ThemeData theme,
            float min = 0f, float max = 1f, float step = 0.05f)
        {
            var ni = new NumberInput(
                bind.Value,
                step,
                min,
                max
            ) { Decimals = 2 };
            ni.OnChanged = bind.Set;
            ni.OnScrubStart = bind.BeginEdit;
            ni.OnScrubEnd = bind.EndEdit;
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            new SizedBox(height: 26f, width: 110f, child: ni),
                        },
                    }
                )
            );
        }

        public static PropRow Toggle(string label, NodeBind<bool> bind, ThemeData theme)
        {
            var cb = new Checkbox(bind.Value, bind.Set);
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            cb,
                        },
                    }
                )
            );
        }

        public static PropRow ColorSwatch(string label, NodeBind<Vec3> bind, ThemeData theme,
            App app)
        {
            var v = bind.Value;
            var sw = new ColorSwatchField(new Color(v.X, v.Y, v.Z), app) {
                OnChanged = c => bind.Set(new Vec3(c.R, c.G, c.B)),
            };
            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        4f
                    ),
                    new Row {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Center,
                        Children = {
                            new SizedBox(
                                76f,
                                child: new Label(label, theme.FontSizeCaption, theme.Hint)
                            ),
                            new SizedBox(4f),
                            sw,
                        },
                    }
                )
            );
        }

        public static PropRow Vec3(string label, NodeBind<Vec3> bind, ThemeData theme)
        {
            var current = bind.Value;
            var tfX = MiniFloat(current.X.ToString("F2"), theme);
            var tfY = MiniFloat(current.Y.ToString("F2"), theme);
            var tfZ = MiniFloat(current.Z.ToString("F2"), theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var v
                )
                    ? v
                    : 0f;
            }

            tfX.OnChanged = s => bind.Set(new Vec3(Parse(s), Parse(tfY.Text), Parse(tfZ.Text)));
            tfY.OnChanged = s => bind.Set(new Vec3(Parse(tfX.Text), Parse(s), Parse(tfZ.Text)));
            tfZ.OnChanged = s => bind.Set(new Vec3(Parse(tfX.Text), Parse(tfY.Text), Parse(s)));

            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        6f
                    ),
                    new Column {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Start,
                        Children = {
                            new Label(label, theme.FontSizeCaption, theme.Hint),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlign = MainAxisAlignment.Start,
                                CrossAxisAlign = CrossAxisAlignment.Center,
                                Children = {
                                    new Label("X", theme.FontSizeCaption, theme.Accent),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfX),
                                    new SizedBox(8f),
                                    new Label("Y", theme.FontSizeCaption, theme.Success),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfY),
                                    new SizedBox(8f),
                                    new Label("Z", theme.FontSizeCaption, theme.Primary),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfZ),
                                },
                            },
                        },
                    }
                )
            );
        }

        public static PropRow Vec3Color(string label, NodeBind<Vec3> bind, ThemeData theme)
        {
            var current = bind.Value;
            var tfR = MiniFloat(current.X.ToString("F2"), theme);
            var tfG = MiniFloat(current.Y.ToString("F2"), theme);
            var tfB = MiniFloat(current.Z.ToString("F2"), theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var v
                )
                    ? v
                    : 0f;
            }

            tfR.OnChanged = s => bind.Set(new Vec3(Parse(s), Parse(tfG.Text), Parse(tfB.Text)));
            tfG.OnChanged = s => bind.Set(new Vec3(Parse(tfR.Text), Parse(s), Parse(tfB.Text)));
            tfB.OnChanged = s => bind.Set(new Vec3(Parse(tfR.Text), Parse(tfG.Text), Parse(s)));

            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        6f
                    ),
                    new Column {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Start,
                        Children = {
                            new Label(label, theme.FontSizeCaption, theme.Hint),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlign = MainAxisAlignment.Start,
                                CrossAxisAlign = CrossAxisAlignment.Center,
                                Children = {
                                    new Label(
                                        "R",
                                        theme.FontSizeCaption,
                                        new Color(0.9f, 0.35f, 0.35f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfR),
                                    new SizedBox(6f),
                                    new Label(
                                        "G",
                                        theme.FontSizeCaption,
                                        new Color(0.3f, 0.85f, 0.3f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfG),
                                    new SizedBox(6f),
                                    new Label(
                                        "B",
                                        theme.FontSizeCaption,
                                        new Color(0.3f, 0.55f, 1.0f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfB),
                                },
                            },
                        },
                    }
                )
            );
        }

        public static PropRow Vec3Color(string label, Vec3 current, Action<Vec3> setter,
            ThemeData theme)
        {
            var tfR = MiniFloat(current.X.ToString("F2"), theme);
            var tfG = MiniFloat(current.Y.ToString("F2"), theme);
            var tfB = MiniFloat(current.Z.ToString("F2"), theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var v
                )
                    ? v
                    : 0f;
            }

            tfR.OnChanged = s => setter(new Vec3(Parse(s), Parse(tfG.Text), Parse(tfB.Text)));
            tfG.OnChanged = s => setter(new Vec3(Parse(tfR.Text), Parse(s), Parse(tfB.Text)));
            tfB.OnChanged = s => setter(new Vec3(Parse(tfR.Text), Parse(tfG.Text), Parse(s)));

            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        6f
                    ),
                    new Column {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Start,
                        Children = {
                            new Label(label, theme.FontSizeCaption, theme.Hint),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlign = MainAxisAlignment.Start,
                                CrossAxisAlign = CrossAxisAlignment.Center,
                                Children = {
                                    new Label(
                                        "R",
                                        theme.FontSizeCaption,
                                        new Color(0.9f, 0.35f, 0.35f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfR),
                                    new SizedBox(6f),
                                    new Label(
                                        "G",
                                        theme.FontSizeCaption,
                                        new Color(0.3f, 0.85f, 0.3f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfG),
                                    new SizedBox(6f),
                                    new Label(
                                        "B",
                                        theme.FontSizeCaption,
                                        new Color(0.3f, 0.55f, 1.0f)
                                    ),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 56f, child: tfB),
                                },
                            },
                        },
                    }
                )
            );
        }

        public static PropRow Vec3(string label, Vec3 current, Action<Vec3> setter, ThemeData theme)
        {
            var tfX = MiniFloat(current.X.ToString("F2"), theme);
            var tfY = MiniFloat(current.Y.ToString("F2"), theme);
            var tfZ = MiniFloat(current.Z.ToString("F2"), theme);

            static float Parse(string s)
            {
                return float.TryParse(
                    s,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var v
                )
                    ? v
                    : 0f;
            }

            tfX.OnChanged = s => setter(new Vec3(Parse(s), Parse(tfY.Text), Parse(tfZ.Text)));
            tfY.OnChanged = s => setter(new Vec3(Parse(tfX.Text), Parse(s), Parse(tfZ.Text)));
            tfZ.OnChanged = s => setter(new Vec3(Parse(tfX.Text), Parse(tfY.Text), Parse(s)));

            return new PropRow(
                new Padding(
                    new EdgeInsets(
                        0f,
                        0f,
                        0f,
                        6f
                    ),
                    new Column {
                        MainAxisAlign = MainAxisAlignment.Start,
                        CrossAxisAlign = CrossAxisAlignment.Start,
                        Children = {
                            new Label(label, theme.FontSizeCaption, theme.Hint),
                            new SizedBox(height: 3f),
                            new Row {
                                MainAxisAlign = MainAxisAlignment.Start,
                                CrossAxisAlign = CrossAxisAlignment.Center,
                                Children = {
                                    new Label("X", theme.FontSizeCaption, theme.Accent),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfX),
                                    new SizedBox(8f),
                                    new Label("Y", theme.FontSizeCaption, theme.Success),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfY),
                                    new SizedBox(8f),
                                    new Label("Z", theme.FontSizeCaption, theme.Primary),
                                    new SizedBox(2f),
                                    new SizedBox(height: 22f, width: 64f, child: tfZ),
                                },
                            },
                        },
                    }
                )
            );
        }

        private static TextField MiniFloat(string val, ThemeData theme)
        {
            return new TextField {
                Text = val,
                MinWidth = 64f,
                Height = 22f,
            };
        }

        public override Size Measure(Constraints c)
        {
            _size = _inner.Measure(c);
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
            _inner.Layout(origin);
        }

        public override void Paint(PaintList paint)
        {
            _inner.Paint(paint);
        }

        public override Widget? HitTest(Offset point)
        {
            if (!Bounds.Contains(point.X, point.Y)) return null;
            return _inner.HitTest(point);
        }
    }

    // ── Section header widget ─────────────────────────────────────────────────

    /// <summary>
    ///     A collapsible section header: a disclosure chevron, a title and a full-width hairline.
    ///     Clicking anywhere toggles the section (the panel hides the rows beneath a collapsed header).
    /// </summary>
    private sealed class SectionHeader : Widget
    {
        private readonly bool _collapsed;
        private readonly Action _onToggle;
        private readonly ThemeData _theme;
        private readonly string _title;
        private bool _hovered;
        private Size _size;

        public SectionHeader(string title, ThemeData theme, bool collapsed, Action onToggle)
        {
            _title = title;
            _theme = theme;
            _collapsed = collapsed;
            _onToggle = onToggle;
        }

        public override Size Measure(Constraints c)
        {
            _size = c.Constrain(new Size(c.MaxWidth, 26f));
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

        public override void Paint(PaintList paint)
        {
            if (_hovered)
                paint.AddRect(
                    new Rect(
                        Bounds.X,
                        Bounds.Y,
                        Bounds.Width,
                        Bounds.Height - 1f
                    ),
                    _theme.ControlHover,
                    4f
                );

            // Disclosure chevron — ▾ expanded, ▸ collapsed.
            const float cs = 14f;
            var chevron = _collapsed ? Icons.ChevronRight : Icons.ChevronDown;
            Icons.Draw(
                paint,
                chevron,
                new Rect(
                    Bounds.X,
                    Bounds.Y,
                    cs,
                    Bounds.Height
                ),
                _theme.TextSecondary,
                cs
            );

            var fs = _theme.FontSizeCaption;
            var ty = Bounds.Y + (Bounds.Height - fs) / 2f + fs * 0.8f;
            paint.AddText(
                _title,
                Bounds.X + cs + 2f,
                ty,
                _theme.OnSurface,
                fs,
                fontWeight: FontWeight.SemiBold
            );

            // Full-width hairline closing the header band off from the rows below.
            paint.AddRect(
                new Rect(
                    Bounds.X,
                    Bounds.Bottom - 1f,
                    Bounds.Width,
                    1f
                ),
                _theme.Separator
            );
        }

        public override void OnPointerEnter()
        {
            _hovered = true;
            MarkNeedsPaint();
        }

        public override void OnPointerExit()
        {
            _hovered = false;
            MarkNeedsPaint();
        }

        public override void OnPointerUp(Offset point)
        {
            if (Bounds.Contains(point.X, point.Y)) _onToggle();
        }
    }
}