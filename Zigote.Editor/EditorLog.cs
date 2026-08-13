using Zigote.Core.Diagnostics;

namespace Zigote.Editor;

/// <summary>Severity of an <see cref="EditorLog" /> entry. Ordered low→high for filtering.</summary>
public enum LogSeverity
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>One console line.</summary>
public readonly record struct LogEntry(LogSeverity Severity, string Message);

/// <summary>
///     Editor-facing view over the canonical <see cref="DebugLog" /> ring buffer in <c>Zigote.Core</c>
///     .
///     The editor Console panel and the in-engine DevTools Logs/Console panels therefore share a
///     single
///     stream — engine stdout/stderr (captured by <see cref="DebugLog.CaptureConsole" />) and any
///     programmatic <see cref="Add" />s appear in both. This type only adapts the level enum and entry
///     shape the editor's <c>ConsolePanel</c> expects.
/// </summary>
public static class EditorLog
{
    private static readonly List<DebugLogEntry> Scratch = [];

    public static int Version => DebugLog.Version;

    public static void CopyInto(List<LogEntry> dest)
    {
        DebugLog.CopyInto(Scratch);
        dest.Clear();
        if (dest.Capacity < Scratch.Count) dest.Capacity = Scratch.Count;
        foreach (var e in Scratch) dest.Add(new LogEntry(ToSeverity(e.Level), e.Message));
    }

    public static (int Error, int Warning, int Info) Counts()
    {
        var (trace, debug, info, warning, error, fatal) = DebugLog.Counts();
        return (error + fatal, warning, info + debug + trace);
    }

    public static void Add(LogSeverity severity, string message)
    {
        DebugLog.Add(ToLevel(severity), message, "editor");
    }

    public static void Clear()
    {
        DebugLog.Clear();
    }

    /// <summary>Tee stdout + stderr into the shared log (call once at startup).</summary>
    public static void CaptureConsole()
    {
        DebugLog.CaptureConsole();
    }

    private static DebugLogLevel ToLevel(LogSeverity s)
    {
        return s switch {
            LogSeverity.Debug => DebugLogLevel.Debug,
            LogSeverity.Warning => DebugLogLevel.Warning,
            LogSeverity.Error => DebugLogLevel.Error,
            _ => DebugLogLevel.Info,
        };
    }

    private static LogSeverity ToSeverity(DebugLogLevel l)
    {
        return l switch {
            DebugLogLevel.Trace or DebugLogLevel.Debug => LogSeverity.Debug,
            DebugLogLevel.Warning => LogSeverity.Warning,
            DebugLogLevel.Error or DebugLogLevel.Fatal => LogSeverity.Error,
            _ => LogSeverity.Info,
        };
    }
}
