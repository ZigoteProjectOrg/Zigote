using Zigote.Core.Math3D;
using Zigote.Physics2D;

namespace Zigote.RuntimeBench;

/// <summary>
///     Probe for the perf backlog's <c>CollisionWorld2D</c> item: "class-behind-dictionary chased per
///     candidate; dense struct array by slot id is the fix."
///     <para>
///         The shape being measured is a platformer's fixed step: a static tilemap of boxes, a handful
///         of characters sweeping through it, all at the 120 Hz gameplay tick. Every broadphase
///         candidate today costs a <c>Dictionary&lt;uint, Collider&gt;</c> lookup in
///         <c>GatherCandidates</c> and a second one in the query loop, plus a pointer chase into a
///         heap object each time. This measures what that adds up to before anything is restructured.
///     </para>
/// </summary>
internal static class Physics2DProbe
{
    private const int Steps = 2000;
    private const float Dt = 1f / 120f;
    private const uint All = 0xFFFF_FFFF;

    private static int _sink;

    public static void Run(string[] args)
    {
        int tiles = args.Length > 0 && int.TryParse(s: args[0], result: out int parsed)
            ? parsed
            : 2500;
        int side = (int)MathF.Sqrt(tiles);
        const int characters = 20;

        Console.WriteLine(
            $"Zigote.Physics2D collision probe — {side * side:N0} static tiles, "
            + $"{characters} characters, {Steps:N0} steps @ {Dt * 1000f:F2} ms"
        );
        Console.WriteLine(new string(c: '─', count: 78));

        var world = new CollisionWorld2D();
        // A solid tilemap: one unit boxes on a grid, which is what the uniform-grid broadphase is
        // tuned for and what a real 2D level looks like.
        for (int x = 0; x < side; x++)
        for (int y = 0; y < side; y++)
        {
            world.AddBox(
                center: new Vec2(x: x, y: y),
                halfExtents: new Vec2(x: 0.5f, y: 0.5f)
            );
        }

        Console.WriteLine($"world holds {world.Count:N0} colliders");

        var results = new List<ColliderHandle>(64);
        var probes = new Vec2[characters];
        for (int i = 0; i < characters; i++)
            probes[i] = new Vec2(x: i * 3.1f % side, y: i * 7.7f % side);

        var half = new Vec2(x: 0.4f, y: 0.9f); // a character-sized box

        double overlap = Program.Time(
            steps: Steps,
            body: () =>
            {
                for (int i = 0; i < probes.Length; i++)
                    _sink += world.OverlapBox(
                        center: probes[i],
                        halfExtents: half,
                        mask: All,
                        results: results
                    );
            }
        );

        double sweep = Program.Time(
            steps: Steps,
            body: () =>
            {
                for (int i = 0; i < probes.Length; i++)
                {
                    if (world.SweepBox(
                            center: probes[i],
                            halfExtents: half,
                            displacement: new Vec2(x: 0.3f, y: -0.5f),
                            mask: All,
                            hit: out _
                        ))
                        _sink++;
                }
            }
        );

        double ray = Program.Time(
            steps: Steps,
            body: () =>
            {
                for (int i = 0; i < probes.Length; i++)
                {
                    if (world.Raycast(
                            origin: probes[i],
                            direction: new Vec2(x: 0f, y: -1f),
                            maxDistance: 4f,
                            mask: All,
                            hit: out _
                        ))
                        _sink++;
                }
            }
        );

        // A character controller's real step: sweep-and-slide, ground snap, trigger bookkeeping.
        var controllers = new CharacterController2D[characters];
        for (int i = 0; i < characters; i++)
        {
            controllers[i] = new CharacterController2D(world: world, halfExtents: half) {
                Position = probes[i],
                Velocity = new Vec2(x: 2f, y: -4f),
            };
        }

        double move = Program.Time(
            steps: Steps,
            body: () =>
            {
                for (int i = 0; i < controllers.Length; i++)
                {
                    // Re-seat each step so the measurement stays on the same part of the map instead of
                    // walking the characters off the tilemap and into the empty-cell fast path.
                    controllers[i].Position = probes[i];
                    controllers[i].Velocity = new Vec2(x: 2f, y: -4f);
                    controllers[i].Move(Dt);
                }
            }
        );

        Console.WriteLine(new string(c: '─', count: 78));
        Report(label: $"OverlapBox   × {characters}", ns: overlap, characters: characters);
        Report(label: $"SweepBox     × {characters}", ns: sweep, characters: characters);
        Report(label: $"Raycast      × {characters}", ns: ray, characters: characters);
        Report(label: $"Controller.Move × {characters}", ns: move, characters: characters);
        Console.WriteLine(new string(c: '─', count: 78));

        // The controller step is the one a real game actually runs every fixed tick.
        double coreShare = move * 1e-9 * (1d / Dt);
        Console.WriteLine(
            $"at the 120 Hz fixed step, {characters} characters cost "
            + $"{coreShare * 100:F2}% of one core."
        );
        Console.WriteLine();
        Console.WriteLine(
            coreShare >= 0.05d
                ? "VERDICT: on the critical path. The dictionary chase is worth removing —\n"
                  + "         start with the cheap half (candidates carry the collider, not its id)."
                : "VERDICT: not on anyone's critical path at this scale. Re-run with more tiles and\n"
                  + "         characters before restructuring the storage."
        );
    }

    private static void Report(string label, double ns, int characters) => Console.WriteLine(
        $"{label,-28} {ns / 1000f,8:F1} µs/step  {ns / characters,8:F0} ns/character"
    );
}
