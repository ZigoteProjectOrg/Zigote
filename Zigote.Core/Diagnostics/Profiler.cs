using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Zigote.Core.Diagnostics;

/// <summary>
///     Lightweight CPU profiler (debug-system Phase 3). Scoped timings with interned names and
///     double-buffered, struct-based event buffers — no per-frame heap allocation on the hot path.
///     Frame N's events are read while frame N+1 fills the other buffer.
///     Usage:
///     <code>
///     using (Profiler.Scope("Renderer.Submit")) { ... }
///     </code>
///     Bracket each frame with <see cref="BeginFrame" /> / <see cref="EndFrame" />.
/// </summary>
public static class Profiler
{
    // Safety cap on the in-progress buffer. A single frame never records anywhere near this many
    // scopes, so a well-behaved host (one that brackets each frame with BeginFrame/EndFrame) never
    // hits it. It exists only to defend against a host that leaves Enabled=true but never ends a
    // frame — e.g. any app driven by the shared App.Frame loop without the editor's per-frame
    // bracketing. Without it, _current grows without bound (its Event[] doubles on the LOH into the
    // gigabytes) under continuous rendering. See the ZIGOTE_CONTINUOUS memory-growth investigation.
    private const int MaxCurrentEvents = 1 << 15;
    [ThreadStatic] private static int _depth;

    private static readonly object Lock = new();
    private static List<Event> _current = new(1024);
    private static List<Event> _previous = new(1024);

    private static readonly Dictionary<string, int> NameIds = new(StringComparer.Ordinal);
    private static readonly List<string> Names = [];

    // Capture state (multi-frame export). Only allocates while a capture is in progress.
    private static int _captureFramesLeft;
    private static readonly List<Event[]> Captured = [];

    private static string _capturePath = "profile_capture.json";

    /// <summary>Master switch — when false, scopes are near-free no-ops.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Events recorded for the most recently completed frame (read-only; no allocation).</summary>
    public static IReadOnlyList<Event> LastFrame => _previous;

    public static string NameOf(int id) => (uint)id < (uint)Names.Count ? Names[id] : "?";

    private static int Intern(string name)
    {
        lock (NameIds)
        {
            if (NameIds.TryGetValue(key: name, value: out int id)) return id;
            id = Names.Count;
            Names.Add(name);
            NameIds[name] = id;
            return id;
        }
    }

    /// <summary>Open a profiling scope; dispose (end of <c>using</c>) records its duration.</summary>
    public static ScopeHandle Scope(string name)
    {
        if (!Enabled) return default;
        int d = _depth;
        _depth = d + 1;
        return new ScopeHandle(nameId: Intern(name), depth: d, start: Stopwatch.GetTimestamp());
    }

    private static void Record(int nameId, int depth, long start, long end)
    {
        _depth = depth; // unwind to this scope's depth (LIFO `using` disposal)
        lock (Lock)
        {
            // Guard against a host that never calls EndFrame: drop the runaway accumulation rather
            // than leak the LOH. Skipped mid-capture so an export frame is never truncated.
            if (_captureFramesLeft == 0 && _current.Count >= MaxCurrentEvents)
                _current.Clear();

            _current.Add(
                new Event {
                    NameId = nameId,
                    Depth = depth,
                    ThreadId = Environment.CurrentManagedThreadId,
                    StartTicks = start,
                    EndTicks = end,
                }
            );
        }
    }

    public static void BeginFrame()
    {
        _depth = 0;
        lock (Lock) _current.Clear();
    }

    public static void EndFrame()
    {
        lock (Lock) (_previous, _current) = (_current, _previous);

        if (_captureFramesLeft > 0)
        {
            Captured.Add(
                _previous.ToArray()
            ); // copy out only while capturing (not the steady-state path)
            if (--_captureFramesLeft == 0) FlushCapture();
        }
    }

    /// <summary>
    ///     Capture the next <paramref name="frames" /> frames and write a Chrome-Trace JSON file
    ///     (openable in chrome://tracing / Perfetto) when complete.
    /// </summary>
    public static void Capture(int frames, string outputPath)
    {
        Captured.Clear();
        _capturePath = outputPath;
        _captureFramesLeft = Math.Max(val1: 1, val2: frames);
    }

    private static void FlushCapture()
    {
        try
        {
            File.WriteAllText(path: _capturePath, contents: ExportChromeTrace(Captured));
            Console.Error.WriteLine(
                $"[Profiler] capture written: {_capturePath} ({Captured.Count} frames)"
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Profiler] capture failed: {ex.Message}");
        }

        Captured.Clear();
    }

    /// <summary>Serialize captured frames to Chrome-Trace ("Trace Event") JSON.</summary>
    public static string ExportChromeTrace(IReadOnlyList<Event[]> frames)
    {
        // ticks → microseconds
        double usPerTick = 1_000_000.0 / Stopwatch.Frequency;
        long baseTick = long.MaxValue;
        foreach (var f in frames)
        foreach (var e in f)
        {
            if (e.StartTicks < baseTick)
                baseTick = e.StartTicks;
        }

        if (baseTick == long.MaxValue) baseTick = 0;

        var sb = new StringBuilder(64 * 1024);
        sb.Append("{\"traceEvents\":[");
        bool first = true;
        foreach (var f in frames)
        foreach (var e in f)
        {
            if (!first) sb.Append(',');
            first = false;
            string ts = ((e.StartTicks - baseTick) * usPerTick).ToString(
                format: "F1",
                provider: CultureInfo.InvariantCulture
            );
            string dur = (e.DurationTicks * usPerTick).ToString(
                format: "F1",
                provider: CultureInfo.InvariantCulture
            );
            sb.Append("{\"name\":\"").Append(Escape(NameOf(e.NameId)))
                .Append("\",\"ph\":\"X\",\"pid\":1,\"tid\":").Append(e.ThreadId)
                .Append(",\"ts\":").Append(ts)
                .Append(",\"dur\":").Append(dur).Append('}');
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        return s.IndexOf('"') < 0 && s.IndexOf('\\') < 0
            ? s
            : s.Replace(oldValue: "\\", newValue: "\\\\").Replace(oldValue: "\"", newValue: "\\\"");
    }

    public readonly struct Event
    {
        public int NameId { get; init; }
        public int Depth { get; init; }
        public int ThreadId { get; init; }
        public long StartTicks { get; init; }
        public long EndTicks { get; init; }
        public long DurationTicks => EndTicks - StartTicks;
    }

    public readonly struct ScopeHandle : IDisposable
    {
        private readonly int _nameId;
        private readonly int _depth;
        private readonly long _start;
        private readonly bool _active;

        internal ScopeHandle(int nameId, int depth, long start)
        {
            _nameId = nameId;
            _depth = depth;
            _start = start;
            _active = true;
        }

        public void Dispose()
        {
            if (_active)
            {
                Record(
                    nameId: _nameId,
                    depth: _depth,
                    start: _start,
                    end: Stopwatch.GetTimestamp()
                );
            }
        }
    }
}
