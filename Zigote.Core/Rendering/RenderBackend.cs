namespace Zigote.Core.Rendering;

/// <summary>
///     Which GPU backend the native engine drives. Chosen ONCE at
///     <see cref="Engine.ZigoteEngine.Initialize" /> — the GPU device is created once and never
///     recreated, so switching backends requires an app relaunch.
///     Mirrors <c>BackendId</c> in <c>src/renderer/backend.zig</c> (values must match).
/// </summary>
/// <remarks>
///     wgpu is the portable default and only implemented backend today. The reserved native-backend
///     values exist to reach vendor features wgpu does not expose — hardware ray tracing and vendor
///     upscalers (DLSS / FSR / XeSS) — when a Vulkan/D3D12 backend lands. Requesting an unimplemented
///     backend degrades gracefully to wgpu; query <see cref="RendererCaps" /> after init to see what
///     was actually selected.
/// </remarks>
public enum RenderBackend : uint
{
    /// <summary>Best available for the platform/hardware (currently resolves to wgpu).</summary>
    Auto = 0,

    /// <summary>
    ///     wgpu-native — portable default and only implemented backend (Metal/Vulkan/D3D12/GL under
    ///     the hood).
    /// </summary>
    Wgpu = 1,

    /// <summary>Vulkan (Linux/Windows) — DLSS/FSR/XeSS + KHR ray tracing. (planned, reserved)</summary>
    Vulkan = 3,

    /// <summary>Direct3D 12 (Windows) — DLSS/FSR/XeSS + DXR. (planned, reserved)</summary>
    D3D12 = 4,
}