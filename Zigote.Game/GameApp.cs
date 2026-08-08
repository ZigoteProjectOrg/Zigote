using Zigote.Core.Physics;
using Zigote.Core.Rendering;
using Zigote.Game.Scene;
using Zigote.UI.Host;

namespace Zigote.Game;

/// <summary>
///     Base class for games. Extends <see cref="App" /> with a fixed-timestep game loop:
///     <see cref="OnFixedStep" /> and the physics simulation advance in constant
///     <see cref="FixedDt" /> slices regardless of the render frame rate — the render dt only
///     decides how many slices run each frame, the remainder carries to the next frame, and the
///     backlog is capped so a long stall can't trigger a spiral of death. <see cref="OnUpdate" />
///     still runs once per render frame at the raw delta time (view work, not simulation).
///     UiApp already owns DeltaTime and Time.
/// </summary>
public abstract class GameApp(string title, uint width = 1280, uint height = 720)
    // A game renders a 3D scene every frame, so it wants the fastest GPU on a multi-GPU machine —
    // unlike a plain UI App, which defaults to the power-efficient one.
    : App(
        title,
        width,
        height,
        gpuPreference: GpuPowerPreference.Performance
    )
{
    /// <summary>Fixed gameplay + physics tick — matches the play session's 120 Hz.</summary>
    public const float FixedDt = 1f / 120f;

    // Simulate at most 250 ms of backlog in a single render frame (alt-tab, a breakpoint).
    private const float MaxCatchUp = 0.25f;

    private float _accumulator;

    /// <summary>
    ///     Target frames per second. 0 (the default) follows the monitor the window is on. An
    ///     explicit value can only slow the loop below the panel's refresh, never past it — see
    ///     <see cref="App.FrameIntervalTicks" />, which does the actual pacing at the end of every
    ///     <see cref="App.Frame" />.
    /// </summary>
    public int TargetFps
    {
        get => FrameRateLimit;
        set => FrameRateLimit = value;
    }

    /// <summary>The main 3D world (C# scene graph with typed transforms and components).</summary>
    public World World { get; } = new();

    /// <summary>
    ///     The JoltPhysics simulation. <see cref="Run" /> initializes it before <see cref="OnStart" />
    ///     and steps it in fixed <see cref="FixedDt" /> slices each frame after <see cref="OnUpdate" />.
    ///     Attach bodies via <c>node.RigidBody = new RigidBody3D { … }</c> inside <see cref="OnStart" />.
    /// </summary>
    public PhysicsWorld Physics { get; } = new();

    /// <summary>
    ///     Fraction of a fixed tick left un-simulated after this frame's slices, in [0, 1):
    ///     blend between the last two ticks' states by this amount to smooth rendering when the
    ///     frame rate beats the fixed tick rate.
    /// </summary>
    public float InterpolationAlpha { get; private set; }

    /// <summary>Called once before the game loop begins.</summary>
    protected virtual void OnStart()
    {
    }

    /// <summary>Called once per render frame with the raw delta time — view work, not simulation.</summary>
    protected virtual void OnUpdate(float dt)
    {
    }

    /// <summary>
    ///     Called once per fixed tick with the constant <see cref="FixedDt" />, right before the
    ///     physics step of the same slice — frame-rate-independent gameplay (forces, movement)
    ///     belongs here.
    /// </summary>
    protected virtual void OnFixedStep(float fixedDt)
    {
    }

    /// <summary>Run until the window is closed.</summary>
    public void Run()
    {
        Physics.Initialize(Engine.Handle);
        OnStart();
        World.AttachPhysics(Physics);

        while (!ShouldQuit)
        {
            Frame(); // UiApp.Frame() updates DeltaTime internally — and paces the frame on the way out
            OnUpdate(DeltaTime);

            _accumulator = MathF.Min(_accumulator + DeltaTime, MaxCatchUp);
            while (_accumulator >= FixedDt)
            {
                OnFixedStep(FixedDt);
                Physics.Step(FixedDt);
                _accumulator -= FixedDt;
            }

            InterpolationAlpha = _accumulator / FixedDt;

            World.SyncFromPhysics(Physics);
            World.Update(DeltaTime); // update transforms, clear per-frame input deltas
            World.Sync(); // push world transforms to Zig renderer
        }

        Physics.Dispose();
    }
}