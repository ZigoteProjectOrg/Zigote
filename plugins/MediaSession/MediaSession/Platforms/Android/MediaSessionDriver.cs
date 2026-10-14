using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.OS;
using AndroidSession = Android.Media.Session.MediaSession;
using PlaybackState = Android.Media.Session.PlaybackState;
using PlaybackStateCode = Android.Media.Session.PlaybackStateCode;

// The service type and its permission have to be in the manifest; declared here so they merge
// into every app that references the plugin instead of being a README step people miss.
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE")]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_MEDIA_PLAYBACK")]

namespace MediaSession;

/// <summary>
///     Android implementation: one framework MediaSession, one media-style notification, one
///     foreground service — lifted from Timbre.
///     <para>
///         A MediaSession is MPRIS (metadata out, transport commands in, and what the lock screen,
///         Bluetooth and a car head unit read), and the ongoing notification is its visible half.
///         The foreground service is what lets audio outlive the activity at all: without it the
///         process is a low-memory-killer candidate seconds after the user leaves, mid-song.
///     </para>
///     <para>
///         Framework <c>Android.Media.Session</c> rather than AndroidX: the floor is API 26 and every
///         piece used here landed in 21.
///     </para>
/// </summary>
internal sealed class MediaSessionDriver : IDisposable
{
    internal const int NotificationId = 0x6d73; // "ms" — stable, so a re-post replaces in place

    /// <summary>
    ///     The live driver, for the service and the button receiver — both are instantiated by the
    ///     OS, not by us, so they cannot be handed one.
    /// </summary>
    internal static volatile MediaSessionDriver? Current;

    private readonly string _channelId;
    private readonly string _appName;
    private readonly Action<MediaCommand> _raise;

    private AndroidSession? _session;
    private AudioManager? _audio;
    private AudioFocusRequestClass? _focusRequest;
    private NoisyReceiver? _noisy;
    private volatile NowPlaying? _now;
    private string? _artPath;
    private Bitmap? _art;

    /// <summary>Paused for another app's transient sound, so regaining focus resumes.</summary>
    private bool _pausedByFocusLoss;

    public MediaSessionDriver(string appId, string appName, Action<MediaCommand> raise)
    {
        _channelId = appId;
        _appName = appName;
        _raise = raise;
    }

    private static Context App => Application.Context;

    public bool Active => _session?.Active ?? false;

    public Task StartAsync()
    {
        try
        {
            CreateNotificationChannel();
            _session = new AndroidSession(App, _appName);
            // The handler is not optional: SetCallback without one binds to Looper.myLooper(), and
            // this runs on SDL's thread, which has no Looper — the framework then dereferences null
            // and takes the process down during startup. The main looper always exists.
            _session.SetCallback(new TransportCallback(_raise), new Handler(Looper.MainLooper!));
            _session.Active = true;

            _audio = App.GetSystemService(Context.AudioService) as AudioManager;

            // Headphones unplugged or Bluetooth gone: Android expects every player to pause, or the
            // song carries on out of the phone's speaker.
            _noisy = new NoisyReceiver(() => _raise(new MediaCommand(MediaAction.Pause)));
            App.RegisterReceiver(_noisy, new IntentFilter(AudioManager.ActionAudioBecomingNoisy));

            Current = this;
        }
        catch (Exception)
        {
            // No session means no lock-screen controls — playback itself is unaffected.
        }

        return Task.CompletedTask;
    }

    public void Update(NowPlaying? nowPlaying)
    {
        _now = nowPlaying;
        PublishSession(nowPlaying);

        if (nowPlaying is { Playing: true })
        {
            RequestFocus();
            // On the first play, not at startup: an app that has played nothing has no claim to a
            // foreground exemption, and Android increasingly enforces that.
            EnsureService();
        }

        if (nowPlaying is null) StopService();
        else UpdateNotification();
    }

    /// <summary>Position rides along in the playback state; Android has no separate seek event.</summary>
    public void Seeked(TimeSpan position)
    {
    }

    public void Dispose()
    {
        Current = null;
        StopService();
        AbandonFocus();
        if (_noisy is not null)
        {
            try
            {
                App.UnregisterReceiver(_noisy);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                // Already unregistered.
            }

            _noisy = null;
        }

        if (_session is not null)
        {
            _session.Active = false;
            _session.Release();
            _session = null;
        }

        _art?.Dispose();
        _art = null;
    }

    // ── media session ─────────────────────────────────────────────────────────

    /// <summary>What the lock screen, the always-on display, a watch and a car all read.</summary>
    private void PublishSession(NowPlaying? now)
    {
        if (_session is null) return;
        if (now is null)
        {
            using var stopped = new PlaybackState.Builder().SetState(PlaybackStateCode.Stopped, 0, 0f).Build();
            _session.SetPlaybackState(stopped);
            return;
        }

        using var metadata = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, now.Title)!
            .PutString(MediaMetadata.MetadataKeyArtist, now.Artist)!
            .PutString(MediaMetadata.MetadataKeyAlbum, now.Album)!
            .PutString(MediaMetadata.MetadataKeyAlbumArtist, now.AlbumArtist)!
            .PutLong(MediaMetadata.MetadataKeyTrackNumber, now.TrackNumber)!
            // Milliseconds, and -1 rather than 0 for a live stream: 0 makes the lock screen draw a
            // seek bar that is already at the end.
            .PutLong(MediaMetadata.MetadataKeyDuration,
                now.Duration > TimeSpan.Zero ? (long)now.Duration.TotalMilliseconds : -1)!
            .PutBitmap(MediaMetadata.MetadataKeyAlbumArt, Art(now.ArtPath))!
            .Build();
        _session.SetMetadata(metadata);

        long actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause
                       | PlaybackState.ActionStop;
        if (now.CanNext) actions |= PlaybackState.ActionSkipToNext;
        if (now.CanPrevious) actions |= PlaybackState.ActionSkipToPrevious;
        if (now.Duration > TimeSpan.Zero) actions |= PlaybackState.ActionSeekTo;

        using var playback = new PlaybackState.Builder()
            .SetActions(actions)!
            .SetState(
                now.Playing ? PlaybackStateCode.Playing : PlaybackStateCode.Paused,
                (long)now.Position.TotalMilliseconds,
                // 1 while playing, 0 while paused: lets the system extrapolate the position between
                // updates instead of showing a frozen counter.
                now.Playing ? 1f : 0f)!
            .Build();
        _session.SetPlaybackState(playback);
    }

    /// <summary>
    ///     Cover art, decoded once per path and capped at 512 px — the lock screen never draws it
    ///     bigger, and a full-size scan as a Binder parcel is how a metadata update fails silently.
    /// </summary>
    private Bitmap? Art(string? path)
    {
        if (path == _artPath) return _art;
        _artPath = path;
        _art?.Dispose();
        _art = null;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeFile(path, bounds);
        int sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= 512) sample *= 2;
        using var options = new BitmapFactory.Options { InSampleSize = sample };
        _art = BitmapFactory.DecodeFile(path, options);
        return _art;
    }

    // ── notification ──────────────────────────────────────────────────────────

    /// <summary>
    ///     The media notification for the current state, or null with nothing loaded. The service
    ///     calls this on its way into the foreground, before it has been handed anything.
    /// </summary>
    internal Notification? BuildNotification(Context context)
    {
        var now = _now;
        if (now is null) return null;

        var builder = new Notification.Builder(context, _channelId)
            .SetContentTitle(now.Title)!
            .SetContentText(now.Artist)!
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)!
            .SetLargeIcon(_art)!
            .SetContentIntent(LaunchIntent(context))!
            // A status readout, not news: it must not buzz, and it must not sort itself above real
            // notifications.
            .SetOngoing(now.Playing)!
            .SetOnlyAlertOnce(true)!
            .SetVisibility(NotificationVisibility.Public)!;

        builder.AddAction(Action(context, global::Android.Resource.Drawable.IcMediaPrevious, "Previous",
            TransportReceiver.Previous));
        builder.AddAction(now.Playing
            ? Action(context, global::Android.Resource.Drawable.IcMediaPause, "Pause", TransportReceiver.Toggle)
            : Action(context, global::Android.Resource.Drawable.IcMediaPlay, "Play", TransportReceiver.Toggle));
        builder.AddAction(Action(context, global::Android.Resource.Drawable.IcMediaNext, "Next",
            TransportReceiver.Next));

        // MediaStyle turns three buttons and two lines into the compact media widget; the session
        // token is what ties it to the session, so Android 11+ draws the seek bar and art from it.
        var style = new Notification.MediaStyle();
        style.SetShowActionsInCompactView(0, 1, 2);
        if (_session is not null) style.SetMediaSession(_session.SessionToken);
        builder.SetStyle(style);

        return builder.Build();
    }

    private void UpdateNotification()
    {
        if (!PlaybackService.Running) return;
        if (BuildNotification(App) is not { } notification) return;
        // Posted directly rather than by restarting the service: same id, so it replaces in place.
        (App.GetSystemService(Context.NotificationService) as NotificationManager)?.Notify(NotificationId, notification);
    }

    /// <summary>
    ///     A notification posted to a channel that does not exist is silently dropped. Low
    ///     importance: in the shade and on the lock screen, never a sound.
    /// </summary>
    private void CreateNotificationChannel()
    {
        if (App.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
        var channel = new NotificationChannel(_channelId, "Playback", NotificationImportance.Low)
        {
            Description = $"What {_appName} is playing, with its transport controls."
        };
        channel.SetShowBadge(false);
        channel.EnableVibration(false);
        channel.SetSound(null, null);
        manager.CreateNotificationChannel(channel);
    }

    private static Notification.Action Action(Context context, int icon, string label, string command) =>
        new Notification.Action.Builder(
            global::Android.Graphics.Drawables.Icon.CreateWithResource(context, icon),
            new Java.Lang.String(label),
            TransportIntent(context, command)
        ).Build()!;

    /// <summary>Tapping the notification body returns to the app rather than starting a new copy.</summary>
    private static PendingIntent? LaunchIntent(Context context)
    {
        var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        intent?.AddFlags(ActivityFlags.SingleTop);
        return PendingIntent.GetActivity(context, 0, intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private static PendingIntent? TransportIntent(Context context, string command)
    {
        var intent = new Intent(context, typeof(TransportReceiver)).SetAction(command);
        // A distinct request code per command: PendingIntents with the same code and flags are
        // deduplicated, so sharing one would make every button do whatever the first one did. Fixed
        // numbers, not GetHashCode: string hashes are randomised per process, PendingIntents are not.
        int requestCode = command switch { TransportReceiver.Previous => 1, TransportReceiver.Next => 3, _ => 2 };
        return PendingIntent.GetBroadcast(context, requestCode, intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    internal void OnButton(string command) => _raise(new MediaCommand(command switch
    {
        TransportReceiver.Next => MediaAction.Next,
        TransportReceiver.Previous => MediaAction.Previous,
        _ => MediaAction.PlayPause
    }));

    // ── audio focus ───────────────────────────────────────────────────────────

    /// <summary>
    ///     The right to make sound. An app that ignores focus talks over navigation and phone
    ///     calls, and on modern Android is simply muted.
    /// </summary>
    private void RequestFocus()
    {
        if (_audio is null || _focusRequest is not null) return;

        var attributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Music)!
            .Build()!;

        _focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
            .SetAudioAttributes(attributes)!
            // Same Looper trap as SetCallback above.
            .SetOnAudioFocusChangeListener(new FocusListener(OnFocusChange), new Handler(Looper.MainLooper!))!
            .Build()!;

        _audio.RequestAudioFocus(_focusRequest);
    }

    private void AbandonFocus()
    {
        if (_audio is null || _focusRequest is null) return;
        _audio.AbandonAudioFocusRequest(_focusRequest);
        _focusRequest = null;
    }

    /// <summary>
    ///     A transient loss (a chime, a call) is a pause we undo; a permanent one (another music
    ///     app started) is not — resuming over the app the user just switched to is obnoxious.
    /// </summary>
    private void OnFocusChange(AudioFocus focus)
    {
        switch (focus)
        {
            case AudioFocus.Loss:
                _pausedByFocusLoss = false;
                AbandonFocus();
                _raise(new MediaCommand(MediaAction.Pause));
                break;
            case AudioFocus.LossTransient:
            case AudioFocus.LossTransientCanDuck:
                if (_now is not { Playing: true }) break;
                _pausedByFocusLoss = true;
                _raise(new MediaCommand(MediaAction.Pause));
                break;
            case AudioFocus.Gain:
                if (!_pausedByFocusLoss) break;
                _pausedByFocusLoss = false;
                _raise(new MediaCommand(MediaAction.Play));
                break;
        }
    }

    // ── service ───────────────────────────────────────────────────────────────

    private static void EnsureService()
    {
        if (PlaybackService.Running) return;
        var intent = new Intent(App, typeof(PlaybackService)).SetAction(PlaybackService.ActionStart);
        try
        {
            // StartForegroundService carries a promise — startForeground within ~5 s or the app is
            // killed — which PlaybackService.OnStartCommand keeps on its first statement.
            App.StartForegroundService(intent);
        }
        catch (Java.Lang.Exception ex)
        {
            // ForegroundServiceStartNotAllowedException (API 31+): a backgrounded app without an
            // exemption is refused. Losing background playback is bad; crashing because the user
            // was not looking is worse.
            global::Android.Util.Log.Warn("zigote.mediasession", $"foreground service refused: {ex.Message}");
        }
    }

    private static void StopService()
    {
        if (!PlaybackService.Running) return;
        App.StartService(new Intent(App, typeof(PlaybackService)).SetAction(PlaybackService.ActionStop));
    }

    // ── callbacks ─────────────────────────────────────────────────────────────

    /// <summary>The lock screen, a headset button, a car, a watch — on the main looper.</summary>
    private sealed class TransportCallback(Action<MediaCommand> raise) : AndroidSession.Callback
    {
        public override void OnPlay() => raise(new MediaCommand(MediaAction.Play));
        public override void OnPause() => raise(new MediaCommand(MediaAction.Pause));
        public override void OnStop() => raise(new MediaCommand(MediaAction.Stop));
        public override void OnSkipToNext() => raise(new MediaCommand(MediaAction.Next));
        public override void OnSkipToPrevious() => raise(new MediaCommand(MediaAction.Previous));
        public override void OnSeekTo(long pos) => raise(new MediaCommand(MediaAction.SeekTo, TimeSpan.FromMilliseconds(pos)));
    }

    private sealed class FocusListener(Action<AudioFocus> onChange)
        : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange) => onChange(focusChange);
    }

    private sealed class NoisyReceiver(Action onNoisy) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionAudioBecomingNoisy) onNoisy();
        }
    }
}

/// <summary>
///     Where the notification's buttons land. A broadcast because it must work when the activity is
///     gone, and be cheap — the system gives a receiver seconds and this needs microseconds.
/// </summary>
[BroadcastReceiver(Name = "dev.zigote.plugins.mediasession.TransportReceiver", Exported = false)]
public sealed class TransportReceiver : BroadcastReceiver
{
    internal const string Toggle = "dev.zigote.plugins.mediasession.TOGGLE";
    internal const string Next = "dev.zigote.plugins.mediasession.NEXT";
    internal const string Previous = "dev.zigote.plugins.mediasession.PREVIOUS";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action is { Length: > 0 } command) MediaSessionDriver.Current?.OnButton(command);
    }
}
