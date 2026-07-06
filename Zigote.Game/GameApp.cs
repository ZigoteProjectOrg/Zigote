using System.Diagnostics;
using Zigote.Core.Physics;
using Zigote.Game.Scene;
using Zigote.UI.Host;

namespace Zigote.Game;

/// <summary>
///     Base class for games. Extends <see cref="App" /> with a fixed-rate game loop.
///     UiApp already owns DeltaTime and Time. Override <see cref="OnUpdate" /> for game logic.
/// </summary>
public abstract class GameApp(string title, uint width = 1280, uint height = 720)
    : App(title, width, height)
{
    /// <summary>Target frames per second. 0 = uncapped.</summary>
    public int TargetFps { get; set; } = 60;

    /// <summary>The main 3D world (C# scene graph with typed transforms and components).</summary>
    public World World { get; } = new();

    /// <summary>
    ///     The JoltPhysics simulation. <see cref="Run" /> initializes it before <see cref="OnStart" />
    ///     and steps it automatically each frame after <see cref="OnUpdate" />.
    ///     Attach bodies via <c>node.RigidBody = new RigidBody3D { … }</c> inside <see cref="OnStart" />.
    /// </summary>
    public PhysicsWorld Physics { get; } = new();

    /// <summary>Called once before the game loop begins.</summary>
    protected virtual void OnStart()
    {
    }

    /// <summary>Called each frame with delta time in seconds before the UI frame.</summary>
    protected virtual void OnUpdate(float dt)
    {
    }

    /// <summary>Run until the window is closed.</summary>
    public void Run()
    {
        Physics.Initialize(Engine.Handle);
        OnStart();
        World.AttachPhysics(Physics);

        var targetTicks = TargetFps > 0 ? Stopwatch.Frequency / TargetFps : 0;
        var clock = Stopwatch.StartNew();

        while (!ShouldQuit)
        {
            var frameStart = clock.ElapsedTicks;

            Frame(); // UiApp.Frame() updates DeltaTime internally
            OnUpdate(DeltaTime);
            Physics.Step(DeltaTime);
            World.SyncFromPhysics(Physics);
            World.Update(DeltaTime); // update transforms, clear per-frame input deltas
            World.Sync(); // push world transforms to Zig renderer

            if (targetTicks > 0)
            {
                var remaining = targetTicks - (clock.ElapsedTicks - frameStart);
                if (remaining > 0)
                    Thread.Sleep((int)(remaining * 1000 / Stopwatch.Frequency));
            }
        }

        Physics.Dispose();
    }
}