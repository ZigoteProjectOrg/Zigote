using System.Diagnostics;

namespace Zigote.Core.Diagnostics;

/// <summary>
///     Frame-history + aggregation layer over <see cref="Profiler" /> for the debug-menu profiler
///     panel
///     (design doc §9.2). <see cref="Profiler" /> itself stays allocation-free and only retains the
///     last
///     frame's scope events; this adds a small ring of recent frame times (for the frame-time graph)
///     and
///     an on-demand flatten of the last frame's nested scopes into a self/total per-scope table. The
///     ring
///     stores only floats — no per-frame event copies — so it is cheap to keep running continuously.
/// </summary>
public static class DebugProfiler
{
    public const int HistoryCapacity = 240;

    private static readonly float[] FrameMs = new float[HistoryCapacity];
    private static int _head;

    /// <summary>Number of frames recorded so far (saturates at <see cref="HistoryCapacity" />).</summary>
    public static int FrameCount { get; private set; }

    public static float Last { get; private set; }

    /// <summary>Push one frame's wall-clock time (ms) into the history ring. Call once per frame.</summary>
    public static void RecordFrame(float milliseconds)
    {
        if (float.IsNaN(milliseconds) || milliseconds < 0f) milliseconds = 0f;
        FrameMs[_head] = milliseconds;
        _head = (_head + 1) % HistoryCapacity;
        if (FrameCount < HistoryCapacity) FrameCount++;
        Last = milliseconds;
    }

    /// <summary>Copy the frame-time history oldest→newest into <paramref name="dest" /> (cleared first).</summary>
    public static void CopyHistory(List<float> dest)
    {
        dest.Clear();
        if (dest.Capacity < FrameCount) dest.Capacity = FrameCount;
        var start = (_head - FrameCount + HistoryCapacity) % HistoryCapacity;
        for (var i = 0; i < FrameCount; i++)
            dest.Add(FrameMs[(start + i) % HistoryCapacity]);
    }

    /// <summary>Min / max / average of the recorded frame times (ms). Zeroes when empty.</summary>
    public static (float Min, float Max, float Avg) Stats()
    {
        if (FrameCount == 0) return (0f, 0f, 0f);
        float min = float.MaxValue, max = 0f, sum = 0f;
        var start = (_head - FrameCount + HistoryCapacity) % HistoryCapacity;
        for (var i = 0; i < FrameCount; i++)
        {
            var v = FrameMs[(start + i) % HistoryCapacity];
            min = MathF.Min(min, v);
            max = MathF.Max(max, v);
            sum += v;
        }

        return (min, max, sum / FrameCount);
    }

    /// <summary>
    ///     Aggregate the events of one frame (e.g. <see cref="Profiler.LastFrame" />) into per-name
    ///     total/self time and call count, sorted by total time descending. Self time is total minus the
    ///     time spent in directly-nested child scopes, computed from the LIFO depth ordering.
    /// </summary>
    public static List<ScopeAggregate> Aggregate(IReadOnlyList<Profiler.Event> frame)
    {
        var n = frame.Count;
        if (n == 0) return [];

        // Each Event is a completed [start,end] interval at a known depth. Self time = duration minus the
        // durations of its direct children. Sort by start and walk a containment stack: pop siblings that
        // closed before this one opened, add this scope's duration to the new top-of-stack (its parent),
        // then push it. Correct regardless of the LIFO record order or depth-field drift.
        var idx = new int[n];
        for (var i = 0; i < n; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => frame[a].StartTicks.CompareTo(frame[b].StartTicks));

        var childTicks = new long[n]; // child time accumulated per event while it sits on the stack
        var stack = new int[n];
        var sp = 0;

        foreach (var i in idx)
        {
            var e = frame[i];
            while (sp > 0 && frame[stack[sp - 1]].EndTicks <= e.StartTicks) sp--;
            if (sp > 0) childTicks[stack[sp - 1]] += e.DurationTicks;
            stack[sp++] = i;
        }

        var perTick = 1000.0 / Stopwatch.Frequency;
        var byName =
            new Dictionary<string, (double total, double self, int calls, int minDepth)>(
                StringComparer.Ordinal
            );
        for (var i = 0; i < n; i++)
        {
            var e = frame[i];
            var name = Profiler.NameOf(e.NameId);
            var selfTicks = e.DurationTicks - childTicks[i];
            if (selfTicks < 0) selfTicks = 0;

            byName.TryGetValue(name, out var acc);
            var first = acc.calls == 0;
            acc.total += e.DurationTicks * perTick;
            acc.self += selfTicks * perTick;
            acc.calls += 1;
            acc.minDepth = first ? e.Depth : Math.Min(acc.minDepth, e.Depth);
            byName[name] = acc;
        }

        var result = new List<ScopeAggregate>(byName.Count);
        foreach (var kv in byName)
            result.Add(
                new ScopeAggregate(
                    kv.Key,
                    kv.Value.total,
                    kv.Value.self,
                    kv.Value.calls,
                    kv.Value.minDepth
                )
            );
        result.Sort(static (a, b) => b.TotalMs.CompareTo(a.TotalMs));
        return result;
    }

    /// <summary>A flattened per-scope aggregate of one frame's events.</summary>
    public readonly record struct ScopeAggregate(
        string Name,
        double TotalMs,
        double SelfMs,
        int Calls,
        int MinDepth);
}
