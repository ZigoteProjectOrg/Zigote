namespace Zigote.Network;

/// <summary>
///     Fixed-timestep accumulator. Feed it the variable frame delta; it returns how many fixed steps to run
///     this frame so the simulation advances at a constant rate regardless of frame rate. <see cref="Alpha" />
///     is the leftover fraction for rendering between steps. Guarded against the spiral of death (a long stall
///     can't queue unbounded catch-up steps).
/// </summary>
public sealed class NetTick(float interval)
{
    private const int MaxStepsPerFrame = 8;
    private float _accumulator;

    public float Interval { get; } = interval > 0f ? interval : 1f / 60f;

    /// <summary>Number of fixed steps elapsed since creation.</summary>
    public uint Tick { get; private set; }

    /// <summary>Fraction in [0,1) through the current step — use to interpolate rendering between fixed states.</summary>
    public float Alpha => _accumulator / Interval;

    /// <summary>Accumulate <paramref name="dt" /> and return the number of fixed steps to run now.</summary>
    public int Advance(float dt)
    {
        _accumulator += dt;
        var steps = 0;
        while (_accumulator >= Interval)
        {
            _accumulator -= Interval;
            Tick++;
            if (++steps < MaxStepsPerFrame) continue;
            _accumulator = 0f; // drop the backlog rather than spiral
            break;
        }

        return steps;
    }

    public void Reset()
    {
        _accumulator = 0f;
        Tick = 0;
    }
}