using Zigote.Scripting;

namespace Samples.Scripting;

/// <summary>
///     Demonstrates the <see cref="Ecs" /> scripting provider: a self-contained, data-oriented
///     sub-simulation living in the live flecs world. On create it spawns <see cref="Count" />
///     entities
///     with Position + Velocity; each frame it integrates them all in a single chunk query — the
///     "thousands of entities in one query" pattern, independent of the scene graph (no per-entity
///     Component, no per-entity FFI). Rendering them would feed their transforms to the Instancing
///     provider; this is the logic half.
/// </summary>
public sealed class EcsSwarm : Component
{
    [Export]
    [EditorRange(0, 5000)]
    [EditorTooltip("Number of ECS entities the swarm spawns and integrates")]
    public int Count { get; set; } = 500;

    protected override void OnCreate()
    {
        if (Ecs.World is not { } world) return;
        for (var i = 0; i < Count; i++)
        {
            var e = world.CreateEntity();
            world.Set(e, new SwarmPos());
            world.Set(
                e,
                new SwarmVel {
                    X = i % 7 - 3,
                    Y = 1f,
                    Z = i % 5 - 2,
                }
            );
        }
    }

    protected override void OnUpdate(float deltaTime)
    {
        Ecs.World?.ForEach<SwarmPos, SwarmVel>((pos, vel) =>
            {
                for (var i = 0; i < pos.Length; i++)
                {
                    pos[i].X += vel[i].X * deltaTime;
                    pos[i].Y += vel[i].Y * deltaTime;
                    pos[i].Z += vel[i].Z * deltaTime;
                }
            }
        );
    }

    private struct SwarmPos
    {
        public float X, Y, Z;
    }

    private struct SwarmVel
    {
        public float X, Y, Z;
    }
}