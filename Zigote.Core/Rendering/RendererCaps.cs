using Zigote.Core.Native;

namespace Zigote.Core.Rendering;

/// <summary>
///     Temporal-upscaler families a backend may provide. Bit flags matching
///     <c>UpscalerKind</c> in <c>src/renderer/backend.zig</c> (and the <c>ZgRendererCaps.Upscalers</c>
///     bitset).
/// </summary>
[Flags]
public enum UpscalerKinds : uint
{
    None = 0,
    Dlss = 1 << 1, // NVIDIA DLSS via Streamline (Vulkan/D3D12)
    Fsr = 1 << 2, // AMD FidelityFX Super Resolution (any)
    XeSs = 1 << 3, // Intel XeSS (Vulkan/D3D12)
}

/// <summary>
///     The single upscaler family the user has SELECTED (distinct from <see cref="UpscalerKinds" />,
///     which is the bitset of what is AVAILABLE). Vendor-neutral by design so the future Vulkan/D3D12
///     backends reuse it verbatim for DLSS/FSR/XeSS — each backend maps the selection to its concrete
///     scaler. Spatial-vs-temporal is an internal backend decision (temporal whenever motion vectors
///     exist), never a user setting, so this never needs a per-vendor variant.
/// </summary>
public enum UpscalerSelection : uint
{
    Off = 0,
    Dlss = 2,
    Fsr = 3,
    XeSs = 4,
}

/// <summary>
///     Vendor-neutral upscaler quality preset. Each backend maps it to an internal render scale (and,
///     for DLSS/FSR/XeSS, the matching vendor preset). <see cref="Auto" /> defers to the explicit
///     render-scale slider. Shared across all upscalers so the UI and settings ABI stay
///     backend-agnostic.
/// </summary>
public enum UpscalerQuality : uint
{
    Auto = 0, // use the explicit render-scale value
    Quality = 1, // ~0.77 internal scale
    Balanced = 2, // ~0.67
    Performance = 3, // ~0.58
    UltraPerformance = 4, // ~0.50
}

/// <summary>
///     Runtime capabilities of the active renderer backend, queried from the native engine after
///     <see cref="Engine.ZigoteEngine.Initialize" />. Lets the editor enable/gray-out features
///     (upscaling, ray tracing) based on what the selected backend and hardware actually support.
/// </summary>
/// <param name="ActiveBackend">
///     The backend actually selected (Auto/native may have fallen back to
///     wgpu).
/// </param>
/// <param name="Upscalers">Which vendor upscalers are available.</param>
/// <param name="RayTracing">Hardware ray tracing (acceleration structures + intersection) available.</param>
/// <param name="RayTracingFromRender">
///     RT usable from fragment shaders, not only compute (Apple-silicon
///     class).
/// </param>
public readonly record struct RendererCaps(
    RenderBackend ActiveBackend,
    UpscalerKinds Upscalers,
    bool RayTracing,
    bool RayTracingFromRender)
{
    /// <summary>True if the given upscaler family is available on the active backend.</summary>
    public bool Supports(UpscalerKinds kind)
    {
        return (Upscalers & kind) != 0;
    }

    /// <summary>True if the selected upscaler family is available (Off is always "supported").</summary>
    public bool Supports(UpscalerSelection sel)
    {
        return sel switch {
            UpscalerSelection.Off => true,
            UpscalerSelection.Dlss => Supports(UpscalerKinds.Dlss),
            UpscalerSelection.Fsr => Supports(UpscalerKinds.Fsr),
            UpscalerSelection.XeSs => Supports(UpscalerKinds.XeSs),
            _ => false,
        };
    }

    /// <summary>The available upscaler selections on this backend, always led by <c>Off</c>.</summary>
    public UpscalerSelection[] AvailableUpscalers()
    {
        var list = new List<UpscalerSelection> { UpscalerSelection.Off };
        if (Supports(UpscalerKinds.Dlss)) list.Add(UpscalerSelection.Dlss);
        if (Supports(UpscalerKinds.Fsr)) list.Add(UpscalerSelection.Fsr);
        if (Supports(UpscalerKinds.XeSs)) list.Add(UpscalerSelection.XeSs);
        return list.ToArray();
    }

    internal static RendererCaps From(ZgRendererCaps c)
    {
        return new RendererCaps(
            (RenderBackend)c.ActiveBackend,
            (UpscalerKinds)c.Upscalers,
            c.RayTracing != 0,
            c.RayTracingFromRender != 0
        );
    }
}