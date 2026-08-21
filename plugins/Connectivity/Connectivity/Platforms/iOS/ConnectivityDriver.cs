using CoreFoundation;
using Network;

namespace Connectivity;

/// <summary>
///     iOS implementation — <c>NWPathMonitor</c>, the Network framework's own answer to this
///     question. One monitor serves both the reading and the events: it is started on the first
///     listener and, for a one-off <see cref="Read" />, briefly on demand.
/// </summary>
internal static class ConnectivityDriver
{
    private static readonly DispatchQueue Queue = new("dev.zigote.connectivity");
    private static NWPathMonitor? _monitor;
    private static Connection _last = Connection.Offline;

    public static Connection Read()
    {
        if (_monitor is not null) return _last;

        // No monitor running: take a snapshot from a throwaway one. NWPathMonitor delivers its
        // first path almost immediately, so a short wait is enough; a timeout answers offline.
        using var ready = new ManualResetEventSlim(false);
        var monitor = new NWPathMonitor();
        monitor.SnapshotHandler = path =>
        {
            _last = From(path);
            ready.Set();
        };
        monitor.SetQueue(Queue);
        monitor.Start();
        ready.Wait(TimeSpan.FromMilliseconds(250));
        monitor.Cancel();
        monitor.Dispose();
        return _last;
    }

    private static Connection From(NWPath? path)
    {
        if (path is null || path.Status != NWPathStatus.Satisfied) return Connection.Offline;
        var kind = path.UsesInterfaceType(NWInterfaceType.Wifi) ? ConnectionKind.WiFi
            : path.UsesInterfaceType(NWInterfaceType.Cellular) ? ConnectionKind.Cellular
            : path.UsesInterfaceType(NWInterfaceType.Wired) ? ConnectionKind.Ethernet
            : ConnectionKind.Other;
        return new Connection(true, kind);
    }

    public static void StartWatching(Action<Connection> publish)
    {
        var monitor = new NWPathMonitor();
        monitor.SnapshotHandler = path =>
        {
            _last = From(path);
            publish(_last);
        };
        monitor.SetQueue(Queue);
        monitor.Start();
        _monitor = monitor;
    }

    public static void StopWatching()
    {
        _monitor?.Cancel();
        _monitor?.Dispose();
        _monitor = null;
    }
}
