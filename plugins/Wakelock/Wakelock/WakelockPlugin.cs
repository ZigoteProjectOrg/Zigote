namespace Wakelock;

/// <summary>
///     Wakelock — the <c>wakelock_plus</c> slot from the plugin roadmap. Keeps the display on
///     while something is being watched, followed or played. Static, nothing to register with
///     <c>PluginHost</c>. It holds the <em>screen</em>, not the CPU: a backgrounded app is still
///     suspended by the OS, which is what every platform's equivalent does too.
/// </summary>
public static class WakelockPlugin
{
    private static readonly Lock Gate = new();

    /// <summary>True where the screen can be held awake — false on a Linux box with no session bus.</summary>
    public static bool Supported => WakelockDriver.Supported;

    /// <summary>Whether this app is currently holding the screen awake.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>Keep the screen on. True once held — including if it already was.</summary>
    public static bool Enable() => Set(true);

    /// <summary>Let the screen sleep again. True once released — including if it never was held.</summary>
    public static bool Disable() => Set(false);

    /// <summary>
    ///     <see cref="Enable" /> or <see cref="Disable" /> from a bool — the shape a toggle or a
    ///     "playing" signal wants. The driver only sees transitions, so calling this every frame
    ///     is free. False when the OS refused, and <see cref="Enabled" /> is left as it was.
    /// </summary>
    public static bool Set(bool on)
    {
        // ponytail: one global flag, last caller wins — a video and a game both holding it race on
        // release. Ref-count with an IDisposable Hold() if two owners ever appear in one app.
        lock (Gate)
        {
            if (Enabled == on) return true;
            if (!WakelockDriver.Set(on)) return false;
            Enabled = on;
            return true;
        }
    }
}
