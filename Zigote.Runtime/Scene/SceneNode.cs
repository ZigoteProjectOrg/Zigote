using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Zigote.Cinematics;
using Zigote.Core;
using Zigote.Core.Assets;
using Zigote.Core.Engine;
using Zigote.Core.Math3D;
using Zigote.Core.Native;
using Zigote.Core.Physics;
using Zigote.Ecs.Scene;
using Zigote.Game.Resources;
using Zigote.Runtime.Animation;
using Zigote.Runtime.Content;

namespace Zigote.Runtime.Scene;

public enum NodeKind
{
    Empty,
    Mesh,
    Light,
    Camera,
    Script,
    ReflectionProbe,
    AudioSource,
    VfxEmitter,
    Sprite,
    Tilemap,
}

public enum LightType : byte
{
    Directional = 0,
    Point = 1,
    Spot = 2,
}

public sealed class SceneNode : IEcsSceneNode
{
    private static int _nextId = 1;
    private string? _emissiveTexturePath;
    private string? _lastUploadedEmissiveTexturePath;
    private string? _lastUploadedMrTexturePath;
    private string? _lastUploadedNormalTexturePath;
    private string? _lastUploadedTexturePath;
    private uint _meshAlphaMode;
    private RenderEffect _meshEffect = RenderEffect.Standard;
    private string? _meshPath;
    private string? _mrTexturePath;

    // Last transform/material values pushed to native. SyncToNative skips an FFI setter when its inputs
    // are unchanged, so a static scene re-rendered each paint stops re-marshalling ~7 setters per mesh.
    // _nativePushed is reset to false whenever the native Handle is (re)created (below), guaranteeing a
    // freshly created node always gets a full push.
    private bool _nativePushed;
    private string? _normalTexturePath;
    private uint _pAlphaMode;

    private bool _pDoubleSided;

    private RenderEffect _pEffect;

    // Absolute paths queued for the parallel batch loader (set by CollectTextureJobs, consumed by
    // SyncToNativeBatched, cleared by MarkTexturesUploaded). Null = nothing queued for that slot.
    private string? _pendingBasePath;
    private string? _pendingEmissivePath;
    private string? _pendingMrPath;
    private string? _pendingNormalPath;

    private bool _pLightCastShadows;
    private LightType _pLightKind;

    private float _pMetallic,
        _pRoughness,
        _pClearcoat,
        _pClearcoatRoughness,
        _pSpecular,
        _pIor,
        _pTransmission,
        _pOcclusionStrength,
        _pAlphaCutoff,
        _pLightIntensity,
        _pLightRange,
        _pLightInnerDeg,
        _pLightOuterDeg,
        _pCameraFovDeg = float.NaN,
        _pCameraNear = float.NaN,
        _pCameraFar = float.NaN;

    private Vec3 _pPos, _pScaleEff, _pColor, _pEmissive, _pLightColorEff;
    private Quat _pRot;
    private string? _texturePath;

    // Parameterless constructor used by System.Text.Json so that ReferenceHandler.Preserve
    // can use property setters instead of matching constructor parameters (which rejects $ref).
    [JsonConstructor]
    public SceneNode()
    {
    }

    public SceneNode(string name, NodeKind kind = NodeKind.Empty)
    {
        Name = name;
        Kind = kind;
    }

    /// <summary>Free-form gameplay tag ("Enemy", "Pickup") queried via the World scripting API.</summary>
    public string? Tag { get; set; }

    public NodeKind Kind { get; set; } = NodeKind.Empty;

    [JsonIgnore] public ulong Handle { get; set; }

    /// <summary>
    ///     Last native visibility the LOD/cull system applied (dedupes the per-frame FFI call). Transient,
    ///     not serialized; managed by <c>LodSystem</c>.
    /// </summary>
    [JsonIgnore]
    internal bool LodVisibleApplied { get; set; } = true;

    /// <summary>Editor-only node (e.g. transform gizmos). Never serialized into a saved scene.</summary>
    [JsonIgnore]
    public bool IsInternal { get; set; }

    public bool IsHidden { get; set; }
    public bool Visible { get; set; } = true;

    // ── Level of detail (generic, project-agnostic) ───────────────────────────
    // Distance LOD/culling evaluated each frame by LodSystem against the active camera (RenderView).
    // Distances are world units from the camera to this node's world position. Project-agnostic: any
    // scene loaded in the editor can use it. Composes with native frustum culling (off-screen) — this
    // handles "too far" / "pick a detail level".

    /// <summary>
    ///     Hide this node (and its subtree) when the camera is farther than this many world units.
    ///     0 = no distance limit (always considered). Pure distance culling.
    /// </summary>
    public float LodMaxDistance { get; set; }

    /// <summary>
    ///     When true, this node's direct children are mutually-exclusive LOD levels: the system shows
    ///     exactly one — the nearest level whose <see cref="LodMaxDistance" /> still covers the camera
    ///     distance (children ordered near→far by ascending LodMaxDistance) — and hides the rest. Beyond
    ///     the farthest level's distance the whole group is culled. A level with LodMaxDistance 0 is the
    ///     fallback "always covers" level (put it last).
    /// </summary>
    public bool LodGroup { get; set; }

    public LightType LightKind { get; set; } = LightType.Point;
    public float LightIntensity { get; set; } = 1.0f;
    public float LightRange { get; set; } = 50.0f;
    public Vec3 LightColor { get; set; } = Vec3.One;

    /// <summary>
    ///     Black-body colour temperature in Kelvin (6500 = neutral). Tints the light without a native
    ///     change.
    /// </summary>
    public float LightTemperature { get; set; } = ColorTemperature.Neutral;

    /// <summary>
    ///     Spot cone angles (degrees). Authoring-only until the native light FFI is widened (see
    ///     SyncToNative).
    /// </summary>
    public float SpotInnerAngleDeg { get; set; } = 20f;

    public float SpotOuterAngleDeg { get; set; } = 35f;

    /// <summary>
    ///     Whether this light casts shadows. Authoring-only until the native per-light shadow FFI
    ///     lands.
    /// </summary>
    public bool LightCastShadows { get; set; } = true;

    /// <summary>Effective light colour = base colour × temperature tint (what gets pushed to native).</summary>
    [JsonIgnore]
    public Vec3 EffectiveLightColor => LightColor * ColorTemperature.KelvinToRgb(LightTemperature);

    /// <summary>Reflection-probe box half-extents (local). Only meaningful when Kind == ReflectionProbe.</summary>
    public Vec3 ProbeExtents { get; set; } = new(5f, 5f, 5f);

