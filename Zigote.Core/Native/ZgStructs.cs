using System.Runtime.InteropServices;
using System.Text;

namespace Zigote.Core.Native;

/// <summary>
///     Discriminant values for <see cref="ZgPaintCommand.Kind" />.
///     Must match the CMD_* constants in src/ffi/root.zig.
/// </summary>
public enum PaintCommandKind : byte
{
    Rect = 0,
    Border = 1,
    Text = 2,
    Image = 3,
    ClipStart = 4,
    ClipEnd = 5,
    PushOpacity = 6,
    PopOpacity = 7,
    Shadow = 8,
    LiquidGlass = 9,
    ShaderEffect = 10,
    TextLayout = 11,
    GlyphRun = 12,
    RenderTextureBegin = 13,
    RenderTextureEnd = 14,
    Blur = 15,
    Bezier = 16,
    Polygon = 17,
    TransformPush = 18,
    TransformPop = 19,
}

/// <summary>
///     Discriminant values for <see cref="ZgEvent.Kind" />.
///     Must match the EVT_* constants in src/ffi/root.zig.
/// </summary>
public enum EventKind : byte
{
    MouseMove = 0,
    MouseDown = 1,
    MouseUp = 2,
    Scroll = 3,
    KeyDown = 4,
    KeyUp = 5,
    Quit = 6,
    Resize = 7,
    TextInput = 8,
    TextEditing = 9,
    WindowFocus = 10,
    WindowClose = 11,
    SystemTheme = 12,
    DropBegin = 13,
    DropFile = 14,
    DropText = 15,
    DropPosition = 16,
    DropComplete = 17,
    TouchDown = 18,
    TouchMove = 19,
    TouchUp = 20,
    TouchCancel = 21,
    AppBackground = 22,
    AppForeground = 23,
    LowMemory = 24,
    ScreenKeyboardShown = 25,
    ScreenKeyboardHidden = 26,
    DisplayChanged = 27,
}

/// <summary>
///     Typed return code for all FFI functions that can fail.
///     Replaces raw <c>i32</c> 0/-1 returns for type safety across the boundary.
/// </summary>
public enum ZgResult
{
    Ok = 0,
    Err = -1,
}

/// <summary>
///     Modifier key bitmask for <see cref="ZgEvent.Modifiers" />.
///     Must match the MOD_* constants in src/ffi/root.zig.
/// </summary>
[Flags]
public enum ModifierKeys : byte
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
    Cmd = 8, // ⌘ on macOS, Super/Win elsewhere — the platform "command" modifier (MOD_GUI)
}

/// <summary>
///     Flat C-ABI paint command. Layout is explicit to match the Zig extern struct
///     ZgPaintCommand in src/ffi/root.zig. Total size: 112 bytes on 64-bit.
///     Fields are ordered large→small (8-byte pointers first, then 4-byte scalars, then the small
///     ints) so the struct packs with a single 3-byte hole instead of the ~11 padding bytes the old
///     natural order forced — 120→112 B on every command a frame streams. Offsets are pinned by
///     <c>AbiLayoutTests</c> here and by comptime <c>@offsetOf</c> asserts on the Zig side.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 112)]
public unsafe struct ZgPaintCommand
{
    [FieldOffset(0)] public byte Kind;
    [FieldOffset(1)] public byte FontStyle; // 0=normal 1=italic
    [FieldOffset(2)] public ushort FontWeight; // 100–900
    [FieldOffset(4)] public byte HasCacheKey;

    /// <summary>ShaderEffect only: refresh the backdrop capture first, so chained filters see
    /// the previous pass's output instead of sharing its input.</summary>
    [FieldOffset(5)] public byte ChainsBackdrop;

    // [6..7] padding (align pointers to 8 bytes)
    [FieldOffset(8)] public byte* TextPtr;
    [FieldOffset(16)] public byte* PixelsPtr;

    [FieldOffset(24)] public float RectX;
    [FieldOffset(28)] public float RectY;
    [FieldOffset(32)] public float RectW;
    [FieldOffset(36)] public float RectH;
    [FieldOffset(40)] public float ColorR;
    [FieldOffset(44)] public float ColorG;
    [FieldOffset(48)] public float ColorB;
    [FieldOffset(52)] public float ColorA;

    [FieldOffset(56)] public float Radius;
    [FieldOffset(60)] public float BorderWidth;
    [FieldOffset(64)] public float BaselineX;
    [FieldOffset(68)] public float BaselineY;

    // Aliases for Image UVs
    [FieldOffset(56)] public float U0;
    [FieldOffset(60)] public float V0;
    [FieldOffset(64)] public float U1;
    [FieldOffset(68)] public float V1;

    // Alias for ShaderEffect shader id (bit-reinterpreted from Radius)
    [FieldOffset(56)] public uint ShaderId;

    // Aliases for Text shadow — rides in slots Text never uses (rect = color, radius /
    // border width = offset, img_pixel_w = blur). Present iff ShadowA > 0.
    [FieldOffset(24)] public float ShadowR;
    [FieldOffset(28)] public float ShadowG;
    [FieldOffset(32)] public float ShadowB;
    [FieldOffset(36)] public float ShadowA;
    [FieldOffset(56)] public float ShadowOffsetX;
    [FieldOffset(60)] public float ShadowOffsetY;
    [FieldOffset(88)] public float ShadowBlur;

    [FieldOffset(72)] public float FontSize;
    [FieldOffset(76)] public float LineHeight;
    [FieldOffset(80)] public float LetterSpacing;
    [FieldOffset(84)] public float WordSpacing;

    [FieldOffset(88)] public uint ImgPixelW;
    [FieldOffset(92)] public uint ImgPixelH;

    [FieldOffset(96)] public uint CacheKeyLo;
    [FieldOffset(100)] public uint CacheKeyHi;
    [FieldOffset(104)] public uint TextLen;
    [FieldOffset(108)] public uint PixelsLen;
}

/// <summary>
///     Flat C-ABI input event. Layout is explicit to match ZgEvent in src/ffi/root.zig.
///     Total size: 44 bytes. The text_input / text_editing UTF-8 payload lives OUT OF BAND in the
///     engine's per-poll text buffer (see <see cref="ZigoteEngine.PollEventsInto" />): this event
///     carries only <see cref="TextOff" />/<see cref="TextLen" /> into it, so the common flood of
///     mouse/key events costs 44 B instead of 288 B. The out-of-band buffer is unbounded, so IME
///     pre-edit strings are never truncated.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 44)]
public unsafe struct ZgEvent
{
    [FieldOffset(0)] public byte Kind; // EventKind

    [FieldOffset(1)]
    public byte Button; // mouse: 0=left 1=right 2=middle; key events: 1 = auto-repeat

    [FieldOffset(2)] public byte Modifiers; // ModifierKeys bitmask
    [FieldOffset(3)] public byte KeyChar; // ASCII char, 0 if not printable
    [FieldOffset(4)] public uint KeyScancode;
    [FieldOffset(8)] public float X;
    [FieldOffset(12)] public float Y;
    [FieldOffset(16)] public float ScrollX;
    [FieldOffset(20)] public float ScrollY;
    [FieldOffset(24)] public uint ResizeW;

    [FieldOffset(28)] public uint ResizeH;

    // Aliases used by text_editing events (UTF-8 byte offsets from SDL).
    [FieldOffset(24)] public uint CompositionStart;
    [FieldOffset(28)] public uint CompositionLength;

    // Aliases used by touch events (EVT_TOUCH_*): the finger slot (a compact per-contact id,
    // 0..9, stable while the finger stays down) rides the key-only KeyScancode field, and
    // pressure (0..1) rides the wheel-only ScrollX field. X/Y are the position in the same
    // window-coordinate space as mouse events.
    [FieldOffset(4)] public uint TouchFinger;
    [FieldOffset(16)] public float TouchPressure;

    // text_input / text_editing: byte range of the payload in the poll text buffer.
    [FieldOffset(32)] public uint TextOff;
    [FieldOffset(36)] public uint TextLen;

    /// <summary>SDL window id the event belongs to; 0 = unknown → treated as the main window.</summary>
    [FieldOffset(40)] public uint WindowId;

    /// <summary>
    ///     Decode this event's text payload from the poll text buffer base pointer
    ///     (<c>zigote_poll_text_ptr</c>). Empty when the event carries no text.
    /// </summary>
    public string GetTextInput(byte* textBase)
    {
        if (textBase is null || TextLen == 0) return string.Empty;
        return Encoding.UTF8.GetString(bytes: textBase + TextOff, byteCount: (int)TextLen);
    }
}

/// <summary>
///     Result of <see cref="NativeEngine.MeasureText" />.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZgSize
{
    public float Width;
    public float Height;
}

/// <summary>
///     One glyph quad for <c>PaintCommandKind.GlyphRun</c>.
///     Screen rect in logical pixels; atlas UVs in [0,1].
///     8 × 4 bytes = 32 bytes total. Must match <c>GlyphRunQuad</c> in ffi/root.zig.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct ZgGlyphQuad
{
    public float X, Y, W, H; // screen position (logical pixels)
    public float U0, V0, U1, V1; // atlas UV [0..1]
}

/// <summary>
///     ABI compatibility info returned by <c>zigote_get_renderer_abi_info</c>.
///     Mirrors <c>ZgAbiInfo</c> in src/ffi/root.zig. Total size: 20 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 20)]
public struct ZgAbiInfo
{
    public uint AbiVersion; // offset  0
    public uint PaintCommandSize; // offset  4
    public uint EventSize; // offset  8
    public uint HandleSize; // offset 12
    public uint RenderSettings3DSize; // offset 16 — must equal sizeof(ZgRenderSettings3D)
}

/// <summary>
///     Runtime renderer capabilities returned by <c>zigote_get_renderer_caps</c> (called after
///     <c>zigote_init</c>). Reports the backend actually selected plus optional native features
///     (vendor upscalers / hardware ray tracing). Mirrors <c>ZgRendererCaps</c> in
///     src/ffi/root.zig (and <c>backend.Caps</c>). Total size: 12 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 12)]
public struct ZgRendererCaps
{
    public uint ActiveBackend; // offset 0 — RenderBackend actually in use
    public uint Upscalers; // offset 4 — UpscalerKinds bitset (0 = none)
    public byte RayTracing; // offset 8 — hardware ray tracing available

    public byte RayTracingFromRender; // offset 9 — RT usable from fragment shaders
    // 2 bytes padding to 12 (matches Zig extern struct).
}

/// <summary>
///     One GPU the engine enumerated at init, returned by <c>zigote_enumerate_gpus</c>. Mirrors
///     <c>GpuInfo</c> in src/renderer/gpu_select.zig. Total size: 144 bytes.
///     <para>
///         The same physical GPU appears once per graphics API the instance was built with (a
///         Windows machine typically lists each card under both D3D12 and Vulkan), so
///         <see cref="Backend" /> is part of a GPU's identity, not just a detail about it.
///     </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 144)]
public unsafe struct ZgGpuInfo
{
    public fixed byte Name[128]; // offset 0   — UTF-8, NUL-padded
    public uint Backend; // offset 128 — ZgGpuBackend
    public uint DeviceType; // offset 132 — ZgGpuDeviceType
    public uint VendorId; // offset 136
    public uint DeviceId; // offset 140
}

/// <summary>Graphics API an adapter drives. Mirrors wgpu's <c>BackendType</c>.</summary>
public enum ZgGpuBackend : uint
{
    Undefined = 0,
    Null = 1,
    WebGpu = 2,
    D3D11 = 3,
    D3D12 = 4,
    Metal = 5,
    Vulkan = 6,
    OpenGl = 7,
    OpenGlEs = 8,
}

/// <summary>Physical kind of an adapter. Mirrors wgpu's <c>AdapterType</c>.</summary>
public enum ZgGpuDeviceType : uint
{
    DiscreteGpu = 1,
    IntegratedGpu = 2,
    Cpu = 3,
    Unknown = 4,
}

/// <summary>
///     Tunable 3D render settings exposed to the editor's Settings tab.
///     Mirrors <c>ZgRenderSettings3D</c> in src/ffi/root.zig (70 floats; colours are linear
///     rgb, sun angles in degrees). Field order MUST match the Zig struct exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZgRenderSettings3D
{
    public float AmbientIntensity;
    public float SkyHorizonR, SkyHorizonG, SkyHorizonB;
    public float SkyZenithR, SkyZenithG, SkyZenithB;
    public float SkyGroundR, SkyGroundG, SkyGroundB;
    public float EnvAvgR, EnvAvgG, EnvAvgB;
    public float SunAzimuthDeg, SunElevationDeg, SunIntensity;
    public float Overhead, HorizonGlow, SunSharpness;
    public float Exposure, Contrast, Saturation;
    public float ShadowStrength, ShadowBias, ShadowSoftness;
    public float Clearcoat;
    public float BloomThreshold, BloomKnee, BloomIntensity;
    public float SsaoRadius, SsaoBias, SsaoStrength, SsaoPower;
    public float SsrIntensity, SsrMaxDistance, SsrThickness, SsrSteps;
    public float TaaEnabled, TaaFeedback;

    /// <summary>
    ///     Renderer Diagnostic Mode (0/1): forces a stable PBR baseline — all stylised post
    ///     effects off, neutral grade, low ambient, single directional light. For inspecting bugs.
    /// </summary>
    public float DiagnosticMode;

    /// <summary>Active debug visualisation channel (see <see cref="DebugView" />); 0 = normal output.</summary>
    public float DebugView;

    // ── Depth of field (gather-bokeh). Centre-spot autofocus tracks the subject;
    //    FocusDistance is the fallback when the screen centre is background. ──

    /// <summary>Depth of field master enable (0/1).</summary>
    public float DofEnabled;

    /// <summary>Fallback focus distance in view-space units (used when autofocus hits background).</summary>
    public float DofFocusDistance;

    /// <summary>Aperture f-stop — lower = shallower depth of field / bigger bokeh.</summary>
    public float DofFStop;

    /// <summary>Max circle-of-confusion (blur radius) in pixels — clamps the bokeh size.</summary>
    public float DofMaxCoc;

    /// <summary>
    ///     Wireframe render debug mode (0/1): draws all 3D geometry as flat line edges via a
    ///     dedicated line-list pipeline. wgpu reference renderer only (Metal ignores it today).
    /// </summary>
    public float Wireframe;

    /// <summary>Atmospheric fog density (0 = off). Height-based exponential fog + analytic sun in-scatter.</summary>
    public float FogDensity;

    /// <summary>
    ///     Fog / aerial-perspective base colour (linear rgb). Default ≈ horizon so distant geometry
    ///     fades into the sky.
    /// </summary>
    public float FogColorR, FogColorG, FogColorB;

    /// <summary>World Y where fog is densest.</summary>
    public float FogHeight;

    /// <summary>How fast fog density decays with height (0 = uniform).</summary>
    public float FogHeightFalloff;

    /// <summary>Sun in-scatter strength — brightens fog toward the sun (god-ray glow).</summary>
    public float FogSunInscatter;

    /// <summary>
    ///     Henyey-Greenstein anisotropy g for the sun in-scatter (0 = isotropic, →1 =
    ///     forward/sun-hugging).
    /// </summary>
    public float FogAnisotropy;

    /// <summary>
    ///     Auto-exposure / eye adaptation on (1) or off (0). Maps the frame's average luminance to
    ///     middle grey.
    /// </summary>
    public float AutoExposureEnabled;

    /// <summary>Auto-exposure key value (target middle grey, ~0.18).</summary>
    public float AutoExposureKey;

    /// <summary>Darkest average luminance the eye adapts to (caps how far it brightens dark scenes).</summary>
    public float AutoExposureMin;

    /// <summary>Brightest average luminance (caps how far it darkens bright scenes).</summary>
    public float AutoExposureMax;

    /// <summary>Per-frame adaptation blend (higher = snappier eye adaptation).</summary>
    public float AutoExposureSpeed;

    // ── Photographic grade (post-AgX look). Exposed so film-stock emulation and the physical camera can
    //    drive them; previously baked as Zig defaults, invisible to C#. Consumed by the tonemap shader. ──

    /// <summary>AgX post-look: 0 = Default (neutral AgX), 1 = Punchy, 2 = Golden.</summary>
    public float AgxLook;

    /// <summary>White balance temperature (linear, pre-AgX): &gt;0 warms (boost R, cut B).</summary>
    public float WbTemperature;

    /// <summary>White balance tint: &gt;0 shifts toward magenta.</summary>
    public float WbTint;

    /// <summary>Vignette darkening strength at the frame edges (0 = off).</summary>
    public float VignetteStrength;

    /// <summary>Vignette falloff softness.</summary>
    public float VignetteSoftness;

    /// <summary>Film-grain amount (added in LDR, animated, luma-modulated).</summary>
    public float GrainAmount;

    /// <summary>Chromatic aberration at frame edges (per-channel UV split, scales with radius²).</summary>
    public float ChromaticAberration;

    // ── Lens optics (physical-camera native effects). Radial distortion is applied as a UV remap in the
    //    tonemap pass: r' = r·(1 + k1·r² + k2·r⁴). k1<0 = barrel, k1>0 = pincushion. 0 = off. ──

    /// <summary>Radial lens distortion k1 (&lt;0 barrel, &gt;0 pincushion, 0 = off).</summary>
    public float LensDistortionK1;

    /// <summary>Radial lens distortion k2 (higher-order term; 0 = off).</summary>
    public float LensDistortionK2;

    // ── Aperture bokeh shape (extends the DoF gather). ──

    /// <summary>Aperture blade count: 0 (or &lt;3) = circular bokeh; 5..9 = polygonal.</summary>
    public float BokehBlades;

    /// <summary>Anamorphic squeeze: 1 = round bokeh; &gt;1 = vertically-stretched oval (anamorphic look).</summary>
    public float BokehAnamorphic;
}

/// <summary>
///     Renderer debug visualisation channels. Mirrors <c>DebugView</c> in
///     src/renderer/wgpu_3d.zig — keep the numeric values in sync.
/// </summary>
public enum DebugView
{
    None = 0,
    BaseColor = 1,
    WorldNormal = 2,
    ViewNormal = 3,
    Roughness = 4,
    Metallic = 5,
    Alpha = 6,
    Emissive = 7,
    Depth = 8,
    ViewPosition = 9,
    ShadowFactor = 10,
    AmbientOcclusion = 11,
    SsrContribution = 12,
    SsrHitMiss = 13,
    Bloom = 14,
    HdrLuminance = 15,
}

/// <summary>
///     One entry for the parallel batch texture loader (<c>zigote_scene_load_textures_batch</c>).
///     The path fields are pointers to UTF-8, null-terminated strings (or <see cref="IntPtr.Zero" />
///     to skip that map). Layout MUST match <c>ZgTextureLoadItem</c> in src/ffi/root.zig.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZgTextureLoadItem
{
    public ulong NodeHandle;
    public IntPtr BaseColorPath;
    public IntPtr MrPath;
    public IntPtr NormalPath;
    public IntPtr EmissivePath;
}

/// <summary>
///     Per-frame engine statistics for the debug overlay/profiler. Mirrors <c>ZgEngineStats</c> in
///     src/ffi/root.zig — field order MUST match.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZgEngineStats
{
    public ulong FrameIndex;
    public uint DrawCalls;
    public uint Triangles;
    public uint RenderPasses;
    public uint VisibleObjects;
    public ulong GpuBufferMemory;
    public ulong GpuTextureMemory;
}
