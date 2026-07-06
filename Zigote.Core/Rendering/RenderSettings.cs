namespace Zigote.Core.Rendering;

/// <summary>
///     Controls which render graph features are active each frame.
///     Pass to <see cref="Engine.ZigoteEngine.SetRenderSettings" /> to update.
/// </summary>
public sealed class RenderSettings
{
    /// <summary>Enable Liquid Glass / backdrop sampling effects.</summary>
    public bool EnableGlassEffects { get; set; } = true;

    /// <summary>Enable the debug overlay pass (layout bounds, stats).</summary>
    public bool EnableDebugOverlays { get; set; } = false;

    /// <summary>Enable 3D scene rendering when scene dimensions are provided.</summary>
    public bool Enable3DScene { get; set; } = true;
}