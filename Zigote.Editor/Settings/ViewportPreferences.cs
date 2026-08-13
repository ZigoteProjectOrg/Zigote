using Zigote.Preferences;

namespace Zigote.Editor.Settings;

/// <summary>
///     Per-project viewport preferences (keys <c>viewport.*</c>): the debug-viz toggles the debug
///     console exposes as <c>render.*</c> variables, plus gizmo snapping. The console variables
///     write these preferences; session bindings in Program.cs mirror them into the fast
///     <c>EditorState</c> flags the viewport reads per paint and apply each toggle's side effects
///     (particle clears, viewport invalidation).
/// </summary>
public sealed class ViewportPreferences : PreferencesProvider
{
    public ViewportPreferences(PreferenceStore store) : base(store, "viewport")
    {
        PhysicsWireframe = Register("physicsWireframe", false);
        StreamDistance = Register("streamDistance", 0f);
        NativeVfx = Register("nativeVfx", false);
        GpuVfx = Register("gpuVfx", false);
        AnimateEditVfx = Register("animateEditVfx", false);
        SnapGrid = Register("snapGrid", 0f);
    }

    /// <summary>Draw physics collision shapes as a wireframe overlay (edit + play mode).</summary>
    public Preference<bool> PhysicsWireframe { get; }

    /// <summary>Demand-stream .zmesh meshes within this camera distance; 0 = off (all resident).</summary>
    public Preference<float> StreamDistance { get; }

    /// <summary>Render VFX particles with the native GPU billboard pass.</summary>
    public Preference<bool> NativeVfx { get; }

    /// <summary>Simulate VFX particles on the GPU (compute) instead of the CPU.</summary>
    public Preference<bool> GpuVfx { get; }

    /// <summary>Animate VFX emitters live in edit mode (off = static preview).</summary>
    public Preference<bool> AnimateEditVfx { get; }

    /// <summary>Gizmo drag snapping increment in world units; 0 = free drag (Shift still snaps).</summary>
    public Preference<float> SnapGrid { get; }
}
