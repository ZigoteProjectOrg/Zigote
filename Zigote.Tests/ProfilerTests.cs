using Xunit;
using Zigote.Core.Diagnostics;

namespace Zigote.Tests;

/// <summary>
///     The profiler is the instrument the hot-path work is measured with, so it gets the same
///     guarantees it checks for: recording scopes on a steady-state frame allocates nothing, frames
///     land in <see cref="Profiler.LastFrame" /> with sane timing/depth, captures deliver exactly
///     the frames asked for, and <see cref="DebugProfiler.Aggregate" /> attributes self vs total
///     correctly.
/// </summary>
public class ProfilerTests
{
    private static void OneFrame()
    {
        Profiler.BeginFrame();
        using (Profiler.Scope("Outer"))
        {
            using (Profiler.Scope("Inner")) { }

            using (Profiler.Scope("Inner")) { }
        }

        Profiler.EndFrame();
    }

    [Fact]
    public void RecordsScopes_WithDepthAndOrdering()
    {
        OneFrame();
        var events = Profiler.LastFrame;

        Assert.Equal(expected: 3, actual: events.Count);
        // `using` disposal records innermost-first; the outer scope closes last.
        var outer = events[2];
        Assert.Equal(expected: "Outer", actual: Profiler.NameOf(outer.NameId));
        Assert.Equal(expected: 0, actual: outer.Depth);
        foreach (var inner in new[] { events[0], events[1] })
        {
            Assert.Equal(expected: "Inner", actual: Profiler.NameOf(inner.NameId));
            Assert.Equal(expected: 1, actual: inner.Depth);
            Assert.True(inner.StartTicks >= outer.StartTicks);
            Assert.True(inner.EndTicks <= outer.EndTicks);
        }
    }

    [Fact]
    public void SteadyStateFrame_AllocatesZero()
    {
        AllocGuard.AssertZeroAlloc(OneFrame);
    }

    [Fact]
    public void Capture_DeliversRequestedFrames_AndWritesChromeTrace()
    {
        string path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"zigote_profiler_test_{Environment.ProcessId}.json"
        );
        IReadOnlyList<Profiler.Event[]>? captured = null;
        Profiler.Capture(frames: 3, outputPath: path, onComplete: f => captured = [.. f]);

        for (int i = 0; i < 5; i++) OneFrame();

        Assert.NotNull(captured);
        Assert.Equal(expected: 3, actual: captured.Count);
        Assert.All(collection: captured, action: f => Assert.Equal(expected: 3, actual: f.Length));
        string trace = File.ReadAllText(path);
        Assert.StartsWith(expectedStartString: "{\"traceEvents\":[", actualString: trace);
        Assert.Contains(expectedSubstring: "\"Outer\"", actualString: trace);
        File.Delete(path);
    }

    [Fact]
    public void Aggregate_SplitsSelfFromChildTime()
    {
        OneFrame();
        var rows = DebugProfiler.Aggregate(Profiler.LastFrame);

        Assert.Equal(expected: 2, actual: rows.Count);
        var outer = rows.Single(r => r.Name == "Outer");
        var inner = rows.Single(r => r.Name == "Inner");
        Assert.Equal(expected: 1, actual: outer.Calls);
        Assert.Equal(expected: 2, actual: inner.Calls);
        // Outer's self time excludes the two nested Inner scopes.
        Assert.True(outer.SelfMs <= outer.TotalMs);
        Assert.Equal(
            expected: outer.TotalMs - inner.TotalMs,
            actual: outer.SelfMs,
            precision: 3
        );
    }
}
