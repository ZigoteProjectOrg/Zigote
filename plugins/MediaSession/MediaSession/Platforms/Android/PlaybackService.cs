using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace MediaSession;

/// <summary>
///     The reason audio keeps playing after the user leaves the app — lifted from Timbre.
///     <para>
///         Android does not let an app with no visible window keep running: once the last activity
///         stops, the process is a low-memory-killer candidate and typically gone within seconds. A
///         <i>foreground</i> service is the documented exemption, and its price is a notification
///         the user can see the whole time.
///     </para>
///     <para>
///         It owns no playback state — audio already runs on the engine's thread in this process.
///         Started, never bound: a bound service dies with its last client, and the client would be
///         the activity, which is exactly what has gone away.
///     </para>
/// </summary>
[Service(
    Name = "dev.zigote.plugins.mediasession.PlaybackService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMediaPlayback
)]
public sealed class PlaybackService : Service
{
    internal const string ActionStart = "dev.zigote.plugins.mediasession.START";
    internal const string ActionStop = "dev.zigote.plugins.mediasession.STOP";

    /// <summary>Whether the service is in the foreground — start it once, not on every update.</summary>
    internal static bool Running { get; private set; }

    /// <summary>
    ///     NotSticky everywhere: if the process died, playback died with it, and coming back
    ///     silently holding a notification is worse than being gone.
    /// </summary>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop || MediaSessionDriver.Current?.BuildNotification(this) is not { } notification)
        {
            // Nothing to show means nothing is playing, and a foreground service with no
            // notification is a guaranteed crash — so refuse rather than start.
            Stop();
            return StartCommandResult.NotSticky;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(MediaSessionDriver.NotificationId, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(MediaSessionDriver.NotificationId, notification);

        Running = true;
        return StartCommandResult.NotSticky;
    }

    private void Stop()
    {
        Running = false;
        // Remove, not Detach: buttons left behind on a stopped service reach nothing.
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    public override void OnDestroy()
    {
        Running = false;
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;
}
