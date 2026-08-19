# Notifications

Notifications for Zigote — the `flutter_local_notifications` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md). Transport only: what to show and when to show it
stays in the app.

```csharp
var notifications = new NotificationClient(appId: "dev.zigote.MyApp", appName: "My App");
await notifications.StartAsync();               // never throws; no daemon = silent no-op

notifications.ActionInvoked += key => { };      // non-UI thread — post before touching widgets

notifications.Show(new Notification("Now playing", "Artist — Title") {
    IconPath = coverPath,                       // file path or theme icon name
    Resident = true,                            // stays until Close; buttons don't dismiss
    Category = "x-gnome.music",
    Actions = notifications.SupportsActions
        ? [("previous", "Previous"), ("playpause", "Pause"), ("next", "Next")]
        : [],
});

notifications.Show(next, slot: 0);              // same slot = rewritten in place, no flicker
notifications.Shutdown();                       // on exit: close + bounded wait
```

| Platform | Backend | Actions |
|---|---|---|
| Linux | `org.freedesktop.Notifications` over D-Bus (Tmds.DBus.Protocol) | Yes, when the daemon lists the `actions` capability |
| Android | `NotificationChannel` (id = appId) + `Notification.Builder`; one notification per slot | Not yet — needs a BroadcastReceiver + PendingIntents |
| Windows / macOS / iOS | Not yet (toasts / UNUserNotificationCenter are future work) — every call no-ops | — |

On Android 13+ the OS drops posts silently until `POST_NOTIFICATIONS` is granted — ask through
the Permissions plugin. On Linux the app's `.desktop` entry (named by `appId`) is what gives the
popup its icon and name.
