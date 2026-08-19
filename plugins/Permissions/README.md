# Permissions

Runtime permissions for Zigote — the `permission_handler` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md), sized to what apps actually ask for.

```csharp
if (await PermissionsPlugin.RequestAsync(ZigotePermission.MediaAudio))
    Rescan();

bool held = PermissionsPlugin.IsGranted(ZigotePermission.Notifications);
```

Static API, no `PluginHost.Register`. Requests are **serialized** internally: each one puts a
system dialog on screen, and asking for a second while the first is up silently loses it — so
fire-and-forget several and they queue. Grants are all-or-nothing per capability.

| Platform | Behavior |
|---|---|
| Desktop | Everything granted, no prompts. (Sandboxed camera/mic portals: future work.) |
| Android | Version-gated manifest sets, asked via a throwaway transparent activity. |
| iOS | Not yet — iOS permissions are per-framework prompts (camera prompts on first capture, notifications via UNUserNotificationCenter); add per capability when a consumer needs one. |

## Android manifest

The plugin asks; the **head declares**. Add the lines for the capabilities you use:

| ZigotePermission | `<uses-permission>` |
|---|---|
| Notifications | `android.permission.POST_NOTIFICATIONS` |
| Camera | `android.permission.CAMERA` |
| Microphone | `android.permission.RECORD_AUDIO` |
| MediaAudio | `android.permission.READ_MEDIA_AUDIO` + `android.permission.READ_EXTERNAL_STORAGE` with `android:maxSdkVersion="32"` |
| MediaImages | `android.permission.READ_MEDIA_IMAGES` + the capped READ_EXTERNAL_STORAGE line |
| MediaVideo | `android.permission.READ_MEDIA_VIDEO` + the capped READ_EXTERNAL_STORAGE line |
| LocationWhenInUse | `android.permission.ACCESS_FINE_LOCATION` |

The media sets are version-dependent, not renamed: `READ_MEDIA_*` exist from API 33 and
`READ_EXTERNAL_STORAGE` is capped at 32, so declaring only one of the pair silently fails on
the other side of the line.
