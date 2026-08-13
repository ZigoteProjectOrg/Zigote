using Zigote.UI.Debug;

namespace Zigote.UI.DevTools;

/// <summary>
///     Declares which kind of application the devtools overlay is running inside, so it can show only
///     the relevant debug layers. A 2D app has no renderer pipeline to inspect, so its
///     <see cref="DevCategory.Render3D" /> tab is hidden and the General tab surfaces the CPU / memory /
///     GPU metrics prominently; a 3D app shows the 2D and 3D layers side by side.
/// </summary>
public enum DevToolsProfile
{
    /// <summary>
    ///     Resolve automatically: 3D once the native renderer has produced a 3D frame, otherwise 2D.
    ///     Re-evaluated whenever the panel opens, so a host that starts 2D and later renders 3D upgrades.
    /// </summary>
    Auto,

    /// <summary>Pure 2D / UI app: General + 2D·UI tabs only (no renderer pipeline).</summary>
    TwoD,

    /// <summary>3D app: General + 2D·UI + 3D·Render tabs.</summary>
    ThreeD,
}

public static class DevToolsProfileExtensions
{
    /// <summary>Resolve <see cref="DevToolsProfile.Auto" /> against the live engine state.</summary>
    public static DevToolsProfile Resolve(this DevToolsProfile profile)
    {
        if (profile != DevToolsProfile.Auto) return profile;
        // The renderer only advances FrameIndex once it has drawn a 3D frame; a UI-only app leaves it
        // at 0. EngineOk guards the headless case (no native engine → treat as 2D).
        return DebugStats.EngineOk && DebugStats.Engine.FrameIndex > 0
            ? DevToolsProfile.ThreeD
            : DevToolsProfile.TwoD;
    }

    /// <summary>True when the <see cref="DevCategory.Render3D" /> tab should be visible for this profile.</summary>
    public static bool ShowsRender3D(this DevToolsProfile profile)
    {
        return profile.Resolve() == DevToolsProfile.ThreeD;
    }
}
