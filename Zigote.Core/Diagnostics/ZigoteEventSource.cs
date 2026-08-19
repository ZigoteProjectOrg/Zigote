using System.Diagnostics.Tracing;

namespace Zigote.Core.Diagnostics;

/// <summary>
///     External-profiler bridge: publishes the engine's frame health as .NET EventCounters and
///     (opt-in via the <c>Scopes</c> keyword) mirrors <see cref="Profiler" /> scopes as start/stop
///     events. Nothing here costs anything until a session subscribes — counters are created on the
///     first Enable command and every hot-path call is gated on <see cref="EventSource.IsEnabled()" />.
///     <para>
///         Consume with any EventPipe tool:
///         <c>dotnet-counters monitor -n YourApp --counters Zigote-Engine</c>,
///         <c>dotnet-trace collect -n YourApp --providers Zigote-Engine:0x1:5</c> (scope events),
///         or Rider's dotTrace Timeline, where the scope events line up against its own sampling.
///     </para>
/// </summary>
[EventSource(Name = "Zigote-Engine")]
public sealed class ZigoteEventSource : EventSource
{
    public static readonly ZigoteEventSource Log = new();

    private EventCounter? _allocKb;
    private EventCounter? _frameMs;
    private IncrementingEventCounter? _jank;

    private ZigoteEventSource() { }

    /// <summary>True while a session asked for per-scope events (Verbose + Scopes keyword).</summary>
    public bool ScopesEnabled => IsEnabled(level: EventLevel.Verbose, keywords: Keywords.Scopes);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command != EventCommand.Enable) return;
        _frameMs ??= new EventCounter(name: "frame-time", eventSource: this) {
            DisplayName = "Frame time", DisplayUnits = "ms",
        };
        _allocKb ??= new EventCounter(name: "ui-alloc-per-frame", eventSource: this) {
            DisplayName = "UI-thread alloc / frame", DisplayUnits = "KB",
        };
        _jank ??= new IncrementingEventCounter(name: "jank-frames", eventSource: this) {
            DisplayName = "Jank frames",
        };
    }

    /// <summary>Feed one frame's sample into the counters. No-op until a session is listening.</summary>
    [NonEvent]
    public void Frame(double frameMs, double allocKb, bool jank)
    {
        _frameMs?.WriteMetric(frameMs);
        _allocKb?.WriteMetric(allocKb);
        if (jank) _jank?.Increment();
    }

    [Event(1, Level = EventLevel.Verbose, Keywords = Keywords.Scopes)]
    public void ScopeStart(string Name) => WriteEvent(1, Name);

    [Event(2, Level = EventLevel.Verbose, Keywords = Keywords.Scopes)]
    public void ScopeStop(string Name) => WriteEvent(2, Name);

    public static class Keywords
    {
        public const EventKeywords Scopes = (EventKeywords)0x1;
    }
}
