using System.Runtime.InteropServices;
using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Assets;
using Zigote.Core.Engine;
using Zigote.Core.Events;
using Zigote.Core.Lod;
using Zigote.Core.Math3D;
using Zigote.Core.Native;
using Zigote.Core.Paint;
using Zigote.Editor.History;
using Zigote.Editor.Scene;
using Zigote.Game.Resources;
using Zigote.Runtime.Prefab;
using Zigote.Runtime.Scene;
using Zigote.Runtime.Vfx;
using Zigote.Scripting;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.Vfx;
using Vec2 = Zigote.Core.Math3D.Vec2;

namespace Zigote.Editor.Panels;

/// <summary>
///     3D viewport with orbit/free-fly editor cameras and play-mode WASD controls.
///     Edit mode:
///     Orbit: right drag = rotate, scroll = zoom
///     Fly: right drag = look, WASD + Q/E = move, scroll = speed
///     Gizmo cross drag = translate selected node
///     Play mode (requires viewport focus — click to activate):
///     WASD + Q/E = move camera
///     Right drag  = mouse look
/// </summary>
public sealed partial class ViewportPanel : Widget
{
    // ── Keyboard (play mode WASD) ─────────────────────────────────────────────

    // SDL scancodes for Delete, Backspace and F11 (toggle viewport fullscreen)
    private const uint ScDelete = 76;
    private const uint ScBackspace = 42;
    private const uint ScF11 = 68;

    private const float ToolRailBtn = 30f;
    private const float ToolRailGap = 4f;
    private const float ToolRailInset = 5f;
    private const float CameraModeWidth = 210f;
    private const float CameraModeHeight = 30f;

    // ── Change-gated 3D render ─────────────────────────────────────────────────
    // The native 3D pass renders into a persistent offscreen texture whose image-cache handle is
    // returned by Render3D, so a UI repaint that changes nothing in the 3D view (toolbar hover,
    // inspector scroll, snackbar…) can re-emit last frame's image instead of re-running the full
    // pipeline (shadow cascades → G-buffer → SSAO → SSR → bloom → tonemap → TAA) plus the per-frame
    // scene walks. `_renderedSig` captures every input to the 3D image: scene/asset edits
    // (EditorState.ViewportVersion), the editor camera, viewport size, selection + gizmo mode; the
    // engine's live render settings are compared separately (covers the debug-menu Renderer panel and
    // the physical-camera driver, which write the engine directly). TAA / SSGI / auto-exposure are
    // temporal, so after the inputs settle we render extra frames to converge, then freeze —
    // SettleFrames covers TAA/SSGI; ComputeSettleFrames widens the window when slower per-rendered-
    // frame adapters are active (auto-exposure, the edit-mode physical-camera focus pull), because
    // once frozen nothing re-triggers them (their state is invisible to the signature).
    private const int SettleFrames = 20;
    private const int MaxSettleFrames = 240;

    // ── Transform snapping (rotate/scale) ──────────────────────────────────────
    private const float SnapAngleRad = 15f * MathF.PI / 180f;
    private const float SnapScaleStep = 0.1f;

    // ── Transform tool-rail (floating left overlay) ───────────────────────────

    private static readonly (GizmoMode Mode, string Icon)[] ToolRailModes = [
        (GizmoMode.Translate, Icons.Move),
        (GizmoMode.Rotate, Icons.Rotate),
        (GizmoMode.Scale, Icons.Scale),
    ];

    private static readonly CameraNavigationMode[] CameraModes = [
        CameraNavigationMode.Orbit,
        CameraNavigationMode.Fly,
        CameraNavigationMode.TwoD,
    ];

    // DrawOverlay runs every painted frame — per-frame text goes through CachedText. Enum names are
    // pre-cached: an enum inside an interpolation falls back to ToString (allocates every call).
    private static readonly string[] KindNames = Enum.GetNames<NodeKind>();

    // Edit-mode physical-camera preview: applies the active camera's DoF/exposure/film grade to the
    // viewport while authoring (play mode uses GameSession's own driver). Separate instance so each mode
    // restores the settings it snapshotted.
    private readonly PhysicalCameraDriver _editCamDriver = new();

    // Edit-mode VFX preview: simulate the scene's VfxEmitter nodes while authoring (not just in play).
    // A Ticker keeps the viewport rendering (Ticker.AnyActive) so particles animate; rebuilt only when the
    // emitter set / their graphs change (signature), so particle state persists during normal viewing.
    private readonly VfxScenePlayback _editVfx = new();
    private readonly CachedText _flyHintText = new();
    private readonly Ticker _flyTicker;
    private readonly CachedText _fpsOverlayText = new();
    private readonly CachedText _selOverlayText = new();

    // ── Widget refs ───────────────────────────────────────────────────────────

    private readonly EditorState _state;
    private readonly ThemeData _theme;

    /// <summary>
    ///     Toggle the viewport to fill the whole editor (fullscreen for testing). Wired by the editor
    ///     shell to <see cref="Widgets.DockLayout.ToggleMaximize" />. Bound to F11 while the viewport
    ///     has focus; also reachable from the panel header's maximize button.
    /// </summary>
    public Action? OnToggleMaximize;

    private CameraNavigationMode _cameraMode;
    private Vec3 _dragStartPos;
    private Quat _dragStartRot;
    private Vec3 _dragStartScale;
    private int _editVfxSig;

    // Throttles the whole-tree VFX signature scan (see UpdateEditVfx) to 4 Hz.
    private long _vfxSigNextTicks;

    // Reused per-paint scratch buffers / hoisted delegate — see their use sites.
    private readonly List<(Vec3 A, Vec3 B)> _wireEdges = [];
    private readonly List<(ulong key, CpuParticleSimulator sim)> _vfxSimScratch = [];
    private Func<int, SceneNode?>? _editFindNodeFunc;
    private Ticker? _editVfxTicker;
    private bool _flyForward, _flyBack, _flyLeft, _flyRight, _flyDown, _flyUp;
    private Vec3 _flyPosition;
    private float _flySpeed = 5f;
    private Vec3 _frameTargetCenter;
    private float _frameTargetDist;

    // ── Frame-to-selection animation ───────────────────────────────────────────
    private Ticker? _frameTicker;
    private SceneNode? _gizmoCenter;

    // ── Gizmo mode ────────────────────────────────────────────────────────────

    private GizmoMode _gizmoMode = GizmoMode.Translate;

    private SceneNode? _gizmoRoot;
    private SceneNode? _gizmoX, _gizmoY, _gizmoZ;
    private SceneNode? _gizmoXTip, _gizmoYTip, _gizmoZTip;
    private MediaQuery? _hudMedia; // viewport-sized MediaQuery wrapping the game tree

    // ── Game HUD widget host ────────────────────────────────────────────────────
    // A play-mode script can publish a full Zigote.UI widget tree via Hud.Root. We host it here, wrapped in
    // a ThemeProvider + a viewport-sized MediaQuery so theme-/media-aware widgets resolve, attached to the
    // editor App so widget invalidation routes correctly, and measured/laid-out/painted at the viewport rect
    // each play frame (the editor renders continuously in play, so the HUD re-flows live). Input routing is
    // in HitTest; focus traversal + hot-reload reach it through GetChildren.
    private Widget? _hudSource; // last seen Hud.Root (identity check)
    private Widget? _hudWrapper; // ThemeProvider→MediaQuery→Hud.Root; the attached root we drive
    private bool _isDraggingGizmoX;
    private bool _isDraggingGizmoY;
    private bool _isDraggingGizmoZ;

    // ── Rotate drag ───────────────────────────────────────────────────────────

    private bool _isDraggingRotX, _isDraggingRotY, _isDraggingRotZ;

    // ── Scale drag ────────────────────────────────────────────────────────────

    private bool _isDraggingScaleX, _isDraggingScaleY, _isDraggingScaleZ, _isDraggingScaleU;

    // ── Drag state ────────────────────────────────────────────────────────────

    private bool _isOrbitDragging;
    private bool _isRightDragging;
    private Offset _lastMousePos;
    private ulong _lastTexHandle;
    private int _lastTexW, _lastTexH;
    private float _orbitDistance = 8f;

    private float _orbitPitch = 0.3f;

    private Vec3 _orbitTarget = new(x: 0f, y: 0.5f, z: 0f);
    // ── Editor camera ────────────────────────────────────────────────────────

    private float _orbitYaw = 0.4f;

    // Reused scratch for flattening particles into the native upload buffer (9 floats/particle).
    private float[] _particleScratch = [];

    private ZgRenderSettings3D _renderedSettings;

    private (int viewportVersion, CameraNavigationMode camMode, float yaw, float pitch, float dist,
        Vec3 orbitTarget, Vec3 flyPos, uint w, uint h, ulong selected, GizmoMode gizmo)?
        _renderedSig;

    private float _rotSnapAccum;

    private Vec2 _rotateLastVec;
    private Vec2 _rotatePivotScreen;
    private int _settleFrames;
    private Size _size;

    public ViewportPanel(EditorState state, ThemeData theme)
    {
        _state = state;
        _theme = theme;
        // ZIGOTE_VIEW=2d opens straight into 2D authoring mode — the dev-loop/capture hook for the
        // 2D viewport, alongside ZIGOTE_SCENE / ZIGOTE_AUTOPLAY.
        if (string.Equals(
                a: Environment.GetEnvironmentVariable("ZIGOTE_VIEW"),
                b: "2d",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ))
        {
            _cameraMode = CameraNavigationMode.TwoD;
            Enter2DMode();
        }

        _flyTicker = new Ticker(TickFlyCamera);
        _state.AssetDropped += OnAssetDropped;
        _state.PlayStarted += () =>
        {
            ResetFlyInput();
            App.Active?.RequestFocus(this);
        };
    }

    // Snap is on while a grid step is selected (the toolbar dropdown) or Shift is held momentarily.
    private bool SnapActive =>
        _state.SnapGrid > 0f || (App.Active?.CurrentModifiers.HasFlag(Modifiers.Shift) ?? false);

    public override bool Focusable => true;

    public override void Detach()
    {
        ResetFlyInput();
        _frameTicker?.Dispose();
        _frameTicker = null;
        _editVfxTicker?.Dispose();
        _editVfxTicker = null;
        base.Detach();
    }

    /// <summary>
    ///     Motion while the game has captured the pointer. The cursor is hidden and pinned by the OS,
    ///     so this is the only motion that arrives — and unlike a drag it never runs out at the edge of
    ///     the panel, which is what makes a first-person camera work inside the editor.
    /// </summary>
    public override void OnPointerRelative(float deltaX, float deltaY)
    {
        if (_state is not { IsPlaying: true, IsPaused: false, ActivePlay: { } play }) return;
        play.LookDx += deltaX;
        play.LookDy += deltaY;
    }

    // Keyboard events reach only the focused widget, and the play-mode drive keys are latched
    // (set on key-down, cleared on key-up here). If focus leaves the viewport while a key is held —
    // clicking another panel, Tab, etc. — the key-up routes elsewhere and the latch would stick "on",
    // making the car accelerate/brake/steer by itself. Drop the latch whenever focus leaves.
    protected override void OnFocusChanged(bool focused)
    {
        if (focused) return;
        _state.ActivePlay?.ResetInput();
        ResetFlyInput();
        _isOrbitDragging = false;
        _isRightDragging = false;
        // Clicking another panel gives the cursor back, so a captured game cannot hold the pointer
        // while the player is trying to use the inspector.
        Owner?.Engine.SetRelativeMouseMode(false);
    }

    // ── Asset drop ────────────────────────────────────────────────────────────

    private void OnAssetDropped(string path, Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return;
        if (path.EndsWith(
                value: PrefabDocument.Extension,
                comparisonType: StringComparison.OrdinalIgnoreCase
            ))
        {
            var id = _state.Assets.Register(
                AssetPath.ToRelative(path: path, contentRoot: _state.ProjectDir)
            );
            if (_state.InstantiatePrefab(id) is null)
            {
                App.Active?.ShowSnackbar(
                    message: $"Failed to instantiate {Path.GetFileName(path)}",
                    duration: 4f
                );
            }

            return;
        }

        if (GltfLoader.IsSupported(path))
        {
            try
            {
                var imported = GltfLoader.Load(path: path, report: out var report);
                _state.History.Execute(
                    new AddNodeCommand(state: _state, parent: _state.Scene.Root, node: imported)
                );
                _state.RefreshAnimations(true); // preview the just-imported animation clips

                Console.Error.WriteLine(report.Summary());
                App.Active?.ShowSnackbar(
                    message: report.OneLine(),
                    duration: report.HasErrors || report.HasWarnings ? 5f : 3f
                );
            }
            catch (Exception ex)
            {
                // Import failed — surface the error rather than dropping in a broken mesh node
                // (the renderer only consumes the importer's `.zmesh` caches, never the raw file).
                Console.Error.WriteLine($"[GltfLoader] {ex.Message}");
                App.Active?.ShowSnackbar(
                    message: $"Failed to import {Path.GetFileName(path)}: {ex.Message}",
                    duration: 5f
                );
            }

            _state.NotifySceneChanged();
        }
        else if (path.EndsWith(value: ".png", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(value: ".jpg", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(
                     value: ".jpeg",
                     comparisonType: StringComparison.OrdinalIgnoreCase
                 ) ||
                 path.EndsWith(
                     value: ".webp",
                     comparisonType: StringComparison.OrdinalIgnoreCase
                 ) ||
                 path.EndsWith(value: ".gif", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            if (_state.Selected is { Kind: NodeKind.Mesh })
            {
                _state.Selected.TexturePath = path;
                _state.NotifySceneChanged();
            }
            else
            {
                // Dropping an image creates a real 2D Sprite node (the old '#quad' mesh fabrication
                // predates the sprite renderer).
                string name = Path.GetFileNameWithoutExtension(path);
                var node = _state.AddNode(name: name, kind: NodeKind.Sprite);
                node.TexturePath = path;
                node.Position = new Vec3(x: 0, y: 1, z: 0);
                _state.NotifySceneChanged();
            }
        }
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: c.MaxWidth, height: c.MaxHeight));
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
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    public override void Paint(PaintList paint)
    {
        paint.AddRect(bounds: Bounds, color: _theme.ViewportBackground);

        // Guard: negative or zero Bounds (cast to uint gives uint.MaxValue → wgpu panics).
        uint renderW = (uint)MathF.Max(x: 1f, y: MathF.Floor(Bounds.Width));
        uint renderH = (uint)MathF.Max(x: 1f, y: MathF.Floor(Bounds.Height));

        // Publish the viewport size so play-mode scripts can build a correctly-proportioned frustum
        // (the camera view-projection itself is published by GameSession from the active camera).
        RenderView.SetViewport(width: renderW, height: renderH);

        ulong texHandle = _lastTexHandle;
        int texW = _lastTexW;
        int texH = _lastTexH;
        if (ShouldRender3D(renderW: renderW, renderH: renderH, sig: out var sig))
        {
            SyncGizmos();

            // Push all C# scene state into Zig before each render so inspector
            // property edits (color, roughness, light intensity, etc.) are always
            // visible immediately — even if NotifySceneChanged fired before a
            // sub-frame layout pass reordered things.
            if (!_state.IsPlaying)
            {
                _state.Scene.Root.SyncToNative();
                PushEditorCamera();

                // Edit-mode physical-camera preview: apply the active camera's DoF/exposure/film grade so the
                // viewport shows the lens it will render through (FOV is already pushed by SyncToNative). The
                // frame is rendered from the orbit/fly camera, so pass its world position for subject autofocus.
                var editCam = FindCameraNode(_state.Scene.Root);
                float camDt = MathF.Min(x: App.Active?.DeltaTime ?? 1f / 60f, y: 1f / 30f);
                _editCamDriver.Apply(
                    camera: editCam,
                    cameraWorldPos: GetCameraPosition(),
                    viewportHeightPx: MathF.Max(x: 1f, y: Bounds.Height),
                    dt: camDt,
                    // Hoisted: a fresh closure per paint otherwise. _state is a field, so the
                    // cached delegate stays correct across scene loads.
                    findNode: _editFindNodeFunc ??=
                        id => FindNodeById(node: _state.Scene.Root, id: id)
                );
            }

            // Distance LOD + cull: select LOD levels and hide distant detail for the active camera, driving
            // real native visibility (composes with the native frustum cull). Runs every rendered frame in
            // both modes — edit mode uses the orbit camera, play mode the camera the renderer drew with.
            // Handles already exist here (edit: SyncToNative just above; play: GameSession.Update synced
            // before this paint).
            var lodCam = _state.IsPlaying && RenderView.IsAvailable
                ? RenderView.CameraPosition
                : GetCameraPosition();
            if (_state.StreamingEnabled)
                // Demand mesh streaming: the residency sink loads near meshes off-thread + unloads far ones,
                // alongside the visibility cull. Off by default (StreamDistance 0) → the plain overload runs.
            {
                LodSystem.Apply(
                    root: _state.Scene.Root,
                    cameraPos: lodCam,
                    residency: _state.MeshStreamer,
                    policy: StreamingPolicy.WithHysteresis(_state.StreamDistance)
                );
            }
            else
                LodSystem.Apply(root: _state.Scene.Root, cameraPos: lodCam);

            ZigoteEngine.Instance!.SceneSetSelectedNode(_state.Selected?.Handle ?? 0);
            PushReflectionProbe();

            // VFX renders in BOTH edit and play mode. In edit mode UpdateEditVfx simulates the scene's
            // VfxEmitter nodes (a static preview by default, animated when render.vfx_edit is on); in play mode
            // GameSession owns the simulation. GPU compute is a play-only scale path; edit mode uses the CPU
            // sim (drawn via the native billboards when a native toggle is on, else the 2D overlay).
            UpdateEditVfx();
            if (_state.IsPlaying)
            {
                if (_state.UseGpuVfx) UploadVfxParticlesGpu();
                else if (_state.UseNativeVfx) UploadVfxParticlesNative();
            }
            else if (_state.UseNativeVfx || _state.UseGpuVfx) UploadVfxParticlesNative();

            // 2D sprites render in BOTH modes through the native sprite pass. Edit mode uses the editor's
            // perspective view-proj (sprites are world-space XY quads, so they stay coherent with gizmos +
            // picking); play mode uses the game's 2D camera and includes the script draw queue.
            UploadSprites2D(renderW: renderW, renderH: renderH);

            texHandle = ZigoteEngine.Instance.Render3D(width: renderW, height: renderH);
            if (texHandle != 0)
            {
                _lastTexHandle = texHandle;
                texW = _lastTexW = (int)renderW;
                texH = _lastTexH = (int)renderH;
                // Record what this frame rendered — including the settings as they stand AFTER the
                // physical-camera driver ran, so a converged driver doesn't read as perpetually dirty.
                _renderedSig = sig;
                _renderedSettings = SafeGetSettings();
            }
            else
                _renderedSig = null; // transient failure — retry next paint
        }

        if (texHandle != 0)
        {
            paint.AddImage(
                bounds: Bounds,
                pixelWidth: texW,
                pixelHeight: texH,
                pixels: null,
                cacheKey: texHandle
            );
            Draw2DOverlay(paint); // no-op outside 2D mode
            if (_state.ShowPhysicsWireframe) DrawPhysicsWireframe(paint);
            if (!_state.UseNativeVfx && !_state.UseGpuVfx) DrawVfxParticles(paint);
            if (_gizmoMode is GizmoMode.Rotate && !_state.IsPlaying) DrawRotateRings(paint);
            DrawOverlay(paint: paint, hasReal3D: true);
        }
        else
        {
            if (Is2D)
                Draw2DOverlay(paint);
            else
            {
                DrawGrid(paint);
                DrawAxes(paint);
            }

            if (_state.ShowPhysicsWireframe) DrawPhysicsWireframe(paint);
            if (!_state.UseNativeVfx && !_state.UseGpuVfx) DrawVfxParticles(paint);
            if (_gizmoMode is GizmoMode.Rotate && !_state.IsPlaying) DrawRotateRings(paint);
            DrawOverlay(paint: paint, hasReal3D: false);
        }
    }

    /// <summary>
    ///     Decide whether this paint must re-run the native 3D render, or can re-emit the previous
    ///     offscreen frame (<see cref="_lastTexHandle" /> — the native target persists across frames).
    ///     Renders when a continuous source forces it (running play mode, app-level continuous modes,
    ///     animated edit-VFX, mesh streaming), when any signature input changed, when the engine render
    ///     settings changed under us (debug-menu Renderer panel / physical-camera driver), or while the
    ///     temporal settle window drains after a change.
    /// </summary>
    private bool ShouldRender3D(uint renderW, uint renderH,
        out (int viewportVersion, CameraNavigationMode camMode, float yaw, float pitch, float dist,
            Vec3 orbitTarget, Vec3 flyPos, uint w, uint h, ulong selected, GizmoMode gizmo) sig)
    {
        sig = (_state.ViewportVersion, _cameraMode, _orbitYaw, _orbitPitch, _orbitDistance,
            _orbitTarget,
            _flyPosition, renderW, renderH, _state.Selected?.Handle ?? 0UL, _gizmoMode);

        var app = App.Active;
        // These sources mutate the 3D image outside the signature (game ticks, animated particle sim,
        // streamed-in meshes) — keep rendering while any is active. A paused play session deliberately
        // falls through to the signature: its frame is frozen, so it idles like a static edit scene.
        bool forced = (_state.IsPlaying && !_state.IsPaused)
                      || (app?.ContinuousUpdate ?? false)
                      || (app?.ForceContinuousRender ?? false)
                      || (!_state.IsPlaying && _state.AnimateEditVfx)
                      || _state.StreamingEnabled
                      || _lastTexHandle == 0;
        if (forced)
        {
            _settleFrames = ComputeSettleFrames();
            return true;
        }

        if (_renderedSig != sig || !SettingsEqual(a: SafeGetSettings(), b: _renderedSettings))
        {
            _settleFrames = ComputeSettleFrames();
            app?.RequestExtraFrames(1);
            return true;
        }

        if (_settleFrames > 0)
        {
            _settleFrames--;
            // Self-schedule the next settle frame — RequestPaint would be cleared right after this
            // paint walk, so an idle UI would otherwise stall the chain mid-convergence.
            if (_settleFrames > 0) app?.RequestExtraFrames(1);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Size the settle window to the slowest active per-rendered-frame adapter, to ~2% residual
    ///     (ln 50 ≈ 3.9): auto-exposure converges by <c>mix(prev, target, speed)</c> per frame, and the
    ///     edit-mode physical-camera focus pull advances only inside the render branch at
    ///     <c>PhysFocusSpeed</c>/s (≈60 rendered fps while settling). Freezing earlier parks the frame
    ///     visibly mis-exposed / mid-defocus with nothing left to re-trigger a render.
    /// </summary>
    private int ComputeSettleFrames()
    {
        int frames = SettleFrames;
        var s = SafeGetSettings();
        if (s.AutoExposureEnabled != 0f)
        {
            float speed = Math.Clamp(value: s.AutoExposureSpeed, min: 0.01f, max: 0.99f);
            frames = Math.Max(
                val1: frames,
                val2: (int)MathF.Ceiling(3.9f / -MathF.Log(1f - speed))
            );
        }

        if (!_state.IsPlaying && FindCameraNode(_state.Scene.Root) is { PhysEnabled: true } physCam)
        {
            frames = Math.Max(
                val1: frames,
                val2: (int)MathF.Ceiling(
                    3.9f * 60f / Math.Clamp(value: physCam.PhysFocusSpeed, min: 0.5f, max: 60f)
                )
            );
        }

        return Math.Min(val1: frames, val2: MaxSettleFrames);
    }

    private static ZgRenderSettings3D SafeGetSettings()
    {
        try
        {
            return ZigoteEngine.Instance?.GetRenderSettings3D() ?? default;
        }
        catch
        {
            return default;
        }
    }

    private static bool SettingsEqual(in ZgRenderSettings3D a, in ZgRenderSettings3D b)
    {
        return MemoryMarshal.AsBytes(new ReadOnlySpan<ZgRenderSettings3D>(in a))
            .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<ZgRenderSettings3D>(in b)));
    }

    // ── Reflection probe ──────────────────────────────────────────────────────

    /// <summary>
    ///     Push the first active reflection-probe box to the engine each frame (or clear it if none).
    ///     The box is world-axis-aligned, centred at the probe node's world position with extents scaled
    ///     by world scale; parent rotation is ignored (sufficient for box-projected env reflections).
    /// </summary>
    private void PushReflectionProbe()
    {
        var probe = FindActiveProbe(_state.Scene.Root);
        if (probe is null)
        {
            ZigoteEngine.Instance!.ClearReflectionProbe();
            return;
        }

        var (center, scale) = WorldCenterScale(probe);
        var ext = new Vec3(
            x: probe.ProbeExtents.X * scale.X,
            y: probe.ProbeExtents.Y * scale.Y,
            z: probe.ProbeExtents.Z * scale.Z
        );
        ZigoteEngine.Instance!.SetReflectionProbe(center: center, halfExtents: ext);
    }

    private static SceneNode? FindActiveProbe(SceneNode n)
    {
        if (n.Kind == NodeKind.ReflectionProbe && n.Visible && !n.IsInternal) return n;
        foreach (var c in n.Children)
        {
            var found = FindActiveProbe(c);
            if (found is not null) return found;
        }

        return null;
    }

    private static (Vec3 center, Vec3 scale) WorldCenterScale(SceneNode n)
    {
        var pos = n.Position;
        var scl = n.Scale;
        var p = n.Parent;
        while (p is not null)
        {
            pos = new Vec3(
                x: p.Position.X + (pos.X * p.Scale.X),
                y: p.Position.Y + (pos.Y * p.Scale.Y),
                z: p.Position.Z + (pos.Z * p.Scale.Z)
            );
            scl = new Vec3(x: scl.X * p.Scale.X, y: scl.Y * p.Scale.Y, z: scl.Z * p.Scale.Z);
            p = p.Parent;
        }

        return (pos, scl);
    }

    // ── Gizmo drag helpers ────────────────────────────────────────────────────

    private Vec3 GetCameraPosition()
    {
        return _cameraMode == CameraNavigationMode.Fly
            ? _flyPosition
            : _orbitTarget - (GetCameraForward() * _orbitDistance);
    }

    private Vec3 GetCameraForward()
    {
        return new Vec3(
            x: -MathF.Sin(_orbitYaw) * MathF.Cos(_orbitPitch),
            y: -MathF.Sin(_orbitPitch),
            z: -MathF.Cos(_orbitYaw) * MathF.Cos(_orbitPitch)
        );
    }

    private Vec3 GetCameraTarget()
    {
        return _cameraMode == CameraNavigationMode.Fly
            ? _flyPosition + GetCameraForward()
            : _orbitTarget;
    }

    private Mat4 GetEditorView() => Mat4.LookAt(
        eye: GetCameraPosition(),
        center: GetCameraTarget(),
        worldUp: Vec3.Up
    );

    private void SetCameraMode(CameraNavigationMode mode)
    {
        if (_cameraMode == mode) return;

        if (mode == CameraNavigationMode.Fly)
            _flyPosition = GetCameraPosition();
        else
            _orbitTarget = GetCameraPosition() + (GetCameraForward() * _orbitDistance);

        _cameraMode = mode;
        if (mode == CameraNavigationMode.TwoD) Enter2DMode();
        else ResetTileStroke();
        ResetFlyInput();
        App.Active?.RequestPaint();
    }

    private void TickFlyCamera(float deltaTime)
    {
        if (_state.IsPlaying || _cameraMode != CameraNavigationMode.Fly) return;

        var move = Vec3.Zero;
        var forward = GetCameraForward();
        var right = new Vec3(x: MathF.Cos(_orbitYaw), y: 0f, z: -MathF.Sin(_orbitYaw));
        if (_flyForward) move += forward;
        if (_flyBack) move -= forward;
        if (_flyRight) move += right;
        if (_flyLeft) move -= right;
        if (_flyUp) move += Vec3.Up;
        if (_flyDown) move += Vec3.Down;
        if (move.LengthSq() < 1e-6f) return;

        float boost = App.Active?.CurrentModifiers.HasFlag(Modifiers.Shift) == true ? 4f : 1f;
        float dt = Math.Clamp(value: deltaTime, min: 0f, max: 0.05f);
        _flyPosition += move.Normalize() * (_flySpeed * boost * dt);
        MarkNeedsPaint();
    }

    private void ResetFlyInput()
    {
        _flyForward = _flyBack = _flyLeft = _flyRight = _flyDown = _flyUp = false;
        _flyTicker.Stop();
    }

    private bool HasFlyInput() =>
        _flyForward || _flyBack || _flyLeft || _flyRight || _flyDown || _flyUp;

    private void FrameSelection()
    {
        if (_state.Selected is not { } selected) return;
        (var center, float radius) = NodeWorldBounds(selected);
        FrameBounds(center: center, radius: radius);
    }

    /// <summary>Fit the whole scene (all visible mesh geometry) in view.</summary>
    private void FrameAll()
    {
        var min = new Vec3(x: float.MaxValue, y: float.MaxValue, z: float.MaxValue);
        var max = new Vec3(x: float.MinValue, y: float.MinValue, z: float.MinValue);
        bool any = false;

        void Walk(SceneNode n)
        {
            if (!n.IsInternal && n.Visible && n.Kind == NodeKind.Mesh &&
                !string.IsNullOrEmpty(n.MeshPath))
            {
                var (c, s) = WorldCenterScale(n);
                float hx = MathF.Abs(s.X) * 0.5f;
                float hy = MathF.Abs(s.Y) * 0.5f;
                float hz = MathF.Abs(s.Z) * 0.5f;
                min = new Vec3(
                    x: MathF.Min(x: min.X, y: c.X - hx),
                    y: MathF.Min(x: min.Y, y: c.Y - hy),
                    z: MathF.Min(x: min.Z, y: c.Z - hz)
                );
                max = new Vec3(
                    x: MathF.Max(x: max.X, y: c.X + hx),
                    y: MathF.Max(x: max.Y, y: c.Y + hy),
                    z: MathF.Max(x: max.Z, y: c.Z + hz)
                );
                any = true;
            }

            foreach (var ch in n.Children) Walk(ch);
        }

        Walk(_state.Scene.Root);
        if (!any)
        {
            FrameBounds(center: new Vec3(x: 0f, y: 0.5f, z: 0f), radius: 5f);
            return;
        }

        var center = (min + max) * 0.5f;
        float radius = ((max - min) * 0.5f).Length();
        FrameBounds(center: center, radius: radius);
    }

    /// <summary>
    ///     A node's world-space bounding sphere — center (parent transforms baked) + radius. Meshes use
    ///     the unit-cube local AABB scaled by world scale (consistent with <see cref="PickNode" />);
    ///     geometry-less nodes (lights/cameras/empties) get a small default so framing still zooms in.
    /// </summary>
    private static (Vec3 center, float radius) NodeWorldBounds(SceneNode node)
    {
        var (center, scale) = WorldCenterScale(node);
        if (node.Kind == NodeKind.Mesh && !string.IsNullOrEmpty(node.MeshPath))
        {
            var half = new Vec3(
                x: MathF.Abs(scale.X) * 0.5f,
                y: MathF.Abs(scale.Y) * 0.5f,
                z: MathF.Abs(scale.Z) * 0.5f
            );
            return (center, MathF.Max(x: 0.25f, y: half.Length()));
        }

        return (center, 1f);
    }

    /// <summary>
    ///     Smoothly orbit-frame a world-space bounding sphere (eased via <see cref="_frameTicker" />
    ///     ).
    /// </summary>
    private void FrameBounds(Vec3 center, float radius)
    {
        radius = MathF.Max(x: radius, y: 0.25f);
        const float fov = MathF.PI / 4f; // matches the projection used for picking / gizmo math
        float dist = radius / MathF.Tan(fov * 0.5f);
        _frameTargetCenter = center;
        _frameTargetDist = Math.Clamp(value: dist * 1.25f, min: 0.5f, max: 200f);
        _cameraMode = CameraNavigationMode.Orbit;
        ResetFlyInput();
        (_frameTicker ??= new Ticker(TickFraming)).Start();
    }

    private void TickFraming(float deltaTime)
    {
        float k = 1f - MathF.Exp(-Math.Clamp(value: deltaTime, min: 0f, max: 0.05f) * 14f);
        _orbitTarget += (_frameTargetCenter - _orbitTarget) * k;
        _orbitDistance += (_frameTargetDist - _orbitDistance) * k;

        if ((_frameTargetCenter - _orbitTarget).LengthSq() < 1e-5f &&
            MathF.Abs(_frameTargetDist - _orbitDistance) < 0.01f)
        {
            _orbitTarget = _frameTargetCenter;
            _orbitDistance = _frameTargetDist;
            _frameTicker?.Stop();
        }

        MarkNeedsPaint();
    }

    /// <summary>Cancel an in-flight framing animation when the user takes manual camera control.</summary>
    private void StopFraming() => _frameTicker?.Stop();

    /// <summary>
    ///     Project a world-space axis direction onto screen pixels and use it to convert
    ///     (dx, dy) screen drag into a world-space displacement along that axis.
    ///     Returns Vec3.Zero if the axis is nearly perpendicular to the view ray.
    /// </summary>
    private Vec3 ScreenDragToWorldDelta(float dx, float dy, Vec3 worldAxis)
    {
        if (_state.Selected is null || Bounds.Width < 1f || Bounds.Height < 1f) return Vec3.Zero;

        float aspect = Bounds.Width / Bounds.Height;
        var proj = Mat4.PerspectiveRhZo(
            fovyRadians: MathF.PI / 4f,
            aspect: aspect,
            near: 0.1f,
            far: 1000f
        );
        var view = GetEditorView();
        var vp = proj * view;

        Vec2 WorldToScreen(Vec3 p)
        {
            var ndc = vp.MulPoint(p);
            return new Vec2(
                x: (ndc.X + 1f) * 0.5f * Bounds.Width,
                y: (1f - ndc.Y) * 0.5f * Bounds.Height
            );
        }

        var nodePos = _state.Selected.Position;
        var s0 = WorldToScreen(nodePos);
        var s1 = WorldToScreen(nodePos + worldAxis);
        var screenVec = s1 - s0;
        float screenLen = screenVec.Length();

        // If axis collapses to < 5px (looking straight down the axis), skip movement.
        if (screenLen < 5f) return Vec3.Zero;

        var screenDir = screenVec / screenLen;
        float dot = (dx * screenDir.X) + (dy * screenDir.Y);
        return worldAxis * (dot / screenLen);
    }

    private Vec3 Snap(Vec3 p)
    {
        // In 2D, snap to the tile grid the user is actually looking at rather than the 3D snap grid.
        if (Is2D) return SnapWorld2D(p);

        float g = _state.SnapGrid;
        if (g <= 0f) return p;
        return new Vec3(
            x: MathF.Round(p.X / g) * g,
            y: MathF.Round(p.Y / g) * g,
            z: MathF.Round(p.Z / g) * g
        );
    }

    // Clamp to a positive minimum and, when snapping is active, round to a 0.1 step.
    private float SnapScale(float v)
    {
        if (SnapActive) v = MathF.Round(v / SnapScaleStep) * SnapScaleStep;
        return MathF.Max(x: 0.001f, y: v);
    }

    private Vec2 ProjectToScreen(Vec3 worldPos)
    {
        if (Bounds.Width < 1f || Bounds.Height < 1f) return Vec2.Zero;
        // Gizmo/overlay projection must use whatever camera the frame was drawn with.
        if (Is2D) return WorldToScreen2D(new Vec2(x: worldPos.X, y: worldPos.Y));

        var vp = Mat4.PerspectiveRhZo(
                     fovyRadians: MathF.PI / 4f,
                     aspect: Bounds.Width / Bounds.Height,
                     near: 0.1f,
                     far: 1000f
                 )
                 * GetEditorView();
        var ndc = vp.MulPoint(worldPos);
        return new Vec2(
            x: Bounds.X + ((ndc.X + 1f) * 0.5f * Bounds.Width),
            y: Bounds.Y + ((1f - ndc.Y) * 0.5f * Bounds.Height)
        );
    }

    // ── Physics wireframe overlay ───────────────────────────────────────────────
    // Project each collision shape's edges to screen and stroke them. In play mode the camera
    // view-projection comes from RenderView (the exact matrix the renderer drew with, published by
    // GameSession), so the overlay tracks the live simulated body; in edit mode it uses the orbit
    // camera so colliders are visible while authoring. x-ray (no depth occlusion) by design.

    private void DrawPhysicsWireframe(PaintList paint)
    {
        if (Bounds.Width < 1f || Bounds.Height < 1f) return;

        Mat4 vp;
        if (_state.IsPlaying && RenderView.IsAvailable)
            vp = RenderView.ViewProjection;
        else
        {
            vp = Mat4.PerspectiveRhZo(
                     fovyRadians: MathF.PI / 4f,
                     aspect: Bounds.Width / Bounds.Height,
                     near: 0.1f,
                     far: 1000f
                 )
                 * GetEditorView();
        }

        var nodeColor = new Color(
            r: 0.45f,
            g: 0.95f,
            b: 0.6f,
            a: 0.9f
        ); // editor-authored colliders
        var scriptColor = new Color(
            r: 0.4f,
            g: 0.85f,
            b: 0.95f,
            a: 0.9f
        ); // script-created Jolt bodies (e.g. a chassis)

        // Clip to the viewport: a collider near the screen edge can project outside Bounds, and the
        // viewport doesn't otherwise clip — without this the strokes would bleed over adjacent panels.
        paint.AddClipStart(Bounds);

        // (1) Editor-authored node.UsePhysics colliders (walk the scene tree).
        DrawPhysicsNode(
            paint: paint,
            node: _state.Scene.Root,
            vp: vp,
            color: nodeColor
        );

        // (2) Script-created bodies (made via the generic Physics API, not SceneNodes) — e.g. a vehicle
        //     chassis. Only available while playing.
        if (_state.IsPlaying && _state.ActivePlay is { } play)
        {
            foreach (var body in play.ScriptBodies)
            {
                PhysicsWireframe.WorldEdgesInto(
                    into: _wireEdges,
                    shape: body.Shape,
                    halfExtents: body.HalfExtents,
                    position: body.Position,
                    rotation: body.Rotation
                );
                StrokeEdges(
                    paint: paint,
                    vp: vp,
                    edges: _wireEdges,
                    color: scriptColor
                );
            }
        }

        // (3) Game-emitted debug lines (suspension rays, raycast wheels, etc.) via the generic DebugDraw
        //     API. These cover what has no collider at all (raycast suspension), so the car's tyres show.
        foreach (var line in DebugDraw.Queue)
        {
            if (ProjectClip(vp: vp, world: line.A, screen: out var la) && ProjectClip(
                    vp: vp,
                    world: line.B,
                    screen: out var lb
                ))
            {
                paint.AddBezier(
                    x0: la.X,
                    y0: la.Y,
                    x1: la.X,
                    y1: la.Y,
                    x2: lb.X,
                    y2: lb.Y,
                    x3: lb.X,
                    y3: lb.Y,
                    color: line.Color,
                    width: 1.2f
                );
            }
        }

        paint.AddClipEnd();
    }

    private void DrawPhysicsNode(PaintList paint, SceneNode node, Mat4 vp, Color color)
    {
        if (node.UsePhysics && !node.IsInternal)
            // Use the node's own Position/Rotation, NOT a parent-baked world transform: the physics
            // body is created from these (GameSession.RegisterBodies) and SyncFromPhysics writes the
            // simulated world transform straight back into them, so they already hold what the body
            // uses. Walking parents would double-apply ancestor transforms for parented bodies.
        {
            PhysicsWireframe.WorldEdgesInto(
                into: _wireEdges,
                shape: node.PhysicsShape,
                halfExtents: node.PhysicsHalfExtents,
                position: node.Position,
                rotation: node.Rotation
            );
            StrokeEdges(
                paint: paint,
                vp: vp,
                edges: _wireEdges,
                color: color
            );
        }

        foreach (var c in node.Children)
        {
            DrawPhysicsNode(
                paint: paint,
                node: c,
                vp: vp,
                color: color
            );
        }
    }

    // ── VFX particle overlay (play mode) ────────────────────────────────────────
    // Draws each live emitter's CPU-simulated particles as projected billboards via the 2D paint path,
    // using RenderView (the exact matrix the renderer drew with, published by GameSession). This is the
    // pre-native stand-in for the GPU billboard pass — additive blending isn't expressible here, so
    // particles alpha-blend; the proper additive / soft look arrives with the native render pass.
    private void DrawVfxParticles(PaintList paint)
    {
        if (Bounds.Width < 1f || Bounds.Height < 1f) return;

        // Play mode: the matrix the renderer drew with (RenderView). Edit mode: the orbit camera (as
        // DrawPhysicsWireframe does) — RenderView is only published while playing.
        Mat4 vp;
        Vec3 camPos;
        if (_state.IsPlaying && RenderView.IsAvailable)
        {
            vp = RenderView.ViewProjection;
            camPos = RenderView.CameraPosition;
        }
        else
        {
            vp = Mat4.PerspectiveRhZo(
                fovyRadians: MathF.PI / 4f,
                aspect: Bounds.Width / Bounds.Height,
                near: 0.1f,
                far: 1000f
            ) * GetEditorView();
            camPos = GetCameraPosition();
        }

        const float fov = MathF.PI / 4f; // both cameras use a 45° vertical FOV
        float focal = Bounds.Height * 0.5f / MathF.Tan(fov * 0.5f);

        paint.AddClipStart(Bounds);
        foreach (var (_, sim) in VfxSimSource())
        {
            var live = sim.Pool.Live;
            for (int i = 0; i < live.Length; i++)
            {
                ref readonly var p = ref live[i];
                if (!ProjectClip(vp: vp, world: p.Position, screen: out var s)) continue;
                float dist = (p.Position - camPos).Length();
                if (dist < 0.05f) continue;
                float r = MathF.Max(x: 1f, y: p.Size * 0.5f * focal / dist);
                paint.AddRect(
                    bounds: new Rect(
                        x: s.X - r,
                        y: s.Y - r,
                        width: r * 2f,
                        height: r * 2f
                    ),
                    color: p.Color,
                    radius: r
                );
            }
        }

        paint.AddClipEnd();
    }

    // Opt-in (render.vfx_native): flatten each live emitter's particles and hand them to the native GPU
    // billboard pass before Render3D, instead of the 2D-projection overlay above. Keyed by node handle.
    private void UploadVfxParticlesNative()
    {
        var engine = ZigoteEngine.Instance;
        if (engine == null) return;

        foreach ((ulong key, var sim) in VfxSimSource())
        {
            if (key == 0) continue;
            var live = sim.Pool.Live;
            int count = live.Length;
            if (count == 0)
            {
                engine.ParticlesClear(key);
                continue;
            }

            int need = count * 9;
            if (_particleScratch.Length < need)
                _particleScratch = new float[Math.Max(val1: need, val2: 256 * 9)];
            for (int i = 0; i < count; i++)
            {
                ref readonly var p = ref live[i];
                int o = i * 9;
                _particleScratch[o] = p.Position.X;
                _particleScratch[o + 1] = p.Position.Y;
                _particleScratch[o + 2] = p.Position.Z;
                _particleScratch[o + 3] = p.Size;
                _particleScratch[o + 4] = p.Rotation;
                _particleScratch[o + 5] = p.Color.R;
                _particleScratch[o + 6] = p.Color.G;
                _particleScratch[o + 7] = p.Color.B;
                _particleScratch[o + 8] = p.Color.A;
            }

            uint blend = sim.Asset.Blend == VfxBlendMode.Additive ? 0u : 1u;
            engine.ParticlesUpload(
                nodeHandle: key,
                data: _particleScratch.AsSpan(start: 0, length: need),
                count: (uint)count,
                blend: blend
            );
        }
    }

    // ── 2D sprites (native sprite pass; both modes) ────────────────────────────

    private void UploadSprites2D(uint renderW, uint renderH)
    {
        if (_state.IsPlaying && _state.ActivePlay is not null)
        {
            var vp = _state.Sprites2D.ResolvePlayCamera(
                root: _state.Scene.Root,
                viewportW: renderW,
                viewportH: renderH
            );
            _state.Sprites2D.Render(
                root: _state.Scene.Root,
                sceneViewProjection: vp,
                viewportW: renderW,
                viewportH: renderH,
                includeScriptQueue: true
            );
        }
        else
        {
            _state.Sprites2D.Render(
                root: _state.Scene.Root,
                sceneViewProjection: EditorSpriteViewProjection(),
                viewportW: renderW,
                viewportH: renderH,
                includeScriptQueue: false
            );
        }
    }

    /// <summary>
    ///     The view-projection the native renderer draws the edit-mode frame with: the orbit/fly view +
    ///     the scene camera's authored FOV/near/far (PushEditorCamera co-locates the camera node with the
    ///     orbit camera, and SyncToNative pushes its FOV) — so sprites land exactly in the rendered image.
    /// </summary>
    private Mat4 EditorSpriteViewProjection()
    {
        // 2D mode draws sprites and tiles through a true orthographic camera so tile edges stay
        // pixel-exact; the 3D pass has no ortho mode, so meshes in a 2D scene will not line up.
        if (Is2D) return Camera2DViewProjection();

        float aspect = MathF.Max(x: 1f, y: Bounds.Width) / MathF.Max(x: 1f, y: Bounds.Height);
        var cam = FindCameraNode(_state.Scene.Root);
        float fovRad = (cam?.EffectiveFovDegrees() ?? 45f) * (MathF.PI / 180f);
        float near = cam?.CameraNear ?? 0.1f;
        float far = cam?.CameraFar ?? 1000f;
        return Mat4.PerspectiveRhZo(
            fovyRadians: fovRad,
            aspect: aspect,
            near: near,
            far: far
        ) * GetEditorView();
    }

    // Opt-in (render.vfx_gpu): drive each node emitter through the native GPU compute path — the host
    // advances emission timing + uploads the lowered params; the GPU simulates + writes the instance
    // buffer the billboard pass draws. Runs at render-rate (clamped) since particles are visual, not sim.
    private void UploadVfxParticlesGpu()
    {
        var engine = ZigoteEngine.Instance;
        if (engine == null) return;
        float dt = MathF.Min(x: App.Active?.DeltaTime ?? 1f / 60f, y: 1f / 30f);

        foreach (var (node, gpu) in VfxGpuSource())
        {
            if (node.Handle == 0) continue;
            gpu.Position = node.Position;
            gpu.Orientation = node.Rotation;
            int spawn = gpu.Step(dt);
            engine.ParticlesComputeEmit(
                nodeHandle: node.Handle,
                paramsData: gpu.BuildParams(spawnCount: spawn, dt: dt),
                capacity: gpu.Capacity,
                blend: gpu.Blend
            );
        }
    }

    // ── Edit-mode VFX preview (renders VfxEmitter nodes while authoring, not just in play) ─────────────
    // Default: a STATIC representative preview — the emitters are warmed to a snapshot once (on a change)
    // and drawn frozen, so the editor never forces continuous rendering. When render.vfx_edit is on, a
    // Ticker animates them live (Ticker.AnyActive keeps the viewport rendering). Rebuilt only when the
    // emitter set / graphs / transforms change (signature). No-op while playing or with no emitters.
    private void UpdateEditVfx()
    {
        if (_state.IsPlaying)
        {
            _editVfxTicker?.Stop();
            return;
        }

        // The signature is a whole-tree walk; 4 Hz is plenty for "did an emitter change" and a
        // ≤250 ms preview-rebuild delay is invisible next to the warm-up itself.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _vfxSigNextTicks) return;
        _vfxSigNextTicks = now + (System.Diagnostics.Stopwatch.Frequency / 4);

        (int sig, bool any) = ComputeVfxSignature(_state.Scene.Root);
        if (!any)
        {
            if (_editVfxSig != 0)
            {
                _editVfx.Reset();
                _editVfxSig = 0;
                ZigoteEngine.Instance?.ParticlesClearAll();
            }

            _editVfxTicker?.Stop();
            return;
        }

        if (sig != _editVfxSig)
        {
            _editVfx.Build(_state.Scene.Root);
            foreach (var (_, sim) in _editVfx.Emitters)
                sim.Emitting = true; // always emit while authoring
            WarmEditVfx(); // simulate to a representative snapshot so the static preview shows particles
            ZigoteEngine.Instance
                ?.ParticlesClearAll(); // drop stale native batches from the old set
            _editVfxSig = sig;
        }

        // Live animation only when the toggle is on; otherwise the warmed snapshot stays frozen and the
        // viewport doesn't render continuously (it redraws on its own repaints — camera move, selection…).
        if (_state.AnimateEditVfx)
        {
            _editVfxTicker ??= new Ticker(OnEditVfxTick);
            _editVfxTicker.Start();
        }
        else
            _editVfxTicker?.Stop();
    }

    private void OnEditVfxTick(float dt)
    {
        if (_state.IsPlaying) return;
        _editVfx.Step(MathF.Min(x: dt, y: 1f / 30f));
    }

    // Step each emitter to a representative state (~0.75 s) so a static preview reads as the effect rather
    // than the empty first instant. Uses the nodes' current transforms (read inside VfxScenePlayback.Step).
    private void WarmEditVfx()
    {
        const float dt = 1f / 30f;
        for (int i = 0; i < 22; i++) _editVfx.Step(dt);
    }

    // Signature of the emitter set: rebuilds the static preview when an emitter is added/removed, its graph
    // is edited, or it is moved (coarse transform so micro-jitter doesn't thrash the warm-up).
    private static (int sig, bool any) ComputeVfxSignature(SceneNode root)
    {
        int hash = 17;
        bool any = false;
        Walk(n: root, hash: ref hash, any: ref any);
        return (hash, any);

        // static + ref params: a capturing local function allocated a display class per paint.
        static void Walk(SceneNode n, ref int hash, ref bool any)
        {
            if (n.Kind == NodeKind.VfxEmitter)
            {
                any = true;
                hash = HashCode.Combine(
                    value1: hash,
                    value2: n.Id,
                    value3: n.VfxGraphJson?.GetHashCode() ?? 0,
                    value4: n.VfxPlayOnStart,
                    value5: (int)(n.Position.X * 10f),
                    value6: (int)(n.Position.Y * 10f),
                    value7: (int)(n.Position.Z * 10f)
                );
            }

            foreach (var c in n.Children) Walk(n: c, hash: ref hash, any: ref any);
        }
    }

    // Active particle simulations (play: GameSession's node + script emitters; edit: the edit playback),
    // keyed by node handle for the native pass. Fills a reused list — this runs two-plus times per
    // paint, and the previous yield-return iterator allocated a state machine per call.
    private List<(ulong key, CpuParticleSimulator sim)> VfxSimSource()
    {
        _vfxSimScratch.Clear();
        if (_state.IsPlaying && _state.ActivePlay is { } play)
        {
            foreach (var e in play.AllVfxSimulators) _vfxSimScratch.Add(e);
            return _vfxSimScratch;
        }

        foreach (var (node, sim) in _editVfx.Emitters) _vfxSimScratch.Add((node.Handle, sim));
        return _vfxSimScratch;
    }

    private IReadOnlyList<(SceneNode node, VfxGpuEmitter gpu)> VfxGpuSource()
    {
        return _state.IsPlaying && _state.ActivePlay is { } play
            ? play.GpuVfxEmitters
            : _editVfx.GpuEmitters;
    }

    private void StrokeEdges(PaintList paint, Mat4 vp, List<(Vec3 A, Vec3 B)> edges, Color color)
    {
        foreach (var (a, b) in edges)
        {
            if (ProjectClip(vp: vp, world: a, screen: out var sa) &&
                ProjectClip(vp: vp, world: b, screen: out var sb))
            {
                paint.AddBezier(
                    x0: sa.X,
                    y0: sa.Y,
                    x1: sa.X,
                    y1: sa.Y,
                    x2: sb.X,
                    y2: sb.Y,
                    x3: sb.X,
                    y3: sb.Y,
                    color: color,
                    width: 1.2f
                );
            }
        }
    }

    /// <summary>Project a world point through a full view-projection; false if behind the camera.</summary>
    private bool ProjectClip(Mat4 vp, Vec3 world, out Vec2 screen)
    {
        var clip = vp.MulVec4(
            new Vec4(
                x: world.X,
                y: world.Y,
                z: world.Z,
                w: 1f
            )
        );
        if (clip.W <= 1e-4f)
        {
            screen = Vec2.Zero;
            return false;
        }

        float nx = clip.X / clip.W;
        float ny = clip.Y / clip.W;
        screen = new Vec2(
            x: Bounds.X + ((nx + 1f) * 0.5f * Bounds.Width),
            y: Bounds.Y + ((1f - ny) * 0.5f * Bounds.Height)
        );
        return true;
    }

    // ── Editor camera ─────────────────────────────────────────────────────────

    private void PushEditorCamera()
    {
        ulong camHandle = FindCameraHandle(_state.Scene.Root);
        if (camHandle == 0 || ZigoteEngine.Instance == null) return;

        var camPos = GetCameraPosition();
        var camRot = Quat.FromEuler(pitch: -_orbitPitch, yaw: _orbitYaw, roll: 0f);
        ZigoteEngine.Instance.SceneUpdateNode(
            nodeHandle: camHandle,
            x: camPos.X,
            y: camPos.Y,
            z: camPos.Z,
            qx: camRot.X,
            qy: camRot.Y,
            qz: camRot.Z,
            qw: camRot.W,
            sx: 1f,
            sy: 1f,
            sz: 1f
        );
    }

    private static ulong FindCameraHandle(SceneNode node)
    {
        if (node.Kind == NodeKind.Camera && node.Handle != 0) return node.Handle;
        foreach (var c in node.Children)
        {
            ulong h = FindCameraHandle(c);
            if (h != 0) return h;
        }

        return 0;
    }

    private static SceneNode? FindCameraNode(SceneNode node)
    {
        if (node.Kind == NodeKind.Camera) return node;
        foreach (var c in node.Children)
        {
            var found = FindCameraNode(c);
            if (found != null) return found;
        }

        return null;
    }

    private static SceneNode? FindNodeById(SceneNode node, int id)
    {
        if (node.Id == id) return node;
        foreach (var c in node.Children)
        {
            var found = FindNodeById(node: c, id: id);
            if (found != null) return found;
        }

        return null;
    }

    private void DropGizmos()
    {
        // Don't call SyncToNative — handles may be stale after SceneClear. Just null the C# refs.
        _gizmoRoot = null;
        _gizmoX = _gizmoY = _gizmoZ = null;
        _gizmoXTip = _gizmoYTip = _gizmoZTip = null;
        _gizmoCenter = null;
    }

    private void SyncGizmos()
    {
        // After LoadScene(), SceneClear() invalidates all Zig handles. The gizmo nodes end up
        // parented to the old Scene.Root rather than the new one — detect and drop them so they
        // are recreated fresh with valid handles on the next frame.
        if (_gizmoRoot != null && _gizmoRoot.Parent != _state.Scene.Root)
            DropGizmos();

        // Play mode: hide the gizmo (collapse to zero scale + push to native) rather than returning with
        // it left at its last edit-mode scale — otherwise the previously-selected object's gizmo stays
        // drawn over the running scene. Play mode also deselects, but SyncGizmos returned before the
        // hide path below ever ran.
        if (_state.IsPlaying)
        {
            if (_gizmoRoot != null)
            {
                _gizmoRoot.Scale = Vec3.Zero;
                _gizmoRoot.SyncToNative();
            }

            return;
        }

        if (_state.Selected != null)
        {
            if (_gizmoRoot == null)
            {
                _gizmoRoot = new SceneNode("__GizmoRoot") {
                    IsHidden = true,
                    IsInternal = true,
                };
                _gizmoX = new SceneNode(name: "__GizmoX", kind: NodeKind.Mesh) {
                    MeshPath = "#cube",
                    IsHidden = true,
                    MeshColor = new Vec3(x: 0.9f, y: 0.1f, z: 0.15f),
                    MeshEffect = RenderEffect.Unlit,
                };
                _gizmoY = new SceneNode(name: "__GizmoY", kind: NodeKind.Mesh) {
                    MeshPath = "#cube",
                    IsHidden = true,
                    MeshColor = new Vec3(x: 0.1f, y: 0.8f, z: 0.15f),
                    MeshEffect = RenderEffect.Unlit,
                };
                _gizmoZ = new SceneNode(name: "__GizmoZ", kind: NodeKind.Mesh) {
                    MeshPath = "#cube",
                    IsHidden = true,
                    MeshColor = new Vec3(x: 0.15f, y: 0.25f, z: 0.9f),
                    MeshEffect = RenderEffect.Unlit,
                };

                _gizmoX.Scale = new Vec3(x: 1.2f, y: 0.02f, z: 0.02f);
                _gizmoX.Position = new Vec3(x: 0.6f, y: 0, z: 0);

                _gizmoY.Scale = new Vec3(x: 0.02f, y: 1.2f, z: 0.02f);
                _gizmoY.Position = new Vec3(x: 0, y: 0.6f, z: 0);

                _gizmoZ.Scale = new Vec3(x: 0.02f, y: 0.02f, z: 1.2f);
                _gizmoZ.Position = new Vec3(x: 0, y: 0, z: 0.6f);

                // Arrow tip cubes — same name as shaft so picking triggers the right drag
                _gizmoXTip = new SceneNode(name: "__GizmoX", kind: NodeKind.Mesh) {
                    MeshPath = "#cube",
                    IsHidden = true,
                    MeshColor = new Vec3(x: 0.9f, y: 0.1f, z: 0.15f),
                    MeshEffect = RenderEffect.Unlit,
                };
                _gizmoYTip = new SceneNode(name: "__GizmoY", kind: NodeKind.Mesh) {
                    MeshPath = "#cube",
                    IsHidden = true,
                    MeshColor = new Vec3(x: 0.1f, y: 0.8f, z: 0.15f),
                    MeshEffect = RenderEffect.Unlit,
                };
                _gizmoZTip = new SceneNode(name: "__GizmoZ", kind: NodeKind.Mesh) {
                    MeshPath = "#cube",
                    IsHidden = true,
                    MeshColor = new Vec3(x: 0.15f, y: 0.25f, z: 0.9f),
                    MeshEffect = RenderEffect.Unlit,
                };

                _gizmoXTip.Scale = new Vec3(x: 0.14f, y: 0.14f, z: 0.14f);
                _gizmoXTip.Position = new Vec3(x: 1.2f, y: 0, z: 0);

                _gizmoYTip.Scale = new Vec3(x: 0.14f, y: 0.14f, z: 0.14f);
                _gizmoYTip.Position = new Vec3(x: 0, y: 1.2f, z: 0);

                _gizmoZTip.Scale = new Vec3(x: 0.14f, y: 0.14f, z: 0.14f);
                _gizmoZTip.Position = new Vec3(x: 0, y: 0, z: 1.2f);

                // Center sphere for scale-mode uniform drag
                _gizmoCenter = new SceneNode(name: "__GizmoCenter", kind: NodeKind.Mesh) {
                    MeshPath = "#sphere",
                    IsHidden = true,
                    MeshColor = new Vec3(x: 1f, y: 1f, z: 1f),
                    MeshEffect = RenderEffect.Unlit,
                };
                _gizmoCenter.Position = Vec3.Zero;

                _gizmoRoot.AddChild(_gizmoX);
                _gizmoRoot.AddChild(_gizmoY);
                _gizmoRoot.AddChild(_gizmoZ);
                _gizmoRoot.AddChild(_gizmoXTip);
                _gizmoRoot.AddChild(_gizmoYTip);
                _gizmoRoot.AddChild(_gizmoZTip);
                _gizmoRoot.AddChild(_gizmoCenter);

                _state.Scene.Root.AddChild(_gizmoRoot);
            }

            _gizmoRoot.Position = _state.Selected.Position;

            // Keep gizmo the same apparent size regardless of camera distance.
            float screenH = MathF.Max(x: 1f, y: _size.Height);
            float cameraDistance = MathF.Max(
                x: 0.05f,
                y: (_state.Selected.Position - GetCameraPosition()).Length()
            );
            float gizmoScale = cameraDistance * (2f * MathF.Tan(MathF.PI / 8f)) / screenH * 80f;
            _gizmoRoot.Scale = Vec3.One * gizmoScale;

            // Show/hide child nodes based on current gizmo mode
            bool showShafts = _gizmoMode is GizmoMode.Translate or GizmoMode.Scale;
            float tipSz = _gizmoMode is GizmoMode.Scale ? 0.22f : 0.14f;
            _gizmoX!.Scale = showShafts ? new Vec3(x: 1.2f, y: 0.02f, z: 0.02f) : Vec3.Zero;
            _gizmoY!.Scale = showShafts ? new Vec3(x: 0.02f, y: 1.2f, z: 0.02f) : Vec3.Zero;
            _gizmoZ!.Scale = showShafts ? new Vec3(x: 0.02f, y: 0.02f, z: 1.2f) : Vec3.Zero;
            _gizmoXTip!.Scale = showShafts ? Vec3.One * tipSz : Vec3.Zero;
            _gizmoYTip!.Scale = showShafts ? Vec3.One * tipSz : Vec3.Zero;
            _gizmoZTip!.Scale = showShafts ? Vec3.One * tipSz : Vec3.Zero;
            _gizmoCenter!.Scale = _gizmoMode is GizmoMode.Scale ? Vec3.One * 0.12f : Vec3.Zero;
            _gizmoRoot.SyncToNative();
        }
        else if (_gizmoRoot != null)
        {
            _gizmoRoot.Scale = Vec3.Zero;
            _gizmoRoot.SyncToNative();
        }
    }

    // ── Game HUD input routing + tree integration ──────────────────────────────

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;

        // In play mode, let interactive/opaque HUD widgets capture input while transparent regions fall
        // through to the viewport (camera control). Hit-test the game tree directly — not the theme/media
        // wrapper, whose InheritedWidget.HitTest absorbs misses by returning itself. The tree was laid out
        // in the last DrawGameHud (one frame stale at most; the editor renders continuously in play).
        if (_state.IsPlaying && _hudSource is not null)
        {
            var hit = _hudSource.HitTest(point);
            if (hit is not null) return hit;
        }

        return this;
    }

    public override IEnumerable<Widget> GetChildren() =>
        _hudWrapper is not null ? new[] { _hudWrapper } : [];

    // ── Pointer input ─────────────────────────────────────────────────────────

    public override void OnPointerDown(Offset point)
    {
        App.Active?.RequestFocus(this);
        _lastMousePos = point;

        // Tool-rail buttons (transform mode) take priority over scene picking; the opaque rail
        // surface (insets/gaps between buttons) also swallows clicks so they never fall through.
        if (CameraModeHit(point)) return;
        if (!_state.IsPlaying && CameraModeBounds().Contains(px: point.X, py: point.Y)) return;
        if (ToolRailHit(point)) return;
        if (!_state.IsPlaying && ToolRailBounds().Contains(px: point.X, py: point.Y)) return;

        // 2D tile tools claim the press before selection/gizmo picking, so painting never also
        // re-selects whatever sprite happens to sit under the cursor.
        if (BeginTileStroke(point)) return;

        if (_state is { Selected: not null, IsPlaying: false })
        {
            _dragStartPos = _state.Selected.Position;
            _dragStartRot = _state.Selected.Rotation;
            _dragStartScale = _state.Selected.Scale;
        }

        if (!_state.IsPlaying)
        {
            // Rotate rings: 2D overlay pick before 3D ray test
            if (_gizmoMode is GizmoMode.Rotate && _state.Selected is not null)
            {
                char ring = PickRotateRing(point);
                if (ring != '\0')
                {
                    _isDraggingRotX = ring == 'X';
                    _isDraggingRotY = ring == 'Y';
                    _isDraggingRotZ = ring == 'Z';
                    _rotSnapAccum = 0f;
                    var pivot = ProjectToScreen(_state.Selected.Position);
                    _rotatePivotScreen = pivot;
                    _rotateLastVec = new Vec2(x: point.X - pivot.X, y: point.Y - pivot.Y);
                    return;
                }
            }

            var hitNode = PickNode(point);
            if (hitNode != null)
            {
                if (_gizmoMode is GizmoMode.Translate)
                {
                    if (hitNode.Name == "__GizmoX")
                    {
                        _isDraggingGizmoX = true;
                        return;
                    }

                    if (hitNode.Name == "__GizmoY")
                    {
                        _isDraggingGizmoY = true;
                        return;
                    }

                    if (hitNode.Name == "__GizmoZ")
                    {
                        _isDraggingGizmoZ = true;
                        return;
                    }
                }

                if (_gizmoMode is GizmoMode.Scale && _state.Selected is not null)
                {
                    if (hitNode.Name == "__GizmoX")
                    {
                        _isDraggingScaleX = true;
                        return;
                    }

                    if (hitNode.Name == "__GizmoY")
                    {
                        _isDraggingScaleY = true;
                        return;
                    }

                    if (hitNode.Name == "__GizmoZ")
                    {
                        _isDraggingScaleZ = true;
                        return;
                    }

                    if (hitNode.Name == "__GizmoCenter")
                    {
                        _isDraggingScaleU = true;
                        return;
                    }
                }

                // A gizmo internal that didn't start a drag (e.g. the scale centre in translate mode)
                // must never become the selection.
                if (hitNode.Name.StartsWith("__Gizmo")) return;

                // Ctrl/Shift extend the selection (toggle membership), matching the hierarchy. A plain
                // click replaces it.
                var selMods = App.Active?.CurrentModifiers ?? Modifiers.None;
                if (selMods.HasFlag(Modifiers.Ctrl) || selMods.HasFlag(Modifiers.Shift))
                    _state.AddToSelection(hitNode);
                else
                    _state.Select(hitNode);
            }
            else
            {
                // Don't clear an additive selection when clicking empty space with a modifier held.
                var emptyMods = App.Active?.CurrentModifiers ?? Modifiers.None;
                if (!emptyMods.HasFlag(Modifiers.Ctrl) && !emptyMods.HasFlag(Modifiers.Shift))
                    _state.Select(null);
            }
        }
    }

    private SceneNode? PickNode(Offset point)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return null;

        float ndcX = ((point.X - Bounds.X) / Bounds.Width * 2f) - 1f;
        float ndcY = 1f - ((point.Y - Bounds.Y) / Bounds.Height * 2f);

        float aspect = Bounds.Width / Bounds.Height;
        var proj = Mat4.PerspectiveRhZo(
            fovyRadians: MathF.PI / 4f,
            aspect: aspect,
            near: 0.1f,
            far: 1000f
        );

        var camPos = GetCameraPosition();
        var view = GetEditorView();

        var invVp = (proj * view).Inverse();

        var nearPt = invVp.MulPoint(new Vec3(x: ndcX, y: ndcY, z: 0f));
        var farPt = invVp.MulPoint(new Vec3(x: ndcX, y: ndcY, z: 1f));
        var dir = (farPt - nearPt).Normalize();
        var ray = new Ray(origin: camPos, direction: dir);

        SceneNode? bestNode = null;
        float bestDist = float.MaxValue;

        void CheckNode(SceneNode node, Transform3D parentTransform)
        {
            var scale = node.Scale;
            if (node.Name.StartsWith("__Gizmo"))
            {
                scale = new Vec3(
                    x: MathF.Max(x: scale.X, y: 0.15f),
                    y: MathF.Max(x: scale.Y, y: 0.15f),
                    z: MathF.Max(x: scale.Z, y: 0.15f)
                );
            }

            var localTransform = new Transform3D(
                position: node.Position,
                rotation: node.Rotation,
                scale: scale
            );
            var worldTransform = Transform3D.Combine(
                parent: parentTransform,
                child: localTransform
            );

            if (node.Kind == NodeKind.Mesh && !string.IsNullOrEmpty(node.MeshPath))
            {
                var invMat = worldTransform.ToMat4().Inverse();
                var localOrigin = invMat.MulPoint(ray.Origin);
                var localDir = invMat.MulDirection(ray.Direction).Normalize();
                var localRay = new Ray(origin: localOrigin, direction: localDir);

                var hit = localRay.IntersectAabb(
                    aabbMin: new Vec3(x: -0.5f, y: -0.5f, z: -0.5f),
                    aabbMax: new Vec3(x: 0.5f, y: 0.5f, z: 0.5f)
                );
                if (hit is { tmax: > 0 })
                {
                    var hitLocalPoint = localRay.At(MathF.Max(x: 0f, y: hit.Value.tmin));
                    var hitWorldPoint = worldTransform.ToMat4().MulPoint(hitLocalPoint);
                    float dist = (hitWorldPoint - ray.Origin).LengthSq();

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestNode = node;
                    }
                }
            }
            else if (node.Kind == NodeKind.Sprite && !string.IsNullOrEmpty(node.TexturePath))
            {
                // Ray ∩ the sprite's local XY-plane rect (frame size / PPU, offset by the pivot).
                // Node scale is already in worldTransform, so the local rect is the unscaled size.
                var tex = _state.Sprites2D.GetTexture(node.TexturePath);
                if (tex != null)
                {
                    int cols = Math.Max(val1: 1, val2: node.SpriteCols);
                    int rows = Math.Max(val1: 1, val2: node.SpriteRows);
                    float ppu = MathF.Max(x: 0.001f, y: node.SpritePixelsPerUnit);
                    float w = tex.Width / (float)cols / ppu;
                    float h = tex.Height / (float)rows / ppu;
                    float cx = (0.5f - node.SpritePivotX) * w;
                    float cy = (0.5f - node.SpritePivotY) * h;

                    var invMat = worldTransform.ToMat4().Inverse();
                    var localOrigin = invMat.MulPoint(ray.Origin);
                    var localDir = invMat.MulDirection(ray.Direction).Normalize();
                    if (MathF.Abs(localDir.Z) > 1e-5f)
                    {
                        float t = -localOrigin.Z / localDir.Z;
                        if (t > 0f)
                        {
                            float px = localOrigin.X + (localDir.X * t);
                            float py = localOrigin.Y + (localDir.Y * t);
                            if (MathF.Abs(px - cx) <= w * 0.5f && MathF.Abs(py - cy) <= h * 0.5f)
                            {
                                var hitWorld = worldTransform.ToMat4()
                                    .MulPoint(new Vec3(x: px, y: py, z: 0f));
                                float dist = (hitWorld - ray.Origin).LengthSq();
                                if (dist < bestDist)
                                {
                                    bestDist = dist;
                                    bestNode = node;
                                }
                            }
                        }
                    }
                }
            }

            foreach (var child in node.Children)
                CheckNode(node: child, parentTransform: worldTransform);
        }

        CheckNode(node: _state.Scene.Root, parentTransform: Transform3D.Identity);
        return bestNode;
    }

    // Right-drag = orbit/free-look (edit) or mouse look (play)
    public override void OnRightClick(Offset point)
    {
        App.Active?.RequestFocus(this);
        _lastMousePos = point;
        if (!_state.IsPlaying &&
            (CameraModeBounds().Contains(px: point.X, py: point.Y) ||
             ToolRailBounds().Contains(px: point.X, py: point.Y)))
        {
            _isOrbitDragging = false;
            _isRightDragging = false;
            return;
        }

        StopFraming(); // manual orbit/look cancels an in-flight frame animation
        _isOrbitDragging = !_state.IsPlaying && _cameraMode == CameraNavigationMode.Orbit;
        _isRightDragging = _state.IsPlaying || _cameraMode == CameraNavigationMode.Fly;
        // 2D has nothing to orbit — right-drag pans instead.
        _isPanning2D = Is2D && !_state.IsPlaying;
    }

    public override void OnRightPointerUp(Offset point)
    {
        _isOrbitDragging = false;
        _isRightDragging = false;
        _isPanning2D = false;
    }

    public override void OnPointerMove(Offset point)
    {
        float dx = point.X - _lastMousePos.X;
        float dy = point.Y - _lastMousePos.Y;
        var lastPos = _lastMousePos;
        _lastMousePos = point;

        if (Is2D && !_state.IsPlaying)
        {
            if (_isPanning2D)
            {
                Pan2D(new Offset(x: point.X - lastPos.X, y: point.Y - lastPos.Y));
                return;
            }

            ContinueTileStroke(point);
            if (_strokePainting || _rectAnchor is not null) return;
        }

        // Left-drag: gizmo (edit mode only)
        if (_state is { Selected: not null, IsPlaying: false })
        {
            // ── Translate ──────────────────────────────────────────────────────
            if (_isDraggingGizmoX)
            {
                _state.Selected.Position = Snap(
                    _state.Selected.Position + ScreenDragToWorldDelta(
                        dx: dx,
                        dy: dy,
                        worldAxis: Vec3.Right
                    )
                );
                _state.NotifySceneChanged();
                return;
            }

            if (_isDraggingGizmoY)
            {
                _state.Selected.Position = Snap(
                    _state.Selected.Position + ScreenDragToWorldDelta(
                        dx: dx,
                        dy: dy,
                        worldAxis: Vec3.Up
                    )
                );
                _state.NotifySceneChanged();
                return;
            }

            if (_isDraggingGizmoZ)
            {
                _state.Selected.Position = Snap(
                    _state.Selected.Position + ScreenDragToWorldDelta(
                        dx: dx,
                        dy: dy,
                        worldAxis: Vec3.Back
                    )
                );
                _state.NotifySceneChanged();
                return;
            }

            // ── Rotate ─────────────────────────────────────────────────────────
            if (_isDraggingRotX || _isDraggingRotY || _isDraggingRotZ)
            {
                var curVec = new Vec2(
                    x: point.X - _rotatePivotScreen.X,
                    y: point.Y - _rotatePivotScreen.Y
                );
                if (curVec.LengthSq() > 1f)
                {
                    var prev = _rotateLastVec;
                    float cross = (prev.X * curVec.Y) - (prev.Y * curVec.X);
                    float dot = (prev.X * curVec.X) + (prev.Y * curVec.Y);
                    float angle = MathF.Atan2(y: cross, x: dot);

                    var axis = _isDraggingRotX ? Vec3.Right : _isDraggingRotY ? Vec3.Up : Vec3.Back;
                    // Flip sign when the axis faces toward the camera so clockwise always means the same thing.
                    var camToNode = (_state.Selected.Position - GetCameraPosition()).Normalize();
                    if (camToNode.Dot(axis) > 0f) angle = -angle;

                    if (SnapActive)
                    {
                        // Accumulate the free angle and only commit whole 15° steps, so a snapped drag
                        // clicks between discrete orientations instead of moving continuously.
                        _rotSnapAccum += angle;
                        float applied = MathF.Truncate(_rotSnapAccum / SnapAngleRad) * SnapAngleRad;
                        if (applied != 0f)
                        {
                            _rotSnapAccum -= applied;
                            _state.Selected.Rotation =
                                Quat.FromAxisAngle(axis: axis, angleRadians: applied) *
                                _state.Selected.Rotation;
                            _state.NotifySceneChanged();
                        }
                    }
                    else
                    {
                        _state.Selected.Rotation =
                            Quat.FromAxisAngle(axis: axis, angleRadians: angle) *
                            _state.Selected.Rotation;
                        _state.NotifySceneChanged();
                    }

                    _rotateLastVec = curVec;
                }

                return;
            }

            // ── Scale ──────────────────────────────────────────────────────────
            if (_isDraggingScaleX || _isDraggingScaleY || _isDraggingScaleZ)
            {
                var axis = _isDraggingScaleX ? Vec3.Right : _isDraggingScaleY ? Vec3.Up : Vec3.Back;
                var delta = ScreenDragToWorldDelta(dx: dx, dy: dy, worldAxis: axis);
                float amount = delta.Dot(axis) * 2f;
                var s = _state.Selected.Scale;
                _state.Selected.Scale = _isDraggingScaleX
                    ? new Vec3(x: SnapScale(s.X + amount), y: s.Y, z: s.Z)
                    : _isDraggingScaleY
                        ? new Vec3(x: s.X, y: SnapScale(s.Y + amount), z: s.Z)
                        : new Vec3(x: s.X, y: s.Y, z: SnapScale(s.Z + amount));
                _state.NotifySceneChanged();
                return;
            }

            if (_isDraggingScaleU)
            {
                float factor = MathF.Max(x: 0.001f, y: 1f + ((dx - dy) * 0.008f));
                var s = _state.Selected.Scale;
                _state.Selected.Scale = new Vec3(
                    x: SnapScale(s.X * factor),
                    y: SnapScale(s.Y * factor),
                    z: SnapScale(s.Z * factor)
                );
                _state.NotifySceneChanged();
                return;
            }
        }

        // Right-drag: mouse look in play mode (frozen while paused — Update isn't consuming the delta,
        // so accumulating it would snap the camera on resume).
        //
        // This is the un-captured path. A game that asks for pointer capture gets its motion through
        // OnPointerRelative below instead, which keeps working past the window edge.
        if (_isRightDragging && _state is
                { IsPlaying: true, IsPaused: false, ActivePlay: not null })
        {
            _state.ActivePlay.LookDx += dx;
            _state.ActivePlay.LookDy += dy;
            return;
        }

        // Right-drag: free look in the editor fly camera
        if (_isRightDragging && !_state.IsPlaying && _cameraMode == CameraNavigationMode.Fly)
        {
            _orbitYaw += dx * 0.005f;
            _orbitPitch -= dy * 0.005f;
            _orbitPitch = Math.Clamp(value: _orbitPitch, min: -1.55f, max: 1.55f);
            return;
        }

        // Right-drag: orbit in edit mode
        if (_isOrbitDragging)
        {
            _orbitYaw += dx * 0.005f;
            _orbitPitch -= dy * 0.005f;
            _orbitPitch = Math.Clamp(value: _orbitPitch, min: -1.45f, max: 1.45f);
        }
    }

    public override void OnPointerUp(Offset point)
    {
        if (Is2D && !_state.IsPlaying && (_strokePainting || _rectAnchor is not null))
        {
            EndTileStroke(point);
            return;
        }

        if (_state is { IsPlaying: false, Selected: not null })
        {
            if (_isDraggingGizmoX || _isDraggingGizmoY || _isDraggingGizmoZ)
            {
                var finalPos = _state.Selected.Position;
                _state.Selected.Position = _dragStartPos;
                _state.History.Execute(
                    new ChangePropertyCommand<Vec3>(
                        state: _state,
                        oldValue: _dragStartPos,
                        newValue: finalPos,
                        setter: v => _state.Selected!.Position = v
                    )
                );
            }

            if (_isDraggingRotX || _isDraggingRotY || _isDraggingRotZ)
            {
                var finalRot = _state.Selected.Rotation;
                _state.Selected.Rotation = _dragStartRot;
                _state.History.Execute(
                    new ChangePropertyCommand<Quat>(
                        state: _state,
                        oldValue: _dragStartRot,
                        newValue: finalRot,
                        setter: v => _state.Selected!.Rotation = v
                    )
                );
            }

            if (_isDraggingScaleX || _isDraggingScaleY || _isDraggingScaleZ || _isDraggingScaleU)
            {
                var finalScale = _state.Selected.Scale;
                _state.Selected.Scale = _dragStartScale;
                _state.History.Execute(
                    new ChangePropertyCommand<Vec3>(
                        state: _state,
                        oldValue: _dragStartScale,
                        newValue: finalScale,
                        setter: v => _state.Selected!.Scale = v
                    )
                );
            }
        }

        _isDraggingGizmoX = _isDraggingGizmoY = _isDraggingGizmoZ = false;
        _isDraggingRotX = _isDraggingRotY = _isDraggingRotZ = false;
        _isDraggingScaleX = _isDraggingScaleY = _isDraggingScaleZ = _isDraggingScaleU = false;
    }

    public override void OnScroll(float dx, float dy)
    {
        if (_state.IsPlaying) return;
        StopFraming(); // manual zoom cancels an in-flight frame animation
        if (Is2D)
        {
            Zoom2DAt(cursor: _lastMousePos, steps: dy);
            return;
        }

        if (_cameraMode == CameraNavigationMode.Fly)
        {
            _flySpeed = Math.Clamp(
                value: _flySpeed * MathF.Pow(x: 1.15f, y: dy),
                min: 0.25f,
                max: 100f
            );
            App.Active?.RequestPaint();
            return;
        }

        _orbitDistance = Math.Clamp(value: _orbitDistance - (dy * 0.5f), min: 0.5f, max: 200f);
    }

    public override void OnKey(char keyChar, uint scancode, bool down, Modifiers mods)
    {
        // Esc — hand the cursor back. This is the only escape hatch that works while the pointer is
        // captured: the cursor is hidden and pinned, so the menu bar cannot be clicked and every mouse
        // button routes to this panel. Swallowed only when it actually released something, so Esc
        // keeps meaning whatever the game wants the rest of the time.
        if (down && scancode == (uint)KeyCode.Escape && Owner is { Engine.RelativeMouseMode: true })
        {
            Owner.Engine.SetRelativeMouseMode(false);
            return;
        }

        // F11 — toggle viewport fullscreen (works in both edit and play mode).
        if (down && scancode == ScF11)
        {
            OnToggleMaximize?.Invoke();
            return;
        }

        // Delete selected node (edit mode only, on key down)
        if (down && !_state.IsPlaying && (scancode == ScDelete || scancode == ScBackspace))
        {
            _state.DeleteSelected();
            return;
        }

        // Edit-mode keyboard shortcuts (checked before single-key gizmo shortcuts)
        if (down && !_state.IsPlaying)
        {
            // Ctrl+D: duplicate selected node
            if (mods.HasFlag(Modifiers.Ctrl) && char.ToLower(keyChar) == 'd')
            {
                _state.DuplicateSelected();
                return;
            }

            if (mods == Modifiers.None && char.ToLower(keyChar) == 'c')
            {
                SetCameraMode(
                    _cameraMode == CameraNavigationMode.Orbit
                        ? CameraNavigationMode.Fly
                        : CameraNavigationMode.Orbit
                );
                return;
            }

            if (char.ToLower(keyChar) == 'f')
            {
                if (mods.HasFlag(Modifiers.Shift)) FrameAll();
                else if (mods == Modifiers.None) FrameSelection();
                return;
            }

            // Gizmo mode: T / R / S (only without modifier so Ctrl+S etc. fall through)
            if (mods == Modifiers.None && _cameraMode == CameraNavigationMode.Orbit)
            {
                switch (char.ToLower(keyChar))
                {
                    case 't':
                        _gizmoMode = GizmoMode.Translate;
                        return;
                    case 'r':
                        _gizmoMode = GizmoMode.Rotate;
                        return;
                    case 's':
                        _gizmoMode = GizmoMode.Scale;
                        return;
                }
            }
        }

        if (!_state.IsPlaying && _cameraMode == CameraNavigationMode.Fly)
        {
            switch (char.ToLower(keyChar))
            {
                case 'w': _flyForward = down; break;
                case 's': _flyBack = down; break;
                case 'a': _flyLeft = down; break;
                case 'd': _flyRight = down; break;
                case 'q': _flyDown = down; break;
                case 'e': _flyUp = down; break;
                default: return;
            }

            if (HasFlyInput()) _flyTicker.Start();
            else _flyTicker.Stop();
            MarkNeedsPaint();
            return;
        }

        if (!_state.IsPlaying || _state.ActivePlay is null) return;

        // P toggles pause/resume (on key-down), before the WASD drive keys.
        if (down && char.ToLower(keyChar) == 'p')
        {
            _state.TogglePause();
            return;
        }

        var play = _state.ActivePlay;
        // Publish the raw key to the session's general held-key set, so a game script can read ANY
        // key (menus, a second couch player, custom bindings) — not just the built-in drive keys.
        if (Enum.GetName((KeyCode)scancode) is { } keyName) play.SetKey(name: keyName, down: down);

        switch (char.ToLower(keyChar))
        {
            case 'w': play.MoveForward = down; break;
            case 's': play.MoveBack = down; break;
            case 'a': play.MoveLeft = down; break;
            case 'd': play.MoveRight = down; break;
            case 'q': play.MoveDown = down; break;
            case 'e': play.MoveUp = down; break;
            case ' ': play.Handbrake = down; break;
            case 'r': play.ResetCar = down; break;
        }
    }

    // ── Decorative draw (edit mode fallback) ─────────────────────────────────

    private void DrawGrid(PaintList paint)
    {
        float cx = Bounds.X + (Bounds.Width * 0.5f);
        float cy = Bounds.Y + (Bounds.Height * 0.5f);
        var color = _theme.OnSurface.WithAlpha(0.18f);
        float horizon = cy - (Bounds.Height * 0.1f);

        for (int i = 1; i <= 8; i++)
        {
            float t = i / 9f;
            float y = horizon + ((cy + (Bounds.Height * 0.4f) - horizon) * (t * t));
            float xSpan = Bounds.Width * 0.5f * t;
            float thick = 0.5f + (t * 1.5f);
            paint.AddRect(
                bounds: new Rect(
                    x: cx - xSpan,
                    y: y,
                    width: xSpan * 2f,
                    height: thick
                ),
                color: color.WithAlpha(color.A * t)
            );
        }

        for (int i = -5; i <= 5; i++)
        {
            float xFar = cx + (i * (Bounds.Width * 0.5f / 5f));
            float xNear = cx + (i * (Bounds.Width * 0.45f));
            float yFar = horizon;
            float yNear = cy + (Bounds.Height * 0.4f);
            float w = MathF.Max(x: 0.8f, y: MathF.Abs(xNear - xFar));
            paint.AddRect(
                bounds: new Rect(
                    x: MathF.Min(x: xFar, y: xNear),
                    y: yFar,
                    width: w,
                    height: yNear - yFar
                ),
                color: color.WithAlpha(0.25f)
            );
        }
    }

    private void DrawAxes(PaintList paint)
    {
        float ox = Bounds.X + 40f;
        float oy = Bounds.Y + Bounds.Height - 40f;
        const float len = 20f;

        paint.AddRect(
            bounds: new Rect(
                x: ox,
                y: oy - 1f,
                width: len,
                height: 2f
            ),
            color: new Color(r: 0.9f, g: 0.2f, b: 0.2f)
        );
        paint.AddText(
            text: "X",
            baselineX: ox + len + 2f,
            baselineY: oy + 4f,
            color: new Color(r: 0.9f, g: 0.2f, b: 0.2f),
            fontSize: 11f
        );

        paint.AddRect(
            bounds: new Rect(
                x: ox - 1f,
                y: oy - len,
                width: 2f,
                height: len
            ),
            color: new Color(r: 0.2f, g: 0.9f, b: 0.2f)
        );
        paint.AddText(
            text: "Y",
            baselineX: ox - 4f,
            baselineY: oy - len - 4f,
            color: new Color(r: 0.2f, g: 0.9f, b: 0.2f),
            fontSize: 11f
        );

        float zLen = len * 0.7f;
        paint.AddRect(
            bounds: new Rect(
                x: ox - (zLen * 0.5f),
                y: oy - (zLen * 0.5f),
                width: zLen,
                height: 1.5f
            ),
            color: new Color(
                r: 0.2f,
                g: 0.4f,
                b: 0.9f,
                a: 0.8f
            )
        );
        paint.AddText(
            text: "Z",
            baselineX: ox - (zLen * 0.5f) - 12f,
            baselineY: oy - (zLen * 0.5f) + 4f,
            color: new Color(r: 0.2f, g: 0.4f, b: 0.9f),
            fontSize: 11f
        );
    }

    private void DrawRotateRings(PaintList paint)
    {
        if (_state.Selected is null || Bounds.Width < 1f || Bounds.Height < 1f) return;
        var center = ProjectToScreen(_state.Selected.Position);
        if (!Bounds.Contains(px: center.X, py: center.Y)) return;

        // Three concentric screen-space rings: Z (inner, blue), Y (middle, green), X (outer, red)
        DrawScreenRing(
            paint: paint,
            center: center,
            radius: 65f,
            color: new Color(r: 0.15f, g: 0.25f, b: 0.9f),
            width: _isDraggingRotZ ? 3.5f : 2f
        );
        DrawScreenRing(
            paint: paint,
            center: center,
            radius: 80f,
            color: new Color(r: 0.1f, g: 0.8f, b: 0.15f),
            width: _isDraggingRotY ? 3.5f : 2f
        );
        DrawScreenRing(
            paint: paint,
            center: center,
            radius: 95f,
            color: new Color(r: 0.9f, g: 0.1f, b: 0.15f),
            width: _isDraggingRotX ? 3.5f : 2f
        );

        paint.AddRect(
            bounds: new Rect(
                x: center.X - 3f,
                y: center.Y - 3f,
                width: 6f,
                height: 6f
            ),
            color: new Color(
                r: 1f,
                g: 1f,
                b: 1f,
                a: 0.6f
            ),
            radius: 3f
        );

        paint.AddText(
            text: "Z",
            baselineX: center.X + 67f,
            baselineY: center.Y + 4f,
            color: new Color(r: 0.35f, g: 0.5f, b: 0.95f),
            fontSize: 10f
        );
        paint.AddText(
            text: "Y",
            baselineX: center.X + 82f,
            baselineY: center.Y + 4f,
            color: new Color(r: 0.2f, g: 0.85f, b: 0.3f),
            fontSize: 10f
        );
        paint.AddText(
            text: "X",
            baselineX: center.X + 97f,
            baselineY: center.Y + 4f,
            color: new Color(r: 0.95f, g: 0.25f, b: 0.2f),
            fontSize: 10f
        );
    }

    private static void DrawScreenRing(PaintList paint, Vec2 center, float radius, Color color,
        float width)
    {
        float sz = radius * 2f;
        paint.AddBorder(
            bounds: new Rect(
                x: center.X - radius,
                y: center.Y - radius,
                width: sz,
                height: sz
            ),
            color: color,
            radius: radius,
            width: width
        );
    }

    private char PickRotateRing(Offset point)
    {
        if (_state.Selected is null || Bounds.Width < 1f) return '\0';
        var center = ProjectToScreen(_state.Selected.Position);
        float dist = MathF.Sqrt(
            ((point.X - center.X) * (point.X - center.X)) +
            ((point.Y - center.Y) * (point.Y - center.Y))
        );
        const float tol = 9f;
        if (MathF.Abs(dist - 65f) < tol) return 'Z';
        if (MathF.Abs(dist - 80f) < tol) return 'Y';
        if (MathF.Abs(dist - 95f) < tol) return 'X';
        return '\0';
    }

    private Rect ToolRailBounds()
    {
        float h = (ToolRailModes.Length * ToolRailBtn) +
                  ((ToolRailModes.Length - 1) * ToolRailGap) +
                  (ToolRailInset * 2f);
        float w = ToolRailBtn + (ToolRailInset * 2f);
        return new Rect(
            x: Bounds.X + 10f,
            y: Bounds.Y + ((Bounds.Height - h) * 0.5f),
            width: w,
            height: h
        );
    }

    private Rect ToolRailButtonRect(int i)
    {
        var rb = ToolRailBounds();
        return new Rect(
            x: rb.X + ToolRailInset,
            y: rb.Y + ToolRailInset + (i * (ToolRailBtn + ToolRailGap)),
            width: ToolRailBtn,
            height: ToolRailBtn
        );
    }

    private void DrawToolRail(PaintList paint)
    {
        var rb = ToolRailBounds();
        paint.AddElevation(bounds: rb, radius: Radii.Md, style: Elevation.Z1);
        paint.AddRect(bounds: rb, color: _theme.Panel.WithAlpha(0.95f), radius: Radii.Md);
        paint.AddBorder(bounds: rb, color: _theme.Border, radius: Radii.Md);

        for (int i = 0; i < ToolRailModes.Length; i++)
        {
            (var mode, string icon) = ToolRailModes[i];
            var br = ToolRailButtonRect(i);
            bool active = _gizmoMode == mode;
            if (active) paint.AddRect(bounds: br, color: _theme.Accent, radius: Radii.Sm);
            Icons.Draw(
                paint: paint,
                glyph: icon,
                box: br,
                color: active ? _theme.OnPrimary : _theme.TextSecondary,
                size: 18f
            );
        }
    }

    /// <summary>Hit-test the tool-rail; switches gizmo mode and returns true when a button is hit.</summary>
    private bool ToolRailHit(Offset point)
    {
        if (_state.IsPlaying) return false;
        for (int i = 0; i < ToolRailModes.Length; i++)
        {
            if (ToolRailButtonRect(i).Contains(px: point.X, py: point.Y))
            {
                _gizmoMode = ToolRailModes[i].Mode;
                App.Active?.RequestPaint();
                return true;
            }
        }

        return false;
    }

    private Rect CameraModeBounds()
    {
        return new Rect(
            x: Bounds.X + ((Bounds.Width - CameraModeWidth) * 0.5f),
            y: Bounds.Y + 8f,
            width: CameraModeWidth,
            height: CameraModeHeight
        );
    }

    private Rect CameraModeButtonRect(CameraNavigationMode mode)
    {
        var bounds = CameraModeBounds();
        int index = Array.IndexOf(array: CameraModes, value: mode);
        if (index < 0) index = 0;
        float seg = (bounds.Width - 6f) / CameraModes.Length;
        return new Rect(
            x: bounds.X + 3f + (index * seg),
            y: bounds.Y + 3f,
            width: seg,
            height: bounds.Height - 6f
        );
    }

    private void DrawCameraModeSwitch(PaintList paint)
    {
        var bounds = CameraModeBounds();
        paint.AddElevation(bounds: bounds, radius: Radii.Md, style: Elevation.Z1);
        paint.AddRect(bounds: bounds, color: _theme.Panel.WithAlpha(0.95f), radius: Radii.Md);
        paint.AddBorder(bounds: bounds, color: _theme.Border, radius: Radii.Md);

        DrawMode(mode: CameraNavigationMode.Orbit, label: "Orbit");
        DrawMode(mode: CameraNavigationMode.Fly, label: "Fly");
        DrawMode(mode: CameraNavigationMode.TwoD, label: "2D");

        void DrawMode(CameraNavigationMode mode, string label)
        {
            var button = CameraModeButtonRect(mode);
            bool active = _cameraMode == mode;
            if (active) paint.AddRect(bounds: button, color: _theme.Accent, radius: Radii.Sm);
            float width = label.Length * _theme.FontSizeCaption * 0.56f;
            paint.AddText(
                text: label,
                baselineX: button.X + ((button.Width - width) * 0.5f),
                baselineY: button.Y + (button.Height * 0.5f) + (_theme.FontSizeCaption * 0.38f),
                color: active ? _theme.OnPrimary : _theme.TextSecondary,
                fontSize: _theme.FontSizeCaption,
                fontWeight: active ? FontWeight.SemiBold : FontWeight.Normal
            );
        }
    }

    private bool CameraModeHit(Offset point)
    {
        if (_state.IsPlaying) return false;
        foreach (var mode in CameraModes)
        {
            if (CameraModeButtonRect(mode).Contains(px: point.X, py: point.Y))
            {
                SetCameraMode(mode);
                return true;
            }
        }

        return false;
    }

    private void DrawOverlay(PaintList paint, bool hasReal3D)
    {
        float fs = _theme.FontSizeCaption;

        // ── FPS counter (top-right) ───────────────────────────────────────────
        float dt = App.Active?.DeltaTime ?? 0f;
        float fps = dt > 0f ? 1f / dt : 0f;
        string fpsText = _fpsOverlayText.Update($"{fps:F0} fps");
        float fpsW = fpsText.Length * fs * 0.56f;
        paint.AddRect(
            bounds: new Rect(
                x: Bounds.Right - fpsW - 16f,
                y: Bounds.Y + 6f,
                width: fpsW + 10f,
                height: fs + 6f
            ),
            color: _theme.OverlayBackground,
            radius: 4f
        );
        paint.AddText(
            text: fpsText,
            baselineX: Bounds.Right - fpsW - 11f,
            baselineY: Bounds.Y + fs + 7f,
            color: fps >= 50f ? _theme.Success : fps >= 25f ? _theme.Warning : _theme.Error,
            fontSize: fs
        );

        // ── Play mode indicator border (all 4 edges) ─────────────────────────
        if (_state.IsPlaying)
        {
            // Paused: dim the frozen frame first, so the indicator border + badge stay crisp on top.
            if (_state.IsPaused)
                paint.AddRect(bounds: Bounds, color: _theme.Background.WithAlpha(0.35f));

            const float b = 3f;
            // Amber while paused, red while running.
            var pc = (_state.IsPaused ? _theme.Warning : _theme.Error).WithAlpha(0.9f);
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X,
                    y: Bounds.Y,
                    width: Bounds.Width,
                    height: b
                ),
                color: pc
            ); // top
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X,
                    y: Bounds.Bottom - b,
                    width: Bounds.Width,
                    height: b
                ),
                color: pc
            ); // bottom
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X,
                    y: Bounds.Y,
                    width: b,
                    height: Bounds.Height
                ),
                color: pc
            ); // left
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.Right - b,
                    y: Bounds.Y,
                    width: b,
                    height: Bounds.Height
                ),
                color: pc
            ); // right

            // Paused: centered PAUSED badge over the dimmed frame.
            if (_state.IsPaused)
            {
                const string badge = "PAUSED";
                float bfs = fs + 6f;
                float bw = badge.Length * bfs * 0.62f;
                float bx = Bounds.X + ((Bounds.Width - bw) / 2f);
                float by = Bounds.Y + (Bounds.Height * 0.5f) - bfs;
                paint.AddRect(
                    bounds: new Rect(
                        x: bx - 14f,
                        y: by,
                        width: bw + 28f,
                        height: bfs + 14f
                    ),
                    color: _theme.OverlayBackground,
                    radius: 6f
                );
                paint.AddText(
                    text: badge,
                    baselineX: bx,
                    baselineY: by + bfs + 2f,
                    color: _theme.Warning,
                    fontSize: bfs,
                    fontWeight: FontWeight.Bold
                );
            }
        }

        // ── Selected node info (top-left) ─────────────────────────────────────
        if (_state.Selected is { } sel)
        {
            string text = _selOverlayText.Update($"{sel.Name}  ({KindNames[(int)sel.Kind]})");
            float tw = text.Length * fs * 0.56f;
            paint.AddRect(
                bounds: new Rect(
                    x: Bounds.X + 6f,
                    y: Bounds.Y + 6f,
                    width: tw + 10f,
                    height: fs + 6f
                ),
                color: _theme.OverlayBackground,
                radius: 4f
            );
            paint.AddText(
                text: text,
                baselineX: Bounds.X + 11f,
                baselineY: Bounds.Y + fs + 7f,
                color: _theme.Primary,
                fontSize: fs
            );
        }

        // ── Transform tool-rail (left) — replaces the old text mode badge ─────
        if (!_state.IsPlaying)
        {
            DrawToolRail(paint);
            DrawCameraModeSwitch(paint);
            if (_cameraMode == CameraNavigationMode.Fly)
            {
                float cx = Bounds.X + (Bounds.Width * 0.5f);
                float cy = Bounds.Y + (Bounds.Height * 0.5f);
                var reticle = _theme.OnSurface.WithAlpha(0.55f);
                paint.AddRect(
                    bounds: new Rect(
                        x: cx - 7f,
                        y: cy - 0.5f,
                        width: 14f,
                        height: 1f
                    ),
                    color: reticle
                );
                paint.AddRect(
                    bounds: new Rect(
                        x: cx - 0.5f,
                        y: cy - 7f,
                        width: 1f,
                        height: 14f
                    ),
                    color: reticle
                );
            }
        }

        // ── Control hints (bottom center) ─────────────────────────────────────
        string controls = _state.IsPlaying
            ? _state.IsPaused
                ? "[P] resume  [Stop] to exit  —  simulation paused"
                : "[WASD] move/drive  [Space] handbrake  [RMB] look  [P] pause  [Stop] to exit"
            : _cameraMode == CameraNavigationMode.Fly
                ? Bounds.Width < 760f
                    ? "RMB look  •  WASD fly  •  Q/E height  •  Shift boost"
                    : _flyHintText.Update(
                        $"[RMB] look  [WASD] fly  [Q/E] down/up  [Shift] boost  [wheel] speed ({_flySpeed:F1} m/s)  [C] orbit"
                    )
                : Bounds.Width < 760f
                    ? "RMB orbit  •  Wheel zoom  •  F frame  •  C fly"
                    : _gizmoMode switch {
                        GizmoMode.Rotate =>
                            "[RMB] orbit  [wheel] zoom  [F] frame  [rings] rotate  [T/S] switch  [C] fly",
                        GizmoMode.Scale =>
                            "[RMB] orbit  [wheel] zoom  [F] frame  [axes] scale  [T/R] switch  [C] fly",
                        _ =>
                            "[RMB] orbit  [wheel] zoom  [F] frame selection  [T/R/S] gizmo  [C] fly",
                    };
        float ctrlW = controls.Length * fs * 0.56f;
        paint.AddRect(
            bounds: new Rect(
                x: Bounds.X + ((Bounds.Width - ctrlW - 10f) / 2f),
                y: Bounds.Bottom - fs - 12f,
                width: ctrlW + 10f,
                height: fs + 6f
            ),
            color: _theme.OverlayBackground,
            radius: 4f
        );
        paint.AddText(
            text: controls,
            baselineX: Bounds.X + ((Bounds.Width - ctrlW) / 2f),
            baselineY: Bounds.Bottom - 8f,
            color: _theme.Hint,
            fontSize: fs
        );

        // ── Game HUD (widget tree + immediate-mode overlay emitted by play-mode scripts) ────
        if (_state.IsPlaying) DrawGameHud(paint);
        else if (_hudWrapper is not null)
            SyncHudWidget(); // tear down a leftover HUD after play stops (Root is null)

        // Scene-transition fade (Scenes.Load with a fade) — covers the frame, HUD included.
        if (_state.ActivePlay is { ScreenFadeAlpha: > 0f } fadingPlay)
            paint.AddRect(bounds: Bounds, color: Color.Black.WithAlpha(fadingPlay.ScreenFadeAlpha));

        // ── No camera watermark ───────────────────────────────────────────────
        if (!hasReal3D)
        {
            string wm = "3D Viewport — no active camera in scene";
            float wmW = wm.Length * (fs + 2f) * 0.55f;
            paint.AddText(
                text: wm,
                baselineX: Bounds.X + ((Bounds.Width - wmW) * 0.5f),
                baselineY: Bounds.Y + (Bounds.Height * 0.5f) - 8f,
                color: _theme.Hint.WithAlpha(0.4f),
                fontSize: fs + 2f
            );
        }
    }

    /// <summary>
    ///     Host the game's <see cref="Zigote.Scripting.Hud.Root" /> widget tree over the viewport in play
    ///     mode:
    ///     measure it tight to the viewport rect, lay it out at the viewport origin, and paint it. The
    ///     retained
    ///     wrapper (built in <see cref="SyncHudWidget" />) supplies an ambient theme + a viewport-sized
    ///     <c>MediaQuery</c> during Measure, so theme-/media-aware HUD widgets resolve them. Input is
    ///     routed in
    ///     <see cref="HitTest" />. The editor has no idea what the HUD means — it just lays out and paints
    ///     whatever the game published.
    /// </summary>
    private void DrawGameHud(PaintList paint)
    {
        SyncHudWidget();
        if (_hudWrapper is null) return;

        float w = MathF.Max(x: 1f, y: Bounds.Width);
        float h = MathF.Max(x: 1f, y: Bounds.Height);
        _hudWrapper.Measure(Constraints.Tight(width: w, height: h));
        _hudWrapper.Layout(new Offset(x: Bounds.X, y: Bounds.Y));
        _hudWrapper.Paint(paint);
    }

    /// <summary>
    ///     Reconcile the hosted HUD widget tree with the game's current <see cref="Hud.Root" />. Rebuilds
    ///     the
    ///     theme/media wrapper (and re-attaches it to the editor App) only when the game swaps the root
    ///     instance; otherwise it just refreshes the viewport-sized media data (a no-op when the size is
    ///     unchanged). A null root tears the wrapper down.
    /// </summary>
    private void SyncHudWidget()
    {
        var src = Hud.Root;
        if (!ReferenceEquals(objA: src, objB: _hudSource))
        {
            _hudWrapper?.Detach();
            _hudSource = src;
            if (src is null)
            {
                _hudWrapper = null;
                _hudMedia = null;
            }
            else
            {
                _hudMedia = new MediaQuery(data: ViewportMedia(), child: src);
                _hudWrapper = new ThemeProvider(data: _theme, child: _hudMedia);
                if (Owner is not null) _hudWrapper.Attach(owner: Owner, parent: this);
            }
        }

        if (_hudMedia is not null)
            _hudMedia.Data = ViewportMedia(); // cheap: early-outs when size unchanged
    }

    private MediaQueryData ViewportMedia()
    {
        float scale = Owner?.Engine.Scale ?? 1f;
        return new MediaQueryData(
            width: MathF.Max(x: 1f, y: Bounds.Width),
            height: MathF.Max(x: 1f, y: Bounds.Height),
            devicePixelRatio: scale
        );
    }

    private enum GizmoMode
    {
        Translate,
        Rotate,
        Scale,
    }

    private enum CameraNavigationMode
    {
        Orbit,
        Fly,

        /// <summary>Orthographic front view of the XY plane — see ViewportPanel.TwoD.cs.</summary>
        TwoD,
    }
}
