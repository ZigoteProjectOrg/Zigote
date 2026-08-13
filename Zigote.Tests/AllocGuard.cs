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

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) iteration();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            condition: allocated == 0,
            userMessage: $"Hot loop allocated {allocated} B over {iterations} iterations " +
                         $"({allocated / (double)iterations:F2} B/iteration); expected 0."
        );
    }
}