    // ── Camera (only meaningful when Kind == Camera) ───────────────────────────
    // Plain projection fills the pre-existing authoring gap (native defaulted to 45/0.1/4000). The
    // physical-camera block below layers a photographic model (lens/sensor/film/exposure/focus) on top —
    // see Zigote.Cinematics + PhysicalCameraMapping. Defaults preserve the previous effective behaviour
    // (45° FOV) so existing scenes are unchanged until a physical camera is enabled.

    /// <summary>
    ///     Perspective vertical field of view in degrees (ignored when the physical camera is
    ///     enabled).
    /// </summary>
    public float CameraFovDegrees { get; set; } = 45f;

    public float CameraNear { get; set; } = 0.1f;
    public float CameraFar { get; set; } = 4000f;

    /// <summary>
    ///     0 = perspective, 1 = orthographic (perspective only for now; matches ProjectionKind
    ///     order).
    /// </summary>
    public int CameraProjection { get; set; }

    public Vec2 CameraOrthoSize { get; set; } = new(2f, 2f);

    // Physical camera — a photographic model resolved to FOV + DoF + exposure + film grade each frame.
    // Enums stored as int for clean JSON under ReferenceHandler.Preserve (map to Zigote.Cinematics enums).
    public bool PhysEnabled { get; set; }
    public int PhysSensorPreset { get; set; } // Zigote.Cinematics.SensorPreset
    public float PhysSensorWidthMm { get; set; } = 36f;
    public float PhysSensorHeightMm { get; set; } = 24f;
    public float PhysFocalLengthMm { get; set; } = 50f;
    public float PhysFStop { get; set; } = 2.8f;
    public int PhysApertureBlades { get; set; }
    public float PhysAnamorphic { get; set; } = 1f;
    public float PhysDistortionK1 { get; set; }
    public float PhysIso { get; set; } = 100f;
    public float PhysShutterSpeed { get; set; } = 1f / 50f;
    public int PhysFocusMode { get; set; } // Zigote.Cinematics.FocusModeKind (0 = Manual)
    public float PhysManualFocusM { get; set; } = 8f;
    public float PhysFocusSpeed { get; set; } = 4f;

    /// <summary>Subject-mode autofocus target: the Id of the scene node to track (null = none).</summary>
    public int? PhysFocusTargetNodeId { get; set; }

    public int PhysFilmStock { get; set; } // Zigote.Cinematics.FilmStockKind (0 = Neutral)
    public float PhysFilmStrength { get; set; } = 1f;
    public bool PhysAffectExposure { get; set; } = true;
    public bool PhysAffectGrade { get; set; } = true;
    public bool PhysAffectDof { get; set; } = true;

    [JsonIgnore] public SceneNode? Parent { get; internal set; }
    public List<SceneNode> Children { get; set; } = [];

    public string? MeshPath
    {
        get => _meshPath;
        set
        {
            _meshPath = value;
            if (Handle != 0)
            {
                _lastUploadedTexturePath = null;
                _lastUploadedMrTexturePath = null;
                _lastUploadedNormalTexturePath = null;
                UploadMesh();
                UpdateTexture();
                UpdateMrTexture();
                UpdateNormalTexture();
            }
        }
    }

    public Vec3 MeshColor { get; set; } = new(0.8f, 0.8f, 0.8f);
    public float MeshMetallic { get; set; }

    public float MeshRoughness { get; set; } = 0.5f;

    // Extended PBR (KHR_materials_clearcoat / _specular / _ior). Clearcoat 0 = no coat (default).
    public float MeshClearcoat { get; set; }
    public float MeshClearcoatRoughness { get; set; }

    public float MeshSpecular { get; set; } = 1.0f;

    /// <summary>Index of refraction (KHR_materials_ior). Drives dielectric F0 and glass refraction.</summary>
    public float MeshIor { get; set; } = 1.5f;

    /// <summary>Transmission factor 0..1 (KHR_materials_transmission). >0 routes to the glass path.</summary>
    public float MeshTransmission { get; set; }

    /// <summary>Render both faces (no back-face culling). glTF doubleSided.</summary>
    public bool MeshDoubleSided { get; set; }

    /// <summary>Alpha-mask cutoff threshold (glTF alphaCutoff; used when MeshAlphaMode == 1).</summary>
    public float MeshAlphaCutoff { get; set; } = 0.5f;

    /// <summary>ORM occlusion strength: >0 marks the MR map's R channel as baked AO (glTF packing).</summary>
    public float MeshOcclusionStrength { get; set; }

    // Emissive colour (KHR_materials_emissive_strength already folded in). Default black = none.
    public Vec3 MeshEmissive { get; set; } = Vec3.Zero;

    /// <summary>
    ///     Animation clips imported with this subtree (glTF). Not serialized — re-imported with
    ///     the model; channels bind to descendant nodes by name. Usually set on an imported model root.
    /// </summary>
    [JsonIgnore]
    public List<AnimationClip> Animations { get; } = [];

    public RenderEffect MeshEffect
    {
        get => _meshEffect;
        set
        {
            _meshEffect = value;
            if (Handle != 0 && Kind == NodeKind.Mesh)
                ZigoteEngine.Instance?.SceneSetMeshEffect(Handle, (uint)_meshEffect);
        }
    }

    /// <summary>Material alpha mode: 0=opaque, 1=mask, 2=blend, 3=glass.</summary>
    public uint MeshAlphaMode
    {
        get => _meshAlphaMode;
        set
        {
            _meshAlphaMode = value;
            if (Handle != 0 && Kind == NodeKind.Mesh)
                ZigoteEngine.Instance?.SceneSetMeshAlphaMode(
                    Handle,
                    _meshAlphaMode,
                    MeshAlphaCutoff
                );
        }
    }

    public string? TexturePath
    {
        get => _texturePath;
        set
        {
            _texturePath = value;
            if (Handle != 0 && Kind == NodeKind.Mesh) UpdateTexture();
        }
    }

    /// <summary>Metallic-roughness map (glTF: roughness in G, metallic in B). Multiplies the factors.</summary>
    public string? MetallicRoughnessTexturePath
    {
        get => _mrTexturePath;
        set
        {
            _mrTexturePath = value;
            if (Handle != 0 && Kind == NodeKind.Mesh) UpdateMrTexture();
        }
    }

    /// <summary>Tangent-space normal map (linear). Applied via the mesh TBN basis.</summary>
    public string? NormalTexturePath
    {
        get => _normalTexturePath;
        set
        {
            _normalTexturePath = value;
            if (Handle != 0 && Kind == NodeKind.Mesh) UpdateNormalTexture();
        }
    }

