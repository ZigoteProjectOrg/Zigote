# Wakelock

Keep the screen awake while something is being watched, followed or played — the
`wakelock_plus` slot from the [plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
WakelockPlugin.Enable();            // video started
WakelockPlugin.Disable();           // video paused or left the screen
WakelockPlugin.Set(isPlaying);      // or straight from a bool — only transitions reach the OS
if (WakelockPlugin.Enabled) { … }
```

It holds the **screen**, not the CPU: a backgrounded app is suspended as usual. Every hold is
owned by the process, so a crash releases it. Static, so no `PluginHost.Register`.

| Platform | How |
|---|---|
| Android | `FLAG_KEEP_SCREEN_ON` on the activity window — no permission, dropped by the OS when the window leaves the foreground |
| iOS | `UIApplication.idleTimerDisabled` |
| macOS | IOKit `PreventUserIdleDisplaySleep` assertion (visible in `pmset -g assertions`) |
| Windows | `PowerCreateRequest` + `PowerRequestDisplayRequired` — not `SetThreadExecutionState`, which lapses with its thread |
| Linux | `org.freedesktop.ScreenSaver.Inhibit` over D-Bus; the hold lives as long as its bus connection |

One global flag: two owners in one app (a video and a game) share it, and the last `Set` wins.
