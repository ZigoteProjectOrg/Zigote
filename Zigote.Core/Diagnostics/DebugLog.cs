using System.Text;

namespace Zigote.Core.Diagnostics;

/// <summary>Severity of a <see cref="DebugLog" /> entry. Ordered low→high for filtering.</summary>
public enum DebugLogLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Fatal,
}

/// <summary>One ring-buffered log line. Immutable; <see cref="Seq" /> is monotonic per process.</summary>
public readonly record struct DebugLogEntry(
    long Seq,
    int Frame,
    DebugLogLevel Level,
    string Category,
    string Message);

/// <summary>
///     Process-wide, thread-safe log ring buffer — the canonical engine log surface (design doc
///     §9.13).
///     Native logs flow through <see cref="Console" />.WriteLine with a <c>[Zigote::XXX]</c> severity
///     prefix and C# diagnostics through <see cref="Console" />.Error; <see cref="CaptureConsole" />
///     tees
///     <b>both</b> stdout and stderr into here (parsing the native prefix, falling back to the
///     stream's
///     default severity). Lives in <c>Zigote.Core</c> so the UI debug menu, the editor console, and
///     headless tools share one buffer with no engine-side change. Oldest entries evict at
///     <see cref="Capacity" />.
/// </summary>
public static class DebugLog
{
    public const int Capacity = 4000;

    private static readonly DebugLogEntry[] Ring = new DebugLogEntry[Capacity];
    private static readonly object Gate = new();
    private static int _head; // next write slot
    private static int _count;
    private static long _seq;

    private static bool _captured;

    /// <summary>
    ///     Monotonic counter bumped on every add/clear so pollers can skip rebuilding when nothing
    ///     changed instead of copying the buffer every frame.
    /// </summary>
    public static int Version { get; private set; }

    /// <summary>Frame index stamped onto new entries. The frame loop sets this each frame; 0 if unset.</summary>
    public static int CurrentFrame { get; set; }

    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return _count;
            }
        }
    }

    public static void Add(DebugLogLevel level, string message, string category = "app")
    {
        lock (Gate)
        {
            Ring[_head] = new DebugLogEntry(
                ++_seq,
                CurrentFrame,
                level,
                category,
                message
            );
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
            Version++;
        }
    }

    public static void Trace(string message, string category = "app")
    {
        Add(DebugLogLevel.Trace, message, category);
    }

    public static void Debug(string message, string category = "app")
    {
        Add(DebugLogLevel.Debug, message, category);
    }

    public static void Info(string message, string category = "app")
    {
        Add(DebugLogLevel.Info, message, category);
    }

    public static void Warn(string message, string category = "app")
    {
        Add(DebugLogLevel.Warning, message, category);
    }

    public static void Error(string message, string category = "app")
    {
        Add(DebugLogLevel.Error, message, category);
    }

    public static void Fatal(string message, string category = "app")
    {
        Add(DebugLogLevel.Fatal, message, category);
    }

    /// <summary>
    ///     Copy current entries oldest→newest into <paramref name="dest" /> without allocating
    ///     fresh storage (cleared first).
    /// </summary>
    public static void CopyInto(List<DebugLogEntry> dest)
    {
        lock (Gate)
        {
            dest.Clear();
            if (dest.Capacity < _count) dest.Capacity = _count;
            var start = (_head - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++)
                dest.Add(Ring[(start + i) % Capacity]);
        }
    }

    /// <summary>Per-level counts (no allocation) for the filter chips.</summary>
    public static (int Trace, int Debug, int Info, int Warning, int Error, int Fatal) Counts()
    {
        lock (Gate)
        {
            int t = 0, d = 0, i = 0, w = 0, e = 0, f = 0;
            var start = (_head - _count + Capacity) % Capacity;
            for (var k = 0; k < _count; k++)
                switch (Ring[(start + k) % Capacity].Level)
                {
                    case DebugLogLevel.Trace: t++; break;
                    case DebugLogLevel.Debug: d++; break;
                    case DebugLogLevel.Info: i++; break;
                    case DebugLogLevel.Warning: w++; break;
                    case DebugLogLevel.Error: e++; break;
                    case DebugLogLevel.Fatal: f++; break;
                }

            return (t, d, i, w, e, f);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _head = 0;
            _count = 0;
            Version++;
        }
    }

    /// <summary>Tee stdout + stderr into the log (idempotent; call once at startup).</summary>
    public static void CaptureConsole()
    {
        lock (Gate)
        {
            if (_captured) return;
            _captured = true;
        }

        Console.SetOut(new TeeWriter(Console.Out, DebugLogLevel.Info));
        Console.SetError(new TeeWriter(Console.Error, DebugLogLevel.Error));
    }

    private static void Ingest(string line, DebugLogLevel fallback)
    {
        var trimmed = line.TrimEnd('\r', '\n');
        if (trimmed.Length == 0) return;

        // Native lines carry an explicit [Zigote::XXX] severity; C# lines fall back to the stream's
        // default (Info for stdout, Error for stderr).
        var level = fallback;
        var category = "app";
        if (trimmed.Contains("::ERR")) level = DebugLogLevel.Error;
        else if (trimmed.Contains("::WRN")) level = DebugLogLevel.Warning;
        else if (trimmed.Contains("::DBG")) level = DebugLogLevel.Debug;
        else if (trimmed.Contains("::INF")) level = DebugLogLevel.Info;

        if (trimmed.StartsWith("[Zigote", StringComparison.Ordinal))
        {
            category = "native";
            var close = trimmed.IndexOf("] ", StringComparison.Ordinal);
            if (close > 0) trimmed = trimmed[(close + 2)..];
        }

        Add(level, trimmed, category);
    }

    /// <summary>
    ///     A <see cref="TextWriter" /> that forwards to the real stream and mirrors lines into the
    ///     log.
    /// </summary>
    private sealed class TeeWriter(TextWriter inner, DebugLogLevel fallback) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            if (!string.IsNullOrEmpty(value)) Ingest(value, fallback);
        }

        public override void WriteLine(object? value)
        {
            WriteLine(value?.ToString());
        }

        public override void Write(char value)
        {
            inner.Write(value);
        }

        public override void Write(string? value)
        {
            inner.Write(value);
        }
    }
}
