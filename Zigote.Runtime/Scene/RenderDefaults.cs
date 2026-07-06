using Zigote.Core.Native;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Canonical default 3D render settings for the editor — the single C# source of truth, kept in
///     sync with the native <c>Settings3D</c> struct defaults (wgpu_3d.zig / metal_backend.zig). Used
///     by the Settings panel's "Reset" affordances and as the fallback applied when a project has no
///     persisted render settings, so opening such a project resets the engine to defaults rather than
///     inheriting the previously-open project's look.
/// </summary>
public static class RenderDefaults
{
    public static ZgRenderSettings3D Settings3D()
    {
        return new ZgRenderSettings3D {
            // Punchy/warm/contrasty grade + lower ambient for directional form. Depth of field defaults OFF.
            AmbientIntensity = 0.6f,
            SkyHorizonR = 0.34f,
            SkyHorizonG = 0.30f,
            SkyHorizonB = 0.26f,
            SkyZenithR = 0.66f,
            SkyZenithG = 0.64f,
            SkyZenithB = 0.56f,
            SkyGroundR = 0.26f,
            SkyGroundG = 0.25f,
            SkyGroundB = 0.23f,
            EnvAvgR = 0.38f,
            EnvAvgG = 0.42f,
            EnvAvgB = 0.50f,
            SunAzimuthDeg = 48f,
            SunElevationDeg = 50f,
            SunIntensity = 6f,
            Overhead = 3.2f,
            HorizonGlow = 0.95f,
            SunSharpness = 150f,
            Exposure = 1.10f,
            Contrast = 0.34f,
            Saturation = 1.20f,
            ShadowStrength = 0.55f,
            ShadowBias = 0.006f,
            ShadowSoftness = 1.5f,
            Clearcoat = 1f,
            BloomThreshold = 0.7f,
            BloomKnee = 0.4f,
            BloomIntensity = 0.45f,
            SsaoRadius = 0.35f,
            SsaoBias = 0.03f,
            SsaoStrength = 0.5f,
            SsaoPower = 1.0f,
            SsrIntensity = 0.5f,
            SsrMaxDistance = 8f,
            SsrThickness = 0.6f,
            SsrSteps = 32f,
            TaaEnabled = 1f,
            TaaFeedback = 0.9f,
            DofEnabled = 0f,
            DofFocusDistance = 8f,
            DofFStop = 2.8f,
            DofMaxCoc = 18f,
            // Atmospheric fog off by default; sensible colour/params so enabling density looks right.
            FogDensity = 0f,
            FogColorR = 0.55f,
            FogColorG = 0.60f,
            FogColorB = 0.68f,
            FogHeight = 0f,
            FogHeightFalloff = 0.15f,
            FogSunInscatter = 0.6f,
            FogAnisotropy = 0.72f,
            // Auto-exposure off by default; sensible metering range when enabled.
            AutoExposureEnabled = 0f,
            AutoExposureKey = 0.18f,
            AutoExposureMin = 0.03f,
            AutoExposureMax = 8f,
            AutoExposureSpeed = 0.08f,
            // Photographic grade — matches the former baked Zig defaults (Punchy look, slight warm WB).
            AgxLook = 1f,
            WbTemperature = 0.10f,
            WbTint = 0f,
            VignetteStrength = 0.18f,
            VignetteSoftness = 0.55f,
            GrainAmount = 0.015f,
            ChromaticAberration = 0.0015f,
            // Lens distortion off + circular bokeh by default (physical camera drives them).
            LensDistortionK1 = 0f,
            LensDistortionK2 = 0f,
            BokehBlades = 0f,
            BokehAnamorphic = 1f,
        };
    }
}