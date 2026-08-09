using Zigote.Core;
using Zigote.Core.Assets;
using Zigote.Core.Engine;
using Zigote.Core.Math3D;
using Zigote.Core.Paint;
using Zigote.Core.Physics;
using Zigote.Editor.History;
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
// CodeEditor: Adwaita has no source view (nor does libadwaita — GtkSourceView is its own
// library), so this one Material widget stays. Everything else in the panel is Adwaita.
using Zigote.UI.Material;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
// Dropdown<T> must be referenced with a concrete type — alias for clarity:

namespace Zigote.Editor.Panels;

/// <summary>
///     Inspector panel: shows an editable name header, transform, and per-kind properties
///     for the selected SceneNode. Section headers use a colored accent bar.
/// </summary>
public sealed partial class InspectorPanel : Widget
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
        var previous = _content;
        _content = content;
        SwapChild(previous, _content); // attach-then-detach; see Widget.SwapChild
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
        var stack = new AdwViewStack {
            Pages = {
                new AdwViewStackPage("nodes", "Nodes", panel),
                new AdwViewStackPage("wgsl", "Generated WGSL", wgslView),
            },
        };
        var root = new AdwToolbarView(stack) {
            TopBars = { new AdwHeaderBar { TitleWidget = new AdwViewSwitcher(stack) } },
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
        var nameField = new AdwEntry { Text = _shown.Name, Compact = true };
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
        else if (_shown.Kind == NodeKind.Tilemap)
        {
            BuildTilemapRows(capturedNode);
        }

        // A 2D collider belongs to anything that can sit in the 2D world, not to one node kind.
        if (_shown.Kind is NodeKind.Sprite or NodeKind.Tilemap or NodeKind.Empty)
            BuildCollider2DRows(capturedNode);

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
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Start,
        };
        col.Children.AddRange(visible);
        SetContent(col);
        RequestLayout();
    }

    private Widget BuildWithMultiSelectBanner(int count)
    {
        // Show a compact summary listing all selected node names.
        var col = new Column {
            MainAxisAlignment = MainAxisAlignment.Start,
            CrossAxisAlignment = CrossAxisAlignment.Start,
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
}