    /// <summary>Emissive map (sRGB). Multiplied by <see cref="MeshEmissive" />.</summary>
    public string? EmissiveTexturePath
    {
        get => _emissiveTexturePath;
        set
        {
            _emissiveTexturePath = value;
            if (Handle != 0 && Kind == NodeKind.Mesh) UpdateEmissiveTexture();
        }
    }

    public string? ScriptPath { get; set; }
    public string? ScriptClass { get; set; }

    public Dictionary<string, string> ScriptExports { get; set; } = new();

    // ── Prefab instance link ───────────────────────────────────────────────────
    // When set, this node is an instance of a .prefab asset. Its numeric authorable state is inherited
    // from the prefab via ScenePrefabLibrary/EcsPrefab; per-property overrides are tracked there.

    /// <summary>The prefab asset this node instantiates; <see cref="AssetId.Empty" /> for a normal node.</summary>
    [JsonIgnore]
    public AssetId PrefabSource { get; set; } = AssetId.Empty;

    /// <summary>Is this node an instance of a prefab asset?</summary>
    [JsonIgnore]
    public bool IsPrefabInstance => !PrefabSource.IsEmpty;

    /// <summary>
    ///     Serialized GUID form of <see cref="PrefabSource" /> (a string round-trips cleanly under
    ///     <c>ReferenceHandler.Preserve</c>, unlike the record-struct itself); null when not an instance.
    /// </summary>
    [JsonInclude]
    public string? PrefabSourceId
    {
        get => PrefabSource.IsEmpty ? null : PrefabSource.ToString();
        set => PrefabSource = AssetId.TryParse(value, out var id) ? id : AssetId.Empty;
    }

    // Physics (play mode)
    public bool UsePhysics { get; set; }
    public bool UseGravity { get; set; } = true;
    public bool IsStatic { get; set; }
    public PhysicsShapeType PhysicsShape { get; set; } = PhysicsShapeType.Box;
    public Vec3 PhysicsHalfExtents { get; set; } = new(0.5f, 0.5f, 0.5f);
    public float PhysicsMass { get; set; } = 1f;
    public float PhysicsFriction { get; set; } = 0.2f;
    public float PhysicsRestitution { get; set; }

    // Audio source (spatial / surround). Only meaningful when Kind == AudioSource. The engine is driven
    // by GameSession in play mode (and the inspector's preview in edit mode); the source position comes
    // from the node transform and the listener follows the active camera. Simple value types so they
    // serialize through the normal property path.
    /// <summary>false = procedural oscillator tone; true = decode/stream <see cref="AudioClipPath" />.</summary>
    public bool AudioUseFile { get; set; }

    /// <summary>
    ///     Clip path (WAV/OGG/MP3/FLAC) used when <see cref="AudioUseFile" />. Resolved relative to
    ///     the scene.
    /// </summary>
    public string? AudioClipPath { get; set; }

    /// <summary>Stream long clips (music) instead of fully decoding (SFX).</summary>
    public bool AudioStreaming { get; set; }

    /// <summary>Procedural waveform: 0 sine, 1 square, 2 triangle, 3 saw, 4 noise.</summary>
    public int AudioWaveform { get; set; }

    public float AudioFrequency { get; set; } = 220f;
    public float AudioVolume { get; set; } = 0.8f;
    public float AudioPitch { get; set; } = 1f;
    public bool AudioLoop { get; set; } = true;

    /// <summary>Start playing automatically when play mode begins.</summary>
    public bool AudioAutoPlay { get; set; } = true;

    /// <summary>3D positioned (panned + distance-attenuated) vs. a flat 2D source.</summary>
    public bool AudioSpatial { get; set; } = true;

    public float AudioMinDistance { get; set; } = 1f;
    public float AudioMaxDistance { get; set; } = 50f;
    public float AudioRolloff { get; set; } = 1f;

    // VFX emitter (particle system). Only meaningful when Kind == VfxEmitter. The authored node-graph is
    // persisted as a JSON string (a plain serializable field — the live GraphDocument can't be embedded
    // directly under ReferenceHandler.Preserve), regenerated into a runtime VfxEmitterAsset on play/preview.
    // GameSession owns the live simulation in play mode; the inspector previews it in edit mode.
    /// <summary>Serialized VFX node-graph (see <c>VfxGraphSerializer</c>); empty = the default preset.</summary>
    public string? VfxGraphJson { get; set; }

    /// <summary>Start emitting automatically when play mode begins.</summary>
    public bool VfxPlayOnStart { get; set; } = true;

    /// <summary>
    ///     Baked <c>VfxEmitterAsset</c> JSON (see <c>VfxAssetJson</c>), written by game export in
    ///     place of the node graph. When set, playback uses it directly and never needs a graph compiler.
    /// </summary>
    public string? VfxBakedJson { get; set; }

    // ── Sprite (2D renderer; only meaningful when Kind == Sprite) ───────────────
    // The texture rides the shared TexturePath property (so asset-dependency export ships it; the
    // mesh-material upload paths all gate on Kind == Mesh, so there is no crosstalk). The sprite is a
    // world-space XY quad: world size = frame pixels / SpritePixelsPerUnit × node scale, position =
    // node world position, rotation = the node's Z rotation. Sprite2DSystem (Zigote.Runtime) collects
    // and draws these each frame in both hosts; play-mode frame animation state lives in the session,
    // never in these authored fields.

    /// <summary>Sprite-sheet grid columns (1 = whole texture).</summary>
    public int SpriteCols { get; set; } = 1;

    /// <summary>Sprite-sheet grid rows (1 = whole texture).</summary>
    public int SpriteRows { get; set; } = 1;

    /// <summary>Authored (poster) frame index into the grid, row-major.</summary>
    public int SpriteFrame { get; set; }

    /// <summary>Frames/second for play-mode animation over the whole grid (0 = static).</summary>
    public float SpriteFps { get; set; }

    /// <summary>Tint (straight alpha), multiplied with the texture.</summary>
    public Vec4 SpriteColor { get; set; } = new(
        1f,
        1f,
        1f,
        1f
    );

    public bool SpriteFlipX { get; set; }
    public bool SpriteFlipY { get; set; }

    /// <summary>
    ///     Coarse draw order: higher layers draw over lower ones (then order-in-layer, then scene
    ///     order).
    /// </summary>
    public int SpriteSortingLayer { get; set; }

    public int SpriteOrderInLayer { get; set; }

