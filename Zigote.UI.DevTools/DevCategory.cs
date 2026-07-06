namespace Zigote.UI.DevTools;

/// <summary>
///     Top-level grouping for devtools panels. The panel strip splits into three tabs so the 2D/UI
///     tooling, the 3D/renderer tooling, and the engine-wide tooling stay separated rather than piling
///     into one flat list. Which tabs are actually shown depends on the host's
///     <see cref="DevToolsProfile" />: a 2D app hides the <see cref="Render3D" /> tab entirely, a 3D app
///     shows all three.
/// </summary>
public enum DevCategory
{
    /// <summary>Engine-wide: overview, profiler, memory, GPU, logs, console, variables.</summary>
    Generic,

    /// <summary>2D / UI: widget tree, layout bounds, paint-command stats, repaint rainbow.</summary>
    Ui2D,

    /// <summary>3D / renderer: pipeline counters, debug views, feature toggles.</summary>
    Render3D,
}

public static class DevCategoryExtensions
{
    public static string Label(this DevCategory c)
    {
        return c switch {
            DevCategory.Generic => "General",
            DevCategory.Ui2D => "2D · UI",
            DevCategory.Render3D => "3D · Render",
            _ => c.ToString(),
        };
    }
}
