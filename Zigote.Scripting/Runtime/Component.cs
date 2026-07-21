using Zigote.Core.Math3D;

namespace Zigote.Scripting;

/// <summary>
///     Base class for all C# script components. Attach to a scene node via the editor.
///     <para>
///         The ScriptWorld syncs Position/Rotation/Scale from the scene node before each
///         OnUpdate, and writes them back afterward — so transform mutations inside OnUpdate
///         are automatically pushed to the 3D scene.
///     </para>
/// </summary>
public abstract class Component
{
    /// <summary>The unique ID of the scene node this component is attached to.</summary>
    public uint EntityId { get; internal set; }

    /// <summary>Whether this component ticks. Disabled components still receive OnEnable/OnDisable.</summary>
    public bool Enabled { get; set; } = true;

    // ── Transform shortcuts (synced by ScriptWorld) ────────────────────────────

    public Vec3 Position { get; set; }
    public Quat Rotation { get; set; } = Quat.Identity;
    public Vec3 Scale { get; set; } = Vec3.One;

    // ── Lifecycle — override in user scripts ──────────────────────────────────

    protected virtual void OnCreate()
    {
    }

    protected virtual void OnDestroy()
    {
    }

    protected virtual void OnEnable()
    {
    }

    protected virtual void OnDisable()
    {
    }

    /// <summary>
    ///     Per-tick update. Runs on the fixed 120 Hz gameplay tick, not the render frame:
    ///     <paramref name="deltaTime" /> is the constant fixed step, a slow render frame runs
    ///     several ticks back-to-back, and a fast one may run none — render-side smoothing can
    ///     blend by <see cref="Time.InterpolationAlpha" />.
    /// </summary>
    protected virtual void OnUpdate(float deltaTime)
    {
    }

    // ── Internal dispatch (catches exceptions so one bad script can't crash the editor) ─

    internal void CallCreate()
    {
        Dispatch(OnCreate);
    }

    internal void CallDestroy()
    {
        Dispatch(OnDestroy);
    }

    internal void CallEnable()
    {
        Dispatch(OnEnable);
    }

    internal void CallDisable()
    {
        Dispatch(OnDisable);
    }

    // Inlined rather than routed through Dispatch: a dt-capturing lambda would allocate a closure
    // per component per tick. Same policy — log the error, disable the component.
    internal void CallUpdate(float dt)
    {
        try
        {
            OnUpdate(dt);
        }
        catch (Exception ex)
        {
            Enabled = false; // disable to stop repeated errors
            Console.Error.WriteLine(
                $"[Script:{GetType().Name}] Unhandled exception — component disabled.\n{ex}"
            );
        }
    }

    private void Dispatch(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Enabled = false; // disable to stop repeated errors
            Console.Error.WriteLine(
                $"[Script:{GetType().Name}] Unhandled exception — component disabled.\n{ex}"
            );
        }
    }
}