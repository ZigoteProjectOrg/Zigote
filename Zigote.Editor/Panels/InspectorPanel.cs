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
using Zigote.UI.Host;
using Zigote.UI.Material;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
// CodeEditor: Adwaita has no source view (nor does libadwaita — GtkSourceView is its own
// library), so this one Material widget stays. Everything else in the panel is Adwaita.

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
        padding: EdgeInsets.All(12f),
        child: new Label(
            text: "Nothing selected",
            fontSize: _theme.FontSizeBody,
            color: _theme.Hint
        )
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
        if (ReferenceEquals(objA: _content, objB: content)) return;
        var previous = _content;
        _content = content;
        SwapChild(previous: previous, next: _content); // attach-then-detach; see Widget.SwapChild
    }

    public override IEnumerable<Widget> GetChildren() => [_content];

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
            graph: graph,
            registry: registry,
            theme: _theme,
            app: _app,
            inspectorHeader: preview,
            onCompiled: artifact =>
            {
                if (artifact is not CompiledShaderGraph cg) return;
                preview.Compiled =
                    cg; // live, per-pixel material-ball preview reflects the compiled graph
                wgslView.Text = cg.Wgsl; // the "Generated WGSL" tab tracks the graph
                ShaderMaterialDomain.ApplyTo(mat: ShaderMaterialDomain.ToMaterial(cg), node: node);
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
                new AdwViewStackPage(name: "nodes", title: "Nodes", child: panel),
                new AdwViewStackPage(name: "wgsl", title: "Generated WGSL", child: wgslView),
            },
        };
        var root = new AdwToolbarView(stack) {
            TopBars = { new AdwHeaderBar { TitleWidget = new AdwViewSwitcher(stack) } },
        };
        new Dialog(content: root, app: _app) {
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
        var preview = new VfxPreviewWidget(theme: _theme, height: 200f);

        var panel = new GraphEditorPanel(
            graph: graph,
            registry: registry,
            theme: _theme,
            app: _app,
            inspectorHeader: preview,
            onCompiled: artifact =>
            {
                if (artifact is not CompiledVfxGraph cvfx) return;
                preview.Asset = cvfx.Asset; // live CPU-sim preview reflects the compiled graph
                VfxNodeEditor.SaveGraph(
                    node: node,
                    graph: graph
                ); // persist edits back onto the node
                _state.NotifySceneChanged();
            }
        );

        preview.Asset = VfxGraphCompiler.Compile(graph).Asset; // seed before the first edit
        new Dialog(content: panel, app: _app) {
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
            string path = Path.IsPathRooted(node.AudioClipPath)
                ? node.AudioClipPath
                : Path.GetFullPath(node.AudioClipPath);
            if (!File.Exists(path)) return;
            id = engine.AudioSoundCreateFile(path: path, streaming: node.AudioStreaming);
        }
        else
        {
            id = engine.AudioSoundCreateTone(
                frequencyHz: node.AudioFrequency,
                waveform: Math.Clamp(value: node.AudioWaveform, min: 0, max: 4)
            );
        }

        if (id == 0) return;
        engine.AudioSoundSetSpatial(id: id, enabled: false);
        engine.AudioSoundSetVolume(id: id, volume: node.AudioVolume);
        engine.AudioSoundSetPitch(id: id, pitch: node.AudioPitch);
        engine.AudioSoundSetLooping(id: id, looping: node.AudioLoop);
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
        var nameField = new AdwEntry {
            Text = _shown.Name,
            Compact = true,
        };
        var capturedNode = _shown;
        nameField.OnChanged = name =>
        {
            string trimmed = name.Trim();
            if (trimmed.Length > 0 && trimmed != capturedNode.Name)
            {
                _state.History.Execute(
                    new ChangePropertyCommand<string>(
                        state: _state,
                        oldValue: capturedNode.Name,
                        newValue: trimmed,
                        setter: v => capturedNode.Name = v
                    )
                );
            }
        };

        _rows.Add(PropRow.NodeHeader(nameField: nameField, kind: _shown.Kind, theme: _theme));
        _rows.Add(PropRow.Spacer(6f));

        // Gameplay tag — queried at play time via the World scripting API (FindAllByTag/OverlapSphere).
        _rows.Add(
            PropRow.Text(
                label: "Tag",
                value: capturedNode.Tag ?? "",
                onChange: v =>
                {
                    string tag = v.Trim();
                    string? newTag = tag.Length == 0 ? null : tag;
                    if (newTag != capturedNode.Tag)
                    {
                        _state.History.Execute(
                            new ChangePropertyCommand<string?>(
                                state: _state,
                                oldValue: capturedNode.Tag,
                                newValue: newTag,
                                setter: val => capturedNode.Tag = val
                            )
                        );
                    }
                },
                theme: _theme,
                app: _app
            )
        );

        if (capturedNode.IsPrefabInstance) BuildPrefabBanner(capturedNode);

        // ── Transform ─────────────────────────────────────────────────────────
        _rows.Add(SectionRow(title: "Transform", theme: _theme));
        _rows.Add(
            PropRow.Vec3(
                label: "Position",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.Position,
                    setter: (n, v) => n.Position = v
                ),
                theme: _theme
            )
        );
        var eulerRad = _shown.Rotation.ToEulerRadians();
        var eulerDeg = new Vec3(
            x: eulerRad.X * (180f / MathF.PI),
            y: eulerRad.Y * (180f / MathF.PI),
            z: eulerRad.Z * (180f / MathF.PI)
        );
        _rows.Add(
            PropRow.Vec3(
                label: "Rotation (deg)",
                current: eulerDeg,
                setter: v =>
                {
                    var newRot = Quat.FromEuler(
                        pitch: v.X * (MathF.PI / 180f),
                        yaw: v.Y * (MathF.PI / 180f),
                        roll: v.Z * (MathF.PI / 180f)
                    );
                    _state.History.Execute(
                        new ChangePropertyCommand<Quat>(
                            state: _state,
                            oldValue: capturedNode.Rotation,
                            newValue: newRot,
                            setter: val => capturedNode.Rotation = val
                        )
                    );
                },
                theme: _theme
            )
        );
        _rows.Add(
            PropRow.Vec3(
                label: "Scale",
                bind: NodeBind.To(
                    state: _state,
                    node: capturedNode,
                    getter: n => n.Scale,
                    setter: (n, v) => n.Scale = v
                ),
                theme: _theme
            )
        );

        // ── Kind-specific properties ──────────────────────────────────────────
        if (_shown.Kind == NodeKind.Mesh)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow(title: "Mesh", theme: _theme));
            _rows.Add(
                PropRow.Path(
                    label: "Mesh Path",
                    value: _shown.MeshPath ?? "",
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            state: _state,
                            oldValue: capturedNode.MeshPath,
                            newValue: v,
                            setter: val => capturedNode.MeshPath = val
                        )
                    ),
                    rootPath: _state.AssetRoot,
                    extensions: [".glb", ".fbx", ".obj"],
                    theme: _theme,
                    app: _app
                )
            );
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow(title: "Material", theme: _theme));
            // One-click finish presets (Car Paint / Chrome / Glass / …) + apply-to-all-sub-meshes.
            _rows.Add(PropRow.Custom(BuildPresetRow()));
            _rows.Add(
                PropRow.Toggle(
                    label: "All sub-meshes",
                    value: _applyToSubMeshes,
                    onChange: v => _applyToSubMeshes = v,
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.ColorSwatch(
                    label: "Color",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.MeshColor,
                        setter: (n, v) => n.MeshColor = v
                    ),
                    theme: _theme,
                    app: _app
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Metallic",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.MeshMetallic,
                        setter: (n, v) => n.MeshMetallic = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Roughness",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.MeshRoughness,
                        setter: (n, v) => n.MeshRoughness = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Clearcoat",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.MeshClearcoat,
                        setter: (n, v) => n.MeshClearcoat = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Coat Rough",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.MeshClearcoatRoughness,
                        setter: (n, v) => n.MeshClearcoatRoughness = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Specular",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.MeshSpecular,
                        setter: (n, v) => n.MeshSpecular = v
                    ),
                    theme: _theme,
                    min: 0f,
                    max: 2f
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "IOR",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.MeshIor,
                        setter: (n, v) => n.MeshIor = v
                    ),
                    theme: _theme,
                    min: 1f,
                    max: 3f
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Transmission",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.MeshTransmission,
                        setter: (n, v) => n.MeshTransmission = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    label: "Double-Sided",
                    value: _shown.MeshDoubleSided,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<bool>(
                            state: _state,
                            oldValue: capturedNode.MeshDoubleSided,
                            newValue: v,
                            setter: val => capturedNode.MeshDoubleSided = val
                        )
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Vec3Color(
                    label: "Emissive",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.MeshEmissive,
                        setter: (n, v) => n.MeshEmissive = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.DropdownRow(
                    label: "Alpha",
                    items: [
                        "Opaque", "Mask", "Blend", "Glass",
                    ], // 3 = glass (refractive + reflective), set by the glTF importer
                    selectedIndex: (int)_shown.MeshAlphaMode,
                    onChange: i => _state.History.Execute(
                        new ChangePropertyCommand<uint>(
                            state: _state,
                            oldValue: capturedNode.MeshAlphaMode,
                            newValue: (uint)i,
                            setter: val => capturedNode.MeshAlphaMode = val
                        )
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Alpha Cutoff",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.MeshAlphaCutoff,
                        setter: (n, v) => n.MeshAlphaCutoff = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Path(
                    label: "Texture Path",
                    value: _shown.TexturePath ?? "",
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            state: _state,
                            oldValue: capturedNode.TexturePath,
                            newValue: v,
                            setter: val => capturedNode.TexturePath = val
                        )
                    ),
                    rootPath: _state.AssetRoot,
                    extensions: [".png", ".jpg", ".jpeg", ".webp", ".gif"],
                    theme: _theme,
                    app: _app
                )
            );
            _rows.Add(
                PropRow.Path(
                    label: "Normal Map",
                    value: _shown.NormalTexturePath ?? "",
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            state: _state,
                            oldValue: capturedNode.NormalTexturePath,
                            newValue: v,
                            setter: val => capturedNode.NormalTexturePath = val
                        )
                    ),
                    rootPath: _state.AssetRoot,
                    extensions: [".png", ".jpg", ".jpeg", ".webp"],
                    theme: _theme,
                    app: _app
                )
            );
            _rows.Add(
                PropRow.Path(
                    label: "Emissive Map",
                    value: _shown.EmissiveTexturePath ?? "",
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            state: _state,
                            oldValue: capturedNode.EmissiveTexturePath,
                            newValue: v,
                            setter: val => capturedNode.EmissiveTexturePath = val
                        )
                    ),
                    rootPath: _state.AssetRoot,
                    extensions: [".png", ".jpg", ".jpeg", ".webp"],
                    theme: _theme,
                    app: _app
                )
            );
            _rows.Add(
                PropRow.DropdownRow(
                    label: "Effect",
                    items: ["Standard", "CrtTv", "Unlit"],
                    selectedIndex: (int)_shown.MeshEffect,
                    onChange: i => _state.History.Execute(
                        new ChangePropertyCommand<RenderEffect>(
                            state: _state,
                            oldValue: capturedNode.MeshEffect,
                            newValue: (RenderEffect)i,
                            setter: val => { capturedNode.MeshEffect = val; }
                        )
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.ActionButton(
                    label: "Edit as Nodes…",
                    onClick: () => OpenShaderEditor(capturedNode)
                )
            );
        }
        else if (_shown.Kind == NodeKind.Light)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow(title: "Light", theme: _theme));
            _rows.Add(
                PropRow.DropdownRow(
                    label: "Type",
                    items: ["Directional", "Point", "Spot"],
                    selectedIndex: (int)_shown.LightKind,
                    onChange: i => _state.History.Execute(
                        new ChangePropertyCommand<LightType>(
                            state: _state,
                            oldValue: capturedNode.LightKind,
                            newValue: (LightType)i,
                            setter: val =>
                            {
                                capturedNode.LightKind = val;
                                Rebuild();
                            }
                        )
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.ColorSwatch(
                    label: "Color",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.LightColor,
                        setter: (n, v) => n.LightColor = v
                    ),
                    theme: _theme,
                    app: _app
                )
            );
            _rows.Add(
                PropRow.DropdownRow(
                    label: "Preset",
                    items: LightPresetNames,
                    selectedIndex: NearestLightPreset(_shown.LightTemperature),
                    onChange: i => _state.History.Execute(
                        new ChangePropertyCommand<float>(
                            state: _state,
                            oldValue: capturedNode.LightTemperature,
                            newValue: LightPresetKelvin[i],
                            setter: val =>
                            {
                                capturedNode.LightTemperature = val;
                                Rebuild();
                            }
                        )
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Temp (K)",
                    value: _shown.LightTemperature,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<float>(
                            state: _state,
                            oldValue: capturedNode.LightTemperature,
                            newValue: v,
                            setter: val => capturedNode.LightTemperature = val
                        )
                    ),
                    theme: _theme,
                    min: 1500f,
                    max: 12000f,
                    step: 100f
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Intensity",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.LightIntensity,
                        setter: (n, v) => n.LightIntensity = v
                    ),
                    theme: _theme,
                    min: 0f,
                    max: 20f,
                    step: 0.1f
                )
            );
            if (_shown.LightKind != LightType.Directional)
            {
                _rows.Add(
                    PropRow.Float(
                        label: "Range",
                        bind: NodeBind.To(
                            state: _state,
                            node: capturedNode,
                            getter: n => n.LightRange,
                            setter: (n, v) => n.LightRange = v
                        ),
                        theme: _theme,
                        min: 0f,
                        max: 200f,
                        step: 1f
                    )
                );
            }

            if (_shown.LightKind == LightType.Spot)
            {
                _rows.Add(
                    PropRow.Float(
                        label: "Inner°",
                        value: _shown.SpotInnerAngleDeg,
                        onChange: v => _state.History.Execute(
                            new ChangePropertyCommand<float>(
                                state: _state,
                                oldValue: capturedNode.SpotInnerAngleDeg,
                                newValue: v,
                                setter: val => capturedNode.SpotInnerAngleDeg = MathF.Min(
                                    x: val,
                                    y: capturedNode.SpotOuterAngleDeg
                                )
                            )
                        ),
                        theme: _theme,
                        min: 1f,
                        max: 88f,
                        step: 1f
                    )
                );
                _rows.Add(
                    PropRow.Float(
                        label: "Outer°",
                        value: _shown.SpotOuterAngleDeg,
                        onChange: v => _state.History.Execute(
                            new ChangePropertyCommand<float>(
                                state: _state,
                                oldValue: capturedNode.SpotOuterAngleDeg,
                                newValue: v,
                                setter: val => capturedNode.SpotOuterAngleDeg = MathF.Max(
                                    x: val,
                                    y: capturedNode.SpotInnerAngleDeg
                                )
                            )
                        ),
                        theme: _theme,
                        min: 1f,
                        max: 89f,
                        step: 1f
                    )
                );
            }

            _rows.Add(
                PropRow.Toggle(
                    label: "Cast Shadows",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.LightCastShadows,
                        setter: (n, v) => n.LightCastShadows = v
                    ),
                    theme: _theme
                )
            );
        }
        else if (_shown.Kind == NodeKind.Camera)
            BuildCameraSection(capturedNode);
        else if (_shown.Kind == NodeKind.ReflectionProbe)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow(title: "Reflection Probe", theme: _theme));
            _rows.Add(
                PropRow.Vec3(
                    label: "Box Extents",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.ProbeExtents,
                        setter: (n, v) => n.ProbeExtents = v
                    ),
                    theme: _theme
                )
            );
        }
        else if (_shown.Kind == NodeKind.AudioSource)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow(title: "Audio Source", theme: _theme));
            _rows.Add(
                PropRow.Toggle(
                    label: "Use File",
                    value: _shown.AudioUseFile,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<bool>(
                            state: _state,
                            oldValue: capturedNode.AudioUseFile,
                            newValue: v,
                            setter: val =>
                            {
                                capturedNode.AudioUseFile = val;
                                Rebuild();
                            }
                        )
                    ),
                    theme: _theme
                )
            );
            if (_shown.AudioUseFile)
            {
                _rows.Add(
                    PropRow.Path(
                        label: "Clip",
                        value: _shown.AudioClipPath ?? "",
                        onChange: v => _state.History.Execute(
                            new ChangePropertyCommand<string?>(
                                state: _state,
                                oldValue: capturedNode.AudioClipPath,
                                newValue: v,
                                setter: val => capturedNode.AudioClipPath = val
                            )
                        ),
                        rootPath: _state.AssetRoot,
                        extensions: [".wav", ".ogg", ".mp3", ".flac"],
                        theme: _theme,
                        app: _app
                    )
                );
                _rows.Add(
                    PropRow.Toggle(
                        label: "Stream",
                        bind: NodeBind.To(
                            state: _state,
                            node: capturedNode,
                            getter: n => n.AudioStreaming,
                            setter: (n, v) => n.AudioStreaming = v
                        ),
                        theme: _theme
                    )
                );
            }
            else
            {
                _rows.Add(
                    PropRow.DropdownRow(
                        label: "Waveform",
                        items: ["Sine", "Square", "Triangle", "Sawtooth", "Noise"],
                        selectedIndex: Math.Clamp(value: _shown.AudioWaveform, min: 0, max: 4),
                        onChange: i => _state.History.Execute(
                            new ChangePropertyCommand<int>(
                                state: _state,
                                oldValue: capturedNode.AudioWaveform,
                                newValue: i,
                                setter: val => capturedNode.AudioWaveform = val
                            )
                        ),
                        theme: _theme
                    )
                );
                _rows.Add(
                    PropRow.Float(
                        label: "Frequency",
                        bind: NodeBind.To(
                            state: _state,
                            node: capturedNode,
                            getter: n => n.AudioFrequency,
                            setter: (n, v) => n.AudioFrequency = v
                        ),
                        theme: _theme,
                        min: 20f,
                        max: 4000f,
                        step: 10f
                    )
                );
            }

            _rows.Add(
                PropRow.Float(
                    label: "Volume",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.AudioVolume,
                        setter: (n, v) => n.AudioVolume = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Pitch",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.AudioPitch,
                        setter: (n, v) => n.AudioPitch = v
                    ),
                    theme: _theme,
                    min: 0.25f,
                    max: 4f
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    label: "Loop",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.AudioLoop,
                        setter: (n, v) => n.AudioLoop = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    label: "Auto Play",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.AudioAutoPlay,
                        setter: (n, v) => n.AudioAutoPlay = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    label: "Spatial (3D)",
                    value: _shown.AudioSpatial,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<bool>(
                            state: _state,
                            oldValue: capturedNode.AudioSpatial,
                            newValue: v,
                            setter: val =>
                            {
                                capturedNode.AudioSpatial = val;
                                Rebuild();
                            }
                        )
                    ),
                    theme: _theme
                )
            );
            if (_shown.AudioSpatial)
            {
                _rows.Add(
                    PropRow.Float(
                        label: "Min Dist",
                        bind: NodeBind.To(
                            state: _state,
                            node: capturedNode,
                            getter: n => n.AudioMinDistance,
                            setter: (n, v) => n.AudioMinDistance = v
                        ),
                        theme: _theme,
                        min: 0.1f,
                        max: 100f,
                        step: 0.5f
                    )
                );
                _rows.Add(
                    PropRow.Float(
                        label: "Max Dist",
                        bind: NodeBind.To(
                            state: _state,
                            node: capturedNode,
                            getter: n => n.AudioMaxDistance,
                            setter: (n, v) => n.AudioMaxDistance = v
                        ),
                        theme: _theme,
                        min: 1f,
                        max: 1000f,
                        step: 1f
                    )
                );
                _rows.Add(
                    PropRow.Float(
                        label: "Rolloff",
                        bind: NodeBind.To(
                            state: _state,
                            node: capturedNode,
                            getter: n => n.AudioRolloff,
                            setter: (n, v) => n.AudioRolloff = v
                        ),
                        theme: _theme,
                        min: 0f,
                        max: 4f,
                        step: 0.1f
                    )
                );
            }

            _rows.Add(
                PropRow.ActionButton(
                    label: _previewPlaying ? "Stop Preview" : "Preview",
                    onClick: () => TogglePreview(capturedNode)
                )
            );
        }
        else if (_shown.Kind == NodeKind.VfxEmitter)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow(title: "VFX Emitter", theme: _theme));

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
                    label: "Preset",
                    items: VfxPresets.Names.ToArray(),
                    selectedIndex: 0,
                    onChange: i =>
                    {
                        string json = VfxGraphSerializer.Serialize(
                            VfxPresets.Create(preset: VfxPresets.Names[i], name: capturedNode.Name)
                        );
                        _state.History.Execute(
                            new ChangePropertyCommand<string?>(
                                state: _state,
                                oldValue: capturedNode.VfxGraphJson,
                                newValue: json,
                                setter: val => capturedNode.VfxGraphJson = val
                            )
                        );
                        Rebuild();
                    },
                    theme: _theme
                )
            );

            _rows.Add(
                PropRow.Toggle(
                    label: "Play On Start",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.VfxPlayOnStart,
                        setter: (n, v) => n.VfxPlayOnStart = v
                    ),
                    theme: _theme
                )
            );

            _rows.Add(
                PropRow.ActionButton(
                    label: "Edit as Nodes…",
                    onClick: () => OpenVfxEditor(capturedNode)
                )
            );
        }
        else if (_shown.Kind == NodeKind.Sprite)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow(title: "Sprite", theme: _theme));
            _rows.Add(
                PropRow.Path(
                    label: "Texture",
                    value: _shown.TexturePath ?? "",
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            state: _state,
                            oldValue: capturedNode.TexturePath,
                            newValue: v,
                            setter: val => capturedNode.TexturePath = val
                        )
                    ),
                    rootPath: _state.AssetRoot,
                    extensions: [".png", ".jpg", ".jpeg", ".webp", ".gif"],
                    theme: _theme,
                    app: _app
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Pixels/Unit",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.SpritePixelsPerUnit,
                        setter: (n, v) => n.SpritePixelsPerUnit = MathF.Max(x: 0.001f, y: v)
                    ),
                    theme: _theme,
                    min: 1f,
                    max: 1024f,
                    step: 1f
                )
            );
            _rows.Add(
                PropRow.Vec3Color(
                    label: "Tint",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => new Vec3(
                            x: n.SpriteColor.X,
                            y: n.SpriteColor.Y,
                            z: n.SpriteColor.Z
                        ),
                        setter: (n, v) => n.SpriteColor = new Vec4(
                            x: v.X,
                            y: v.Y,
                            z: v.Z,
                            w: n.SpriteColor.W
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
                        node: capturedNode,
                        getter: n => n.SpriteColor.W,
                        setter: (n, v) => n.SpriteColor = new Vec4(
                            x: n.SpriteColor.X,
                            y: n.SpriteColor.Y,
                            z: n.SpriteColor.Z,
                            w: Math.Clamp(value: v, min: 0f, max: 1f)
                        )
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    label: "Flip X",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.SpriteFlipX,
                        setter: (n, v) => n.SpriteFlipX = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Toggle(
                    label: "Flip Y",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.SpriteFlipY,
                        setter: (n, v) => n.SpriteFlipY = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Pivot X",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.SpritePivotX,
                        setter: (n, v) => n.SpritePivotX = v
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Pivot Y",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.SpritePivotY,
                        setter: (n, v) => n.SpritePivotY = v
                    ),
                    theme: _theme
                )
            );

            _rows.Add(SectionRow(title: "Sprite Sheet", theme: _theme));
            _rows.Add(
                PropRow.Float(
                    label: "Columns",
                    value: capturedNode.SpriteCols,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            state: _state,
                            oldValue: capturedNode.SpriteCols,
                            newValue: Math.Max(val1: 1, val2: (int)v),
                            setter: val => capturedNode.SpriteCols = val
                        )
                    ),
                    theme: _theme,
                    min: 1f,
                    max: 64f,
                    step: 1f
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Rows",
                    value: capturedNode.SpriteRows,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            state: _state,
                            oldValue: capturedNode.SpriteRows,
                            newValue: Math.Max(val1: 1, val2: (int)v),
                            setter: val => capturedNode.SpriteRows = val
                        )
                    ),
                    theme: _theme,
                    min: 1f,
                    max: 64f,
                    step: 1f
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Frame",
                    value: capturedNode.SpriteFrame,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            state: _state,
                            oldValue: capturedNode.SpriteFrame,
                            newValue: Math.Max(val1: 0, val2: (int)v),
                            setter: val => capturedNode.SpriteFrame = val
                        )
                    ),
                    theme: _theme,
                    min: 0f,
                    max: 4095f,
                    step: 1f
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "FPS",
                    bind: NodeBind.To(
                        state: _state,
                        node: capturedNode,
                        getter: n => n.SpriteFps,
                        setter: (n, v) => n.SpriteFps = MathF.Max(x: 0f, y: v)
                    ),
                    theme: _theme,
                    min: 0f,
                    max: 60f,
                    step: 1f
                )
            );

            _rows.Add(SectionRow(title: "Material", theme: _theme));
            _rows.Add(
                PropRow.DropdownRow(
                    label: "Blend",
                    items: ["Alpha", "Additive", "Opaque"],
                    selectedIndex: Math.Clamp(value: _shown.SpriteBlend, min: 0, max: 2),
                    onChange: i => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            state: _state,
                            oldValue: capturedNode.SpriteBlend,
                            newValue: i,
                            setter: val => capturedNode.SpriteBlend = val
                        )
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.DropdownRow(
                    label: "Stage",
                    items: ["Scene (HDR)", "Overlay (exact)"],
                    selectedIndex: Math.Clamp(value: _shown.SpriteStage, min: 0, max: 1),
                    onChange: i => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            state: _state,
                            oldValue: capturedNode.SpriteStage,
                            newValue: i,
                            setter: val => capturedNode.SpriteStage = val
                        )
                    ),
                    theme: _theme
                )
            );
            _rows.Add(
                PropRow.Path(
                    label: "Shader (.wgsl)",
                    value: _shown.SpriteShaderPath ?? "",
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<string?>(
                            state: _state,
                            oldValue: capturedNode.SpriteShaderPath,
                            newValue: v,
                            setter: val => capturedNode.SpriteShaderPath = val
                        )
                    ),
                    rootPath: _state.AssetRoot,
                    extensions: [".wgsl"],
                    theme: _theme,
                    app: _app
                )
            );

            _rows.Add(SectionRow(title: "Sorting", theme: _theme));
            _rows.Add(
                PropRow.Float(
                    label: "Layer",
                    value: capturedNode.SpriteSortingLayer,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            state: _state,
                            oldValue: capturedNode.SpriteSortingLayer,
                            newValue: (int)v,
                            setter: val => capturedNode.SpriteSortingLayer = val
                        )
                    ),
                    theme: _theme,
                    min: -100f,
                    max: 100f,
                    step: 1f
                )
            );
            _rows.Add(
                PropRow.Float(
                    label: "Order",
                    value: capturedNode.SpriteOrderInLayer,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<int>(
                            state: _state,
                            oldValue: capturedNode.SpriteOrderInLayer,
                            newValue: (int)v,
                            setter: val => capturedNode.SpriteOrderInLayer = val
                        )
                    ),
                    theme: _theme,
                    min: -100f,
                    max: 100f,
                    step: 1f
                )
            );
        }
        else if (_shown.Kind == NodeKind.Tilemap) BuildTilemapRows(capturedNode);

        // A 2D collider belongs to anything that can sit in the 2D world, not to one node kind.
        if (_shown.Kind is NodeKind.Sprite or NodeKind.Tilemap or NodeKind.Empty)
            BuildCollider2DRows(capturedNode);

        if (_shown.Kind is NodeKind.Mesh or NodeKind.Empty)
        {
            _rows.Add(PropRow.Spacer(4f));
            _rows.Add(SectionRow(title: "Physics", theme: _theme));
            _rows.Add(
                PropRow.Toggle(
                    label: "Use Physics",
                    value: _shown.UsePhysics,
                    onChange: v => _state.History.Execute(
                        new ChangePropertyCommand<bool>(
                            state: _state,
                            oldValue: capturedNode.UsePhysics,
                            newValue: v,
                            setter: val =>
                            {
                                capturedNode.UsePhysics = val;
                                Rebuild();
                            }
                        )
                    ),
                    theme: _theme
                )
            );

            if (_shown.UsePhysics)
            {
                _rows.Add(
                    PropRow.Toggle(
                        label: "Static",
                        value: _shown.IsStatic,
                        onChange: v => _state.History.Execute(
                            new ChangePropertyCommand<bool>(
                                state: _state,
                                oldValue: capturedNode.IsStatic,
                                newValue: v,
                                setter: val =>
                                {
                                    capturedNode.IsStatic = val;
                                    Rebuild();
                                }
                            )
                        ),
                        theme: _theme
                    )
                );
                _rows.Add(
                    PropRow.Toggle(
                        label: "Use Gravity",
                        bind: NodeBind.To(
                            state: _state,
                            node: capturedNode,
                            getter: n => n.UseGravity,
                            setter: (n, v) => n.UseGravity = v
                        ),
                        theme: _theme
                    )
                );
                _rows.Add(
                    PropRow.DropdownRow(
                        label: "Shape",
                        items: ["Box", "Sphere", "Capsule", "Cylinder"],
                        selectedIndex: (int)_shown.PhysicsShape,
                        onChange: i => _state.History.Execute(
                            new ChangePropertyCommand<PhysicsShapeType>(
                                state: _state,
                                oldValue: capturedNode.PhysicsShape,
                                newValue: (PhysicsShapeType)i,
                                setter: val => capturedNode.PhysicsShape = val
                            )
                        ),
                        theme: _theme
                    )
                );
                _rows.Add(
                    PropRow.Vec3(
                        label: "Half Extents",
                        bind: NodeBind.To(
                            state: _state,
                            node: capturedNode,
                            getter: n => n.PhysicsHalfExtents,
                            setter: (n, v) => n.PhysicsHalfExtents = v
                        ),
                        theme: _theme
                    )
                );
                if (!_shown.IsStatic)
                {
                    _rows.Add(
                        PropRow.Float(
                            label: "Mass",
                            bind: NodeBind.To(
                                state: _state,
                                node: capturedNode,
                                getter: n => n.PhysicsMass,
                                setter: (n, v) => n.PhysicsMass = v
                            ),
                            theme: _theme,
                            min: 0.01f,
                            max: 1000f,
                            step: 0.5f
                        )
                    );
                }

                _rows.Add(
                    PropRow.Float(
                        label: "Friction",
                        bind: NodeBind.To(
                            state: _state,
                            node: capturedNode,
                            getter: n => n.PhysicsFriction,
                            setter: (n, v) => n.PhysicsFriction = v
                        ),
                        theme: _theme
                    )
                );
                _rows.Add(
                    PropRow.Float(
                        label: "Restitution",
                        bind: NodeBind.To(
                            state: _state,
                            node: capturedNode,
                            getter: n => n.PhysicsRestitution,
                            setter: (n, v) => n.PhysicsRestitution = v
                        ),
                        theme: _theme
                    )
                );
            }
        }

        // ── Script (available on every node kind) ─────────────────────────────
        _rows.Add(PropRow.Spacer(4f));
        _rows.Add(SectionRow(title: "Script", theme: _theme));
        _rows.Add(
            PropRow.Suggest(
                label: "Class",
                value: _shown.ScriptClass ?? "",
                suggest: q => _state.ScriptRegistry.All
                    .Where(m => string.IsNullOrEmpty(q)
                                || m.FullName.Contains(
                                    value: q,
                                    comparisonType: StringComparison.OrdinalIgnoreCase
                                )
                                || m.DisplayName.Contains(
                                    value: q,
                                    comparisonType: StringComparison.OrdinalIgnoreCase
                                )
                    )
                    .OrderByDescending(m => m.DisplayName.StartsWith(
                            value: q,
                            comparisonType: StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .ThenBy(m => m.DisplayName)
                    .Select(m => (m.FullName, m.DisplayName))
                    .Take(12)
                    .ToList(),
                onCommit: v => _state.History.Execute(
                    new ChangePropertyCommand<string?>(
                        state: _state,
                        oldValue: capturedNode.ScriptClass,
                        newValue: v,
                        setter: val =>
                        {
                            capturedNode.ScriptClass = val;
                            Rebuild();
                        }
                    )
                ),
                theme: _theme,
                app: _app
            )
        );
        _rows.Add(
            PropRow.Path(
                label: "Path",
                value: _shown.ScriptPath ?? "",
                onChange: v => _state.History.Execute(
                    new ChangePropertyCommand<string?>(
                        state: _state,
                        oldValue: capturedNode.ScriptPath,
                        newValue: v,
                        setter: val => capturedNode.ScriptPath = val
                    )
                ),
                rootPath: _state.AssetRoot,
                extensions: [".csproj", ".cs"],
                theme: _theme,
                app: _app
            )
        );
        _rows.Add(
            PropRow.ActionButton(
                label: "Build & Reload",
                onClick: () =>
                {
                    string? path = ResolveProjectPath(capturedNode.ScriptPath);
                    if (path != null) _ = _state.BuildScriptsAsync(path);
                }
            )
        );

        // Build status
        if (_state.IsScriptBuilding)
            _rows.Add(PropRow.StatusLine(text: "Building...", color: _theme.Hint, theme: _theme));
        else if (_state.ScriptDiagnostics.Count > 0)
        {
            int errors =
                _state.ScriptDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            int warnings =
                _state.ScriptDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            string summary = errors > 0 ? $"{errors} error{(errors != 1 ? "s" : "")}" : "";
            if (warnings > 0)
            {
                summary += (summary.Length > 0 ? ", " : "") +
                           $"{warnings} warning{(warnings != 1 ? "s" : "")}";
            }

            _rows.Add(
                PropRow.StatusLine(
                    text: summary,
                    color: errors > 0 ? _theme.Error : _theme.Accent,
                    theme: _theme
                )
            );
            foreach (var d in _state.ScriptDiagnostics.Take(8))
                _rows.Add(PropRow.DiagnosticLine(d: d, theme: _theme));
        }

        if (!string.IsNullOrEmpty(_shown.ScriptClass))
        {
            var meta = _state.ScriptRegistry.Find(_shown.ScriptClass);
            if (meta?.ExportedFields.Length > 0)
            {
                _rows.Add(PropRow.Spacer(4f));
                _rows.Add(SectionRow(title: "Properties", theme: _theme));
                foreach (var field in meta.ExportedFields)
                    _rows.Add(BuildExportedFieldRow(field: field, meta: meta, node: capturedNode));
            }
        }

        // Drop rows belonging to a collapsed section (everything between a collapsed header and the
        // next header). Headers always show so the section can be re-expanded.
        var visible = new List<PropRow>(_rows.Count);
        bool collapsing = false;
        foreach (var r in _rows)
        {
            if (r.IsSectionHeader)
            {
                collapsing = r.SectionTitle != null && _collapsedSections.Contains(r.SectionTitle);
                visible.Add(r);
            }
            else if (!collapsing) visible.Add(r);
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
                padding: EdgeInsets.All(8f),
                child: new ColoredBox(
                    color: _theme.Primary.WithAlpha(0.12f),
                    child: new Padding(
                        padding: EdgeInsets.Symmetric(horizontal: 8f, vertical: 6f),
                        child: new Label(
                            text: $"{count} nodes selected",
                            fontSize: _theme.FontSizeCaption,
                            color: _theme.Primary
                        )
                    )
                )
            )
        );
        col.Children.Add(
            new Padding(
                padding: EdgeInsets.Symmetric(horizontal: 12f, vertical: 4f),
                child: new Label(
                    text: "Ctrl+click to toggle, Shift+click to range-select.",
                    fontSize: _theme.FontSizeCaption,
                    color: _theme.Hint
                )
            )
        );
        foreach (var n in _state.SelectedNodes.Take(20))
        {
            col.Children.Add(
                new Padding(
                    padding: new EdgeInsets(
                        left: 12f,
                        top: 2f,
                        right: 4f,
                        bottom: 2f
                    ),
                    child: new Label(
                        text: $"  • {n.Name}",
                        fontSize: _theme.FontSizeCaption,
                        color: _theme.OnSurface
                    )
                )
            );
        }

        if (count > 20)
        {
            col.Children.Add(
                new Padding(
                    padding: EdgeInsets.Symmetric(horizontal: 12f, vertical: 2f),
                    child: new Label(
                        text: $"  … and {count - 20} more",
                        fontSize: _theme.FontSizeCaption,
                        color: _theme.Hint
                    )
                )
            );
        }

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
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        _content.Layout(origin);
    }

    public override void Paint(PaintList paint) => _content.Paint(paint);

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _content.HitTest(point);
    }
}
