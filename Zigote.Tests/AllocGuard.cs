using Xunit;

namespace Zigote.Tests;

/// <summary>
///     The standard steady-state allocation gate: warm a hot loop past tiered JIT and cache
///     population, then assert it allocates exactly zero managed bytes. Use for every new hot loop
///     (per the zero-allocation rules) instead of hand-rolling the warmup/measure dance:
///     <code>AllocGuard.AssertZeroAlloc(() =&gt; Frame(root, paint, c));</code>
///     Exact and deterministic for a single-threaded loop
///     (<see cref="GC.GetAllocatedBytesForCurrentThread" />).
/// </summary>
public static class AllocGuard
{
    public static void AssertZeroAlloc(Action iteration, int warmup = 200, int iterations = 500)
    {
        for (int i = 0; i < warmup; i++) iteration();

        long allocated = Measure(() =>
        {
            for (int i = 0; i < iterations; i++) iteration();
        });

        Assert.True(
            condition: allocated == 0,
            userMessage: $"Hot loop allocated {allocated} B over {iterations} iterations " +
                         $"({allocated / (double)iterations:F2} B/iteration); expected 0."
        );
    }

    /// <summary>
    ///     Managed bytes <paramref name="loop" /> allocates on this thread in steady state: the least
    ///     of up to <paramref name="attempts" /> runs, stopping at the first that allocates nothing.
    ///     <para>
    ///         One run is not enough when the suite runs in parallel. Now and then a single burst of
    ///         4–8 KB lands in the window that the loop did not cause — measured on a particle step
    ///         that is pure arithmetic: 7856 B in one 25-step slice, 0 B in the 39 slices around it.
    ///         A loop that really allocates does so on every run, so the minimum still catches it;
    ///         the loop must therefore be repeatable.
    ///     </para>
    /// </summary>
    public static long Measure(Action loop, int attempts = 3)
    {
        long least = long.MaxValue;
        for (int attempt = 0; attempt < attempts && least != 0; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            loop();
            least = Math.Min(val1: least, val2: GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }
}
