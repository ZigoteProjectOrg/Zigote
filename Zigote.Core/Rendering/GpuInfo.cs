using Zigote.Core.Native;

namespace Zigote.Core.Rendering;

/// <summary>
///     Which GPU to prefer on a machine that has more than one. Chosen ONCE at
///     <see cref="Engine.ZigoteEngine.Initialize" /> — the device is created a single time and never
///     recreated, so changing it requires an app restart.
/// </summary>
public enum GpuPowerPreference : uint
{
    /// <summary>No app-level intent; ranked the same as <see cref="Performance" />.</summary>
    Auto = 0,

    /// <summary>
    ///     The fastest GPU available, discrete first. What 3D hosts (editor, player, games) want.
    /// </summary>
    Performance = 1,

    /// <summary>
    ///     The most power-efficient GPU, integrated first. What 2D/UI apps want — on a laptop this
    ///     keeps the discrete card asleep instead of waking it to draw rectangles.
    /// </summary>
    Efficiency = 2,
}

/// <summary>
///     One GPU the engine found at startup. Enumerated from the graphics APIs the current platform
///     build enables, so the same physical card can appear more than once — once per API (a Windows
///     machine usually lists each card under both D3D12 and Vulkan). <see cref="Backend" /> is
///     therefore part of a GPU's identity: picking an entry picks the API too.
/// </summary>
/// <param name="Index">
///     Position in <see cref="Engine.ZigoteEngine.EnumerateGpus" />. This is the value to pass back
///     as the GPU override at init.
/// </param>
/// <param name="Name">Device name as the driver reports it, e.g. "NVIDIA GeForce RTX 4080".</param>
/// <param name="Backend">Graphics API this entry drives.</param>
/// <param name="DeviceType">Discrete, integrated, CPU (software), or unknown.</param>
/// <param name="VendorId">PCI vendor id.</param>
/// <param name="DeviceId">PCI device id.</param>
public readonly record struct GpuInfo(
    int Index,
    string Name,
    ZgGpuBackend Backend,
    ZgGpuDeviceType DeviceType,
    uint VendorId,
    uint DeviceId)
{
    /// <summary>True when this is a separate graphics card rather than a CPU-attached GPU.</summary>
    public bool IsDiscrete => DeviceType == ZgGpuDeviceType.DiscreteGpu;

    /// <summary>
    ///     A label fit for a settings list — the device name plus the API, since the same card can be
    ///     listed under several. e.g. "NVIDIA GeForce RTX 4080 (Vulkan, discrete)".
    /// </summary>
    public string DisplayName
    {
        get
        {
            string kind = DeviceType switch {
                ZgGpuDeviceType.DiscreteGpu => "discrete",
                ZgGpuDeviceType.IntegratedGpu => "integrated",
                ZgGpuDeviceType.Cpu => "software",
                _ => "unknown",
            };
            return $"{Name} ({Backend}, {kind})";
        }
    }
}