    /// <summary>How many texture pixels make one world unit (sprite world size = pixels / this).</summary>
    public float SpritePixelsPerUnit { get; set; } = 100f;

    /// <summary>
    ///     Pivot inside the sprite rect (0..1); position/rotation act about this point. (0.5, 0.5) =
    ///     center.
    /// </summary>
    public float SpritePivotX { get; set; } = 0.5f;

    public float SpritePivotY { get; set; } = 0.5f;

    /// <summary>Material blend: 0 alpha, 1 additive, 2 opaque (Zigote.Render2D.Blend2D).</summary>
    public int SpriteBlend { get; set; }

    /// <summary>Render stage: 0 scene (HDR, bloom/tonemap apply), 1 overlay (post-tonemap, exact colors).</summary>
    public int SpriteStage { get; set; }

    /// <summary>Optional custom material shader (.wgsl, sprite contract). Resolved relative to the scene.</summary>
    public string? SpriteShaderPath { get; set; }

    /// <summary>
    ///     Corner radius in world units; 0 = square. At half the shorter side the quad becomes a
    ///     capsule (a circle when square) — how the 2D canvas draws rounded panels and discs.
    /// </summary>
    public float SpriteCornerRadius { get; set; }

    /// <summary>Outline thickness in world units; 0 fills, greater than 0 strokes a ring in the tint.</summary>
    public float SpriteBorderWidth { get; set; }

    // ── Tilemap (2D; only meaningful when Kind == Tilemap) ─────────────────────
    // Tiles are drawn by Sprite2DSystem through the same Renderer2D batcher as sprites: one layer's
    // visible cells become sprite instances sharing the tileset texture, so a whole tilemap collapses
    // into one GPU batch. World placement: tile (0,0)'s lower-left corner sits at the node's world
    // position; one tile spans TileWorldSize units, and the node's scale multiplies that.

    /// <summary>Path to the <c>.tileset</c> asset backing every layer, relative to the project root.</summary>
    public string? TilesetPath { get; set; }

    /// <summary>Layers, painted back-to-front within their sorting layer / order-in-layer.</summary>
    public List<TilemapLayer> TilemapLayers { get; set; } = [];

    /// <summary>World size of one tile edge. 1 tile = 1 unit by default.</summary>
    public float TileWorldSize { get; set; } = 1f;

    /// <summary>Tint applied to every tile (straight alpha), multiplied with the texture.</summary>
    public Vec4 TilemapColor { get; set; } = new(
        1f,
        1f,
        1f,
        1f
    );

    /// <summary>Material blend: 0 alpha, 1 additive, 2 opaque (Zigote.Render2D.Blend2D).</summary>
    public int TilemapBlend { get; set; }

    /// <summary>Render stage: 0 scene, 1 overlay — same meaning as <see cref="SpriteStage" />.</summary>
    public int TilemapStage { get; set; }

    /// <summary>Bake solid-flagged tiles into the 2D collision world on play.</summary>
    public bool TilemapCollision { get; set; } = true;

    // ── 2D collider (Physics2D; any 2D node) ───────────────────────────────────
    // Physics2D is an axis-aligned box/circle world with no rotation and no polygons — these fields
    // mirror exactly what CollisionWorld2D.AddBox/AddCircle accept, and nothing more.

    /// <summary>Contribute a collider to the 2D collision world.</summary>
    public bool Collider2DEnabled { get; set; }

    /// <summary>0 = box (<see cref="Collider2DSize" />), 1 = circle (<see cref="Collider2DRadius" />).</summary>
    public int Collider2DShape { get; set; }

    /// <summary>Offset from the node's world position, in world units.</summary>
    public Vec2 Collider2DOffset { get; set; }

    /// <summary>Box half-extents in world units.</summary>
    public Vec2 Collider2DSize { get; set; } = new(0.5f, 0.5f);

    public float Collider2DRadius { get; set; } = 0.5f;

    /// <summary>Collision layer bitmask handed to <c>CollisionWorld2D</c>.</summary>
    public uint Collider2DLayer { get; set; } = 1;

    /// <summary>Reports overlaps without blocking movement.</summary>
    public bool Collider2DIsTrigger { get; set; }

    /// <summary>Jump-through platform: blocks only downward crossings.</summary>
    public bool Collider2DOneWayUp { get; set; }

    public string KindIcon => Kind switch {
        NodeKind.Mesh => "[M]",
        NodeKind.Light => "[L]",
        NodeKind.Camera => "[C]",
        NodeKind.Script => "[S]",
        NodeKind.ReflectionProbe => "[P]",
        NodeKind.AudioSource => "[A]",
        NodeKind.VfxEmitter => "[V]",
        NodeKind.Sprite => "[2]",
        NodeKind.Tilemap => "[#]",
        _ => "[ ]",
    };

    // The EcsSceneBridge mirrors the node tree to flecs entities via this surface. Children differs in
    // element type (List<SceneNode> vs IReadOnlyList<IEcsSceneNode>), so it's an explicit implementation;
    // Id/Name/Position/Rotation/Scale already satisfy the interface directly.
    IReadOnlyList<IEcsSceneNode> IEcsSceneNode.Children => Children;

    public int Id { get; } = _nextId++;
    public string Name { get; set; } = "Node";

    // Transform
    public Vec3 Position { get; set; } = Vec3.Zero;
    public Quat Rotation { get; set; } = Quat.Identity;
    public Vec3 Scale { get; set; } = Vec3.One;

    private unsafe void UpdateTexture()
    {
        if (Handle == 0 || Kind != NodeKind.Mesh)
            return;

        if (_texturePath == _lastUploadedTexturePath)
            return;

        try
        {
            if (string.IsNullOrEmpty(_texturePath) || !File.Exists(_texturePath))
            {
                ZigoteEngine.Instance?.SceneSetMeshTextureFile(Handle, null);
                _lastUploadedTexturePath = _texturePath;
                return;
            }

            var absPath = Path.GetFullPath(_texturePath);
            var pathBytes = Encoding.UTF8.GetBytes(absPath + "\0");
            fixed (byte* pathPtr = pathBytes)
            {
                ZigoteEngine.Instance?.SceneSetMeshTextureFile(Handle, pathPtr);
            }

            _lastUploadedTexturePath = _texturePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading texture {_texturePath}: {ex.Message}");
        }
    }

    private unsafe void UpdateMrTexture()
    {
        if (Handle == 0 || Kind != NodeKind.Mesh) return;
        if (_mrTexturePath == _lastUploadedMrTexturePath) return;

        try
        {
            if (string.IsNullOrEmpty(_mrTexturePath) || !File.Exists(_mrTexturePath))
            {
                ZigoteEngine.Instance?.SceneSetMeshMrTextureFile(Handle, null);
                _lastUploadedMrTexturePath = _mrTexturePath;
                return;
            }

            var absPath = Path.GetFullPath(_mrTexturePath);
            var pathBytes = Encoding.UTF8.GetBytes(absPath + "\0");
            fixed (byte* pathPtr = pathBytes)
            {
                ZigoteEngine.Instance?.SceneSetMeshMrTextureFile(Handle, pathPtr);
            }

            _lastUploadedMrTexturePath = _mrTexturePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading MR texture {_mrTexturePath}: {ex.Message}");
        }
    }

    private unsafe void UpdateNormalTexture()
    {
        if (Handle == 0 || Kind != NodeKind.Mesh) return;
        if (_normalTexturePath == _lastUploadedNormalTexturePath) return;

        try
        {
            if (string.IsNullOrEmpty(_normalTexturePath) || !File.Exists(_normalTexturePath))
            {
                ZigoteEngine.Instance?.SceneSetMeshNormalTextureFile(Handle, null);
                _lastUploadedNormalTexturePath = _normalTexturePath;
                return;
            }

            var t0 = LoadProfile.Mark();
            var absPath = Path.GetFullPath(_normalTexturePath);
            var pathBytes = Encoding.UTF8.GetBytes(absPath + "\0");
            fixed (byte* pathPtr = pathBytes)
            {
                ZigoteEngine.Instance?.SceneSetMeshNormalTextureFile(Handle, pathPtr);
            }

            LoadProfile.NormalTicks += LoadProfile.Since(t0);
            LoadProfile.NormalCount++;

            _lastUploadedNormalTexturePath = _normalTexturePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading normal texture {_normalTexturePath}: {ex.Message}");
        }
    }

    private unsafe void UpdateEmissiveTexture()
    {
        if (Handle == 0 || Kind != NodeKind.Mesh) return;
        if (_emissiveTexturePath == _lastUploadedEmissiveTexturePath) return;

        try
        {
            if (string.IsNullOrEmpty(_emissiveTexturePath) || !File.Exists(_emissiveTexturePath))
            {
                ZigoteEngine.Instance?.SceneSetMeshEmissiveTextureFile(Handle, null);
                _lastUploadedEmissiveTexturePath = _emissiveTexturePath;
                return;
            }

            var absPath = Path.GetFullPath(_emissiveTexturePath);
            var pathBytes = Encoding.UTF8.GetBytes(absPath + "\0");
            fixed (byte* pathPtr = pathBytes)
            {
                ZigoteEngine.Instance?.SceneSetMeshEmissiveTextureFile(Handle, pathPtr);
            }

            _lastUploadedEmissiveTexturePath = _emissiveTexturePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Error loading emissive texture {_emissiveTexturePath}: {ex.Message}"
            );
        }
    }

    public void AddChild(SceneNode child)
    {
        child.Parent?.Children.Remove(child);
        child.Parent = this;
        Children.Add(child);
    }

    public void RemoveChild(SceneNode child)
    {
        if (Children.Remove(child)) child.Parent = null;
    }

    public IEnumerable<SceneNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants())
                yield return d;
        }
    }

    /// <summary>
    ///     Recursively clones this node and all its descendants.
    ///     The clone has no Handle (not yet registered in the Zig scene) and no parent.
    /// </summary>
    public SceneNode DeepClone(string? nameOverride = null)
    {
        var c = new SceneNode(nameOverride ?? Name, Kind);
        c.Position = Position;
        c.Rotation = Rotation;
        c.Scale = Scale;
        c.Visible = Visible;
        c.LodMaxDistance = LodMaxDistance;
        c.LodGroup = LodGroup;
        c.MeshPath = MeshPath; // safe: c.Handle == 0
        c.MeshColor = MeshColor;
        c.MeshMetallic = MeshMetallic;
        c.MeshRoughness = MeshRoughness;
        c.MeshClearcoat = MeshClearcoat;
        c.MeshClearcoatRoughness = MeshClearcoatRoughness;
        c.MeshSpecular = MeshSpecular;
        c.MeshIor = MeshIor;
        c.MeshTransmission = MeshTransmission;
        c.MeshDoubleSided = MeshDoubleSided;
        c.MeshAlphaCutoff = MeshAlphaCutoff;
        c.MeshOcclusionStrength = MeshOcclusionStrength;
        c.MeshEmissive = MeshEmissive;
        c.MeshEffect = MeshEffect; // safe: c.Handle == 0
        c.MeshAlphaMode = MeshAlphaMode; // safe: c.Handle == 0
        c.TexturePath = TexturePath; // safe: c.Handle == 0
        c.MetallicRoughnessTexturePath = MetallicRoughnessTexturePath; // safe: c.Handle == 0
        c.NormalTexturePath = NormalTexturePath;
        c.EmissiveTexturePath = EmissiveTexturePath;
        c.LightKind = LightKind;
        c.LightColor = LightColor;
        c.LightIntensity = LightIntensity;
        c.LightRange = LightRange;
        c.LightTemperature = LightTemperature;
        c.SpotInnerAngleDeg = SpotInnerAngleDeg;
        c.SpotOuterAngleDeg = SpotOuterAngleDeg;
        c.LightCastShadows = LightCastShadows;
        c.CameraFovDegrees = CameraFovDegrees;
        c.CameraNear = CameraNear;
        c.CameraFar = CameraFar;
        c.CameraProjection = CameraProjection;
        c.CameraOrthoSize = CameraOrthoSize;
        c.PhysEnabled = PhysEnabled;
        c.PhysSensorPreset = PhysSensorPreset;
        c.PhysSensorWidthMm = PhysSensorWidthMm;
        c.PhysSensorHeightMm = PhysSensorHeightMm;
        c.PhysFocalLengthMm = PhysFocalLengthMm;
        c.PhysFStop = PhysFStop;
        c.PhysApertureBlades = PhysApertureBlades;
        c.PhysAnamorphic = PhysAnamorphic;
        c.PhysDistortionK1 = PhysDistortionK1;
        c.PhysIso = PhysIso;
        c.PhysShutterSpeed = PhysShutterSpeed;
        c.PhysFocusMode = PhysFocusMode;
        c.PhysManualFocusM = PhysManualFocusM;
        c.PhysFocusSpeed = PhysFocusSpeed;
        c.PhysFocusTargetNodeId = PhysFocusTargetNodeId;
        c.PhysFilmStock = PhysFilmStock;
        c.PhysFilmStrength = PhysFilmStrength;
        c.PhysAffectExposure = PhysAffectExposure;
        c.PhysAffectGrade = PhysAffectGrade;
        c.PhysAffectDof = PhysAffectDof;
        c.ScriptPath = ScriptPath;
        c.ScriptClass = ScriptClass;
        c.UsePhysics = UsePhysics;
        c.UseGravity = UseGravity;
        c.IsStatic = IsStatic;
        c.PhysicsShape = PhysicsShape;
        c.PhysicsHalfExtents = PhysicsHalfExtents;
        c.PhysicsMass = PhysicsMass;
        c.PhysicsFriction = PhysicsFriction;
        c.PhysicsRestitution = PhysicsRestitution;
        c.AudioUseFile = AudioUseFile;
        c.AudioClipPath = AudioClipPath;
        c.AudioStreaming = AudioStreaming;
        c.AudioWaveform = AudioWaveform;
        c.AudioFrequency = AudioFrequency;
        c.AudioVolume = AudioVolume;
        c.AudioPitch = AudioPitch;
        c.AudioLoop = AudioLoop;
        c.AudioAutoPlay = AudioAutoPlay;
        c.AudioSpatial = AudioSpatial;
        c.AudioMinDistance = AudioMinDistance;
        c.AudioMaxDistance = AudioMaxDistance;
        c.AudioRolloff = AudioRolloff;
        c.VfxGraphJson = VfxGraphJson;
        c.VfxPlayOnStart = VfxPlayOnStart;
        c.VfxBakedJson = VfxBakedJson;
        c.SpriteCols = SpriteCols;
        c.SpriteRows = SpriteRows;
        c.SpriteFrame = SpriteFrame;
        c.SpriteFps = SpriteFps;
        c.SpriteColor = SpriteColor;
        c.SpriteFlipX = SpriteFlipX;
        c.SpriteFlipY = SpriteFlipY;
        c.SpriteSortingLayer = SpriteSortingLayer;
        c.SpriteOrderInLayer = SpriteOrderInLayer;
        c.SpritePixelsPerUnit = SpritePixelsPerUnit;
        c.SpritePivotX = SpritePivotX;
        c.SpritePivotY = SpritePivotY;
        c.SpriteBlend = SpriteBlend;
        c.SpriteStage = SpriteStage;
        c.SpriteShaderPath = SpriteShaderPath;
        c.SpriteCornerRadius = SpriteCornerRadius;
        c.SpriteBorderWidth = SpriteBorderWidth;
        c.TilesetPath = TilesetPath;
        c.TilemapLayers = [.. TilemapLayers.Select(l => l.Clone())];
        c.TileWorldSize = TileWorldSize;
        c.TilemapColor = TilemapColor;
        c.TilemapBlend = TilemapBlend;
        c.TilemapStage = TilemapStage;
        c.TilemapCollision = TilemapCollision;
        c.Collider2DEnabled = Collider2DEnabled;
        c.Collider2DShape = Collider2DShape;
        c.Collider2DOffset = Collider2DOffset;
        c.Collider2DSize = Collider2DSize;
        c.Collider2DRadius = Collider2DRadius;
        c.Collider2DLayer = Collider2DLayer;
        c.Collider2DIsTrigger = Collider2DIsTrigger;
        c.Collider2DOneWayUp = Collider2DOneWayUp;
        c.PrefabSource = PrefabSource;
        c.Tag = Tag;
        foreach (var kv in ScriptExports) c.ScriptExports[kv.Key] = kv.Value;
        foreach (var child in Children) c.AddChild(child.DeepClone());
        return c;
    }

    /// <summary>
    ///     Upload this node's mesh geometry to native from <see cref="MeshPath" />: a built-in
    ///     primitive (<c>#cube</c>/<c>#quad</c>/<c>#sphere</c>) or a <c>.zmesh</c> blob on disk.
    ///     No-op when there is no native handle, no path, or no engine.
    /// </summary>
    private void UploadMesh()
    {
        var engine = ZigoteEngine.Instance;
        if (Handle == 0 || engine == null || string.IsNullOrEmpty(_meshPath)) return;

        if (_meshPath.StartsWith('#'))
        {
            byte primType = _meshPath switch {
                "#cube" => 0,
                "#quad" => 1,
                "#sphere" => 2,
                "#cylinder" => 3,
                _ => 0,
            };
            engine.SceneSetMeshPrimitive(Handle, primType);
        }
        else if (ContentFiles.Exists(_meshPath))
        {
            var t0 = LoadProfile.Mark();
            var data = ContentFiles.ReadAllBytes(_meshPath);
            engine.SceneSetMeshBlob(Handle, data);
            LoadProfile.MeshTicks += LoadProfile.Since(t0);
            LoadProfile.MeshBytes += data.Length;
            LoadProfile.MeshCount++;
        }
    }

    /// <summary>
    ///     Push this node (and its subtree) to the native scene. When <paramref name="texBatch" />
    ///     is supplied, texture <em>uploads</em> are not done inline — each mesh node with a
    ///     pending texture is added to the list so the caller can decode them all in parallel via
    ///     <see cref="SyncToNativeBatched" />. Clears (texture removed) are still applied inline.
    /// </summary>
    public void SyncToNative(List<SceneNode>? texBatch = null)
    {
        if (Handle == 0 && ZigoteEngine.Instance != null)
        {
            var parentHandle = Parent?.Handle ?? 0;
            Handle = ZigoteEngine.Instance.SceneAddChildNode(parentHandle, Name, (byte)Kind);
            _lastUploadedTexturePath = null;
            _lastUploadedMrTexturePath = null;
            _lastUploadedNormalTexturePath = null;
            _nativePushed = false; // fresh native node — force a full property push below

            if (Kind == NodeKind.Mesh) UploadMesh();
        }

        if (Handle != 0 && ZigoteEngine.Instance != null)
        {
            var engine = ZigoteEngine.Instance;
            var first = !_nativePushed; // first push after (re)creation must send everything

            // When not visible, push zero scale so the GPU discards all triangles.
            var s = Visible ? Scale : Vec3.Zero;
            // Tolerant compare (ApproxEquals) so sub-tolerance float drift doesn't re-sync every frame;
            // rotation uses exact Quat equality.
            if (first || !_pPos.ApproxEquals(Position) || _pRot != Rotation ||
                !_pScaleEff.ApproxEquals(s))
            {
                engine.SceneUpdateNode(
                    Handle,
                    Position.X,
                    Position.Y,
                    Position.Z,
                    Rotation.X,
                    Rotation.Y,
                    Rotation.Z,
                    Rotation.W,
                    s.X,
                    s.Y,
                    s.Z
                );
                _pPos = Position;
                _pRot = Rotation;
                _pScaleEff = s;
            }

            if (Kind == NodeKind.Light)
            {
                // Push base colour × colour-temperature tint, plus the spot cone angles (radians) and
                // the per-light shadow flag (consumed by the renderer's spot cone falloff + perspective
                // shadow maps).
                var eff = EffectiveLightColor;
                if (first || _pLightKind != LightKind || _pLightColorEff != eff ||
                    _pLightIntensity != LightIntensity || _pLightRange != LightRange ||
                    _pLightInnerDeg != SpotInnerAngleDeg || _pLightOuterDeg != SpotOuterAngleDeg ||
                    _pLightCastShadows != LightCastShadows)
                {
                    const float deg2Rad = MathF.PI / 180f;
                    engine.SceneSetLightProperties(
                        Handle,
                        (byte)LightKind,
                        eff.X,
                        eff.Y,
                        eff.Z,
                        LightIntensity,
                        LightRange,
                        SpotInnerAngleDeg * deg2Rad,
                        SpotOuterAngleDeg * deg2Rad,
                        LightCastShadows
                    );
                    _pLightKind = LightKind;
                    _pLightColorEff = eff;
                    _pLightIntensity = LightIntensity;
                    _pLightRange = LightRange;
                    _pLightInnerDeg = SpotInnerAngleDeg;
                    _pLightOuterDeg = SpotOuterAngleDeg;
                    _pLightCastShadows = LightCastShadows;
                }
            }

            if (Kind == NodeKind.Camera)
            {
                var fovDeg = EffectiveFovDegrees();
                if (first || _pCameraFovDeg != fovDeg || _pCameraNear != CameraNear ||
                    _pCameraFar != CameraFar)
                {
                    engine.SceneSetCameraParams(
                        Handle,
                        fovDeg,
                        CameraNear,
                        CameraFar
                    );
                    _pCameraFovDeg = fovDeg;
                    _pCameraNear = CameraNear;
                    _pCameraFar = CameraFar;
                }
            }

            if (Kind == NodeKind.Mesh)
            {
                var colorPushed = false;
                if (first || _pColor != MeshColor)
                {
                    engine.SceneSetMeshColor(
                        Handle,
                        MeshColor.X,
                        MeshColor.Y,
                        MeshColor.Z
                    );
                    _pColor = MeshColor;
                    colorPushed = true;
                }

                if (first || _pMetallic != MeshMetallic || _pRoughness != MeshRoughness)
                {
                    engine.SceneSetMeshRoughness(Handle, MeshMetallic, MeshRoughness);
                    _pMetallic = MeshMetallic;
                    _pRoughness = MeshRoughness;
                }

                if (first || _pClearcoat != MeshClearcoat ||
                    _pClearcoatRoughness != MeshClearcoatRoughness ||
                    _pSpecular != MeshSpecular)
                {
                    engine.SceneSetMeshSurface(
                        Handle,
                        MeshClearcoat,
                        MeshClearcoatRoughness,
                        MeshSpecular
                    );
                    _pClearcoat = MeshClearcoat;
                    _pClearcoatRoughness = MeshClearcoatRoughness;
                    _pSpecular = MeshSpecular;
                }

                if (first || _pIor != MeshIor || _pTransmission != MeshTransmission)
                {
                    engine.SceneSetMeshVolume(Handle, MeshIor, MeshTransmission);
                    _pIor = MeshIor;
                    _pTransmission = MeshTransmission;
                }

                if (first || _pDoubleSided != MeshDoubleSided)
                {
                    engine.SceneSetMeshDoubleSided(Handle, MeshDoubleSided);
                    _pDoubleSided = MeshDoubleSided;
                }

                if (first || _pOcclusionStrength != MeshOcclusionStrength)
                {
                    engine.SceneSetMeshOcclusionStrength(Handle, MeshOcclusionStrength);
                    _pOcclusionStrength = MeshOcclusionStrength;
                }

                if (first || _pEmissive != MeshEmissive)
                {
                    engine.SceneSetMeshEmissive(
                        Handle,
                        MeshEmissive.X,
                        MeshEmissive.Y,
                        MeshEmissive.Z
                    );
                    _pEmissive = MeshEmissive;
                }

                if (first || _pEffect != MeshEffect)
                {
                    engine.SceneSetMeshEffect(Handle, (uint)MeshEffect);
                    _pEffect = MeshEffect;
                }

                // SceneSetMeshColor resets native base alpha to 1, so alpha mode must be re-applied
                // whenever colour was pushed — otherwise a transparent material loses its tint.
                if (first || colorPushed || _pAlphaMode != MeshAlphaMode ||
                    _pAlphaCutoff != MeshAlphaCutoff)
                {
                    engine.SceneSetMeshAlphaMode(Handle, MeshAlphaMode, MeshAlphaCutoff);
                    _pAlphaMode = MeshAlphaMode;
                    _pAlphaCutoff = MeshAlphaCutoff;
                }

                if (texBatch != null)
                {
                    CollectTextureJobs(texBatch);
                }
                else
                {
                    UpdateTexture();
                    UpdateMrTexture();
                    UpdateNormalTexture();
                    UpdateEmissiveTexture();
                }
            }

            _nativePushed = true;
        }

        foreach (var child in Children) child.SyncToNative(texBatch);
    }

    /// <summary>
    ///     Effective vertical FOV (degrees): the physical lens/sensor FOV when the physical camera is
    ///     enabled, otherwise the plain authored <see cref="CameraFovDegrees" />.
    /// </summary>
    public float EffectiveFovDegrees()
    {
        if (!PhysEnabled) return CameraFovDegrees;
        var sensorH = PhysSensorPreset == (int)SensorPreset.Custom
            ? PhysSensorHeightMm
            : SensorFormat.Of((SensorPreset)PhysSensorPreset).HeightMm;
        return PhysicalCameraResolver.VerticalFov(PhysFocalLengthMm, sensorH) * (180f / MathF.PI);
    }

    /// <summary>
    ///     Push this camera node's projection (FOV/near/far) to native immediately. Call after an
    ///     inspector edit to a FOV-affecting field so the viewport updates without waiting for a resync.
    ///     No-op unless this is a camera node with a native handle.
    /// </summary>
    public void PushCameraParams()
    {
        if (Handle == 0 || Kind != NodeKind.Camera) return;
        var fovDeg = EffectiveFovDegrees();
        ZigoteEngine.Instance?.SceneSetCameraParams(
            Handle,
            fovDeg,
            CameraNear,
            CameraFar
        );
        _pCameraFovDeg = fovDeg;
        _pCameraNear = CameraNear;
        _pCameraFar = CameraFar;
    }

    /// <summary>
    ///     Remove this node and its whole subtree from the native scene (freeing the engine-side
    ///     objects/GPU resources) and reset handles + push-state so a later re-add — e.g. undoing a
    ///     delete — recreates them cleanly via <see cref="SyncToNative" />. Without this, deleting a node
    ///     only detached it from the C# tree, leaving a ghost object in the native scene.
    ///     Post-order (children first) so every handle is still valid when it is removed.
    /// </summary>
    public void RemoveFromNative()
    {
        foreach (var child in Children) child.RemoveFromNative();

        if (Handle != 0)
        {
            ZigoteEngine.Instance?.SceneRemoveNode(Handle);
            Handle = 0;
        }

        _nativePushed = false;
        _lastUploadedTexturePath = null;
        _lastUploadedMrTexturePath = null;
        _lastUploadedNormalTexturePath = null;
        _lastUploadedEmissiveTexturePath = null;
    }

    /// <summary>
    ///     Sync the subtree, then decode every pending texture in parallel in one native call,
    ///     instead of decoding them one at a time on the calling thread. Use this for bulk loads
    ///     (model import, scene load) where many materials have textures.
    /// </summary>
    public void SyncToNativeBatched()
    {
        if (ZigoteEngine.Instance == null)
        {
            SyncToNative();
            return;
        }

        var pending = new List<SceneNode>();
        SyncToNative(pending); // structural sync + collect texture sets (clears applied inline)
        if (pending.Count == 0) return;

        var items = new ZgTextureLoadItem[pending.Count];
        var allocated = new List<IntPtr>(pending.Count * 2);
        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                var n = pending[i];
                var basePtr = IntPtr.Zero;
                var mrPtr = IntPtr.Zero;
                var normalPtr = IntPtr.Zero;
                var emissivePtr = IntPtr.Zero;
                if (n._pendingBasePath is { } b)
                {
                    basePtr = Marshal.StringToCoTaskMemUTF8(b);
                    allocated.Add(basePtr);
                }

                if (n._pendingMrPath is { } m)
                {
                    mrPtr = Marshal.StringToCoTaskMemUTF8(m);
                    allocated.Add(mrPtr);
                }

                if (n._pendingNormalPath is { } nm)
                {
                    normalPtr = Marshal.StringToCoTaskMemUTF8(nm);
                    allocated.Add(normalPtr);
                }

                if (n._pendingEmissivePath is { } em)
                {
                    emissivePtr = Marshal.StringToCoTaskMemUTF8(em);
                    allocated.Add(emissivePtr);
                }

                items[i] = new ZgTextureLoadItem {
                    NodeHandle = n.Handle,
                    BaseColorPath = basePtr,
                    MrPath = mrPtr,
                    NormalPath = normalPtr,
                    EmissivePath = emissivePtr,
                };
            }

            var t0 = LoadProfile.Mark();
            ZigoteEngine.Instance.SceneLoadTexturesBatch(items);
            LoadProfile.TexBatchTicks += LoadProfile.Since(t0);
        }
        finally
        {
            foreach (var p in allocated) Marshal.FreeCoTaskMem(p);
        }

        foreach (var n in pending) n.MarkTexturesUploaded();
    }

    /// <summary>
    ///     For a mesh node, queue any base-colour / MR texture that needs (re)loading into
    ///     <paramref name="batch" /> for parallel decode. Texture <em>removals</em> are applied
    ///     inline (cheap, no decode). Does not mark the node uploaded — that happens after the
    ///     batch completes via <see cref="MarkTexturesUploaded" />.
    /// </summary>
    private void CollectTextureJobs(List<SceneNode> batch)
    {
        if (Handle == 0 || Kind != NodeKind.Mesh) return;

        var queued = false;
        _pendingBasePath = null;
        _pendingMrPath = null;
        _pendingNormalPath = null;
        _pendingEmissivePath = null;

        if (_texturePath != _lastUploadedTexturePath)
        {
            if (string.IsNullOrEmpty(_texturePath) || !File.Exists(_texturePath))
            {
                UpdateTexture(); // clear / missing — handled inline (no decode)
            }
            else
            {
                _pendingBasePath = Path.GetFullPath(_texturePath);
                queued = true;
            }
        }

        if (_mrTexturePath != _lastUploadedMrTexturePath)
        {
            if (string.IsNullOrEmpty(_mrTexturePath) || !File.Exists(_mrTexturePath))
            {
                UpdateMrTexture(); // clear / missing — handled inline (no decode)
            }
            else
            {
                _pendingMrPath = Path.GetFullPath(_mrTexturePath);
                queued = true;
            }
        }

        if (_normalTexturePath != _lastUploadedNormalTexturePath)
        {
            if (string.IsNullOrEmpty(_normalTexturePath) || !File.Exists(_normalTexturePath))
            {
                UpdateNormalTexture(); // clear / missing — handled inline (no decode)
            }
            else
            {
                _pendingNormalPath = Path.GetFullPath(_normalTexturePath);
                queued = true;
            }
        }

        if (_emissiveTexturePath != _lastUploadedEmissiveTexturePath)
        {
            if (string.IsNullOrEmpty(_emissiveTexturePath) || !File.Exists(_emissiveTexturePath))
            {
                UpdateEmissiveTexture(); // clear / missing — handled inline (no decode)
            }
            else
            {
                _pendingEmissivePath = Path.GetFullPath(_emissiveTexturePath);
                queued = true;
            }
        }

        if (queued) batch.Add(this);
    }

    /// <summary>Mark the textures queued by <see cref="CollectTextureJobs" /> as uploaded.</summary>
    private void MarkTexturesUploaded()
    {
        if (_pendingBasePath != null)
        {
            _lastUploadedTexturePath = _texturePath;
            _pendingBasePath = null;
        }

        if (_pendingMrPath != null)
        {
            _lastUploadedMrTexturePath = _mrTexturePath;
            _pendingMrPath = null;
        }

        if (_pendingNormalPath != null)
        {
            _lastUploadedNormalTexturePath = _normalTexturePath;
            _pendingNormalPath = null;
        }

        if (_pendingEmissivePath != null)
        {
            _lastUploadedEmissiveTexturePath = _emissiveTexturePath;
            _pendingEmissivePath = null;
        }
    }
}