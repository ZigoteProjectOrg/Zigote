using Zigote.Ecs;
using Zigote.Scripting;

namespace Samples.Scripting;

/// <summary>
///     Demonstrates the <see cref="Ecs" /> scripting provider: a self-contained, data-oriented
///     sub-simulation living in the live flecs world. On create it spawns <see cref="Count" />
///     entities
///     with Position + Velocity and builds the chunk query ONCE; each frame it integrates them all
///     through that cached query — the "thousands of entities in one query" pattern, independent of
///     the scene graph (no per-entity Component, no per-entity FFI, no per-frame query build).
///     Rendering them would feed their transforms to the Instancing provider; this is the logic
///     half.
/// </summary>
public sealed class EcsSwarm : Component
{
    private float _dt;
    private Action<Span<SwarmPos>, Span<SwarmVel>>? _integrate;
    private Query<SwarmPos, SwarmVel>? _query;

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

        _query = world.Query<SwarmPos, SwarmVel>();
        _integrate = Integrate;
    }

    protected override void OnUpdate(float deltaTime)
    {
        if (_query is not { } query || _integrate is not { } integrate) return;
        _dt = deltaTime;
        query.Each(integrate);
    }

    protected override void OnDestroy()
    {
        _query?.Dispose();
        _query = null;
    }

    private void Integrate(Span<SwarmPos> pos, Span<SwarmVel> vel)
    {
        for (var i = 0; i < pos.Length; i++)
        {
            pos[i].X += vel[i].X * _dt;
            pos[i].Y += vel[i].Y * _dt;
            pos[i].Z += vel[i].Z * _dt;
        }
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