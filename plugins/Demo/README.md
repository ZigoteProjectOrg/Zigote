# Plugins demo

One Adwaita app that exercises every plugin in this folder. Each page calls the plugin the way a
real app would and prints what came back — including "unavailable", which on a laptop is the
honest answer for half of them, and is exactly the behaviour the plugins promise.

```bash
dotnet run --project plugins/Demo/PluginsDemo
```

| Page | Plugins | What to try |
|---|---|---|
| Device | DeviceInfo, PathProvider | read-only facts, no channels involved |
| Battery | Battery | unplug the charger and hit Refresh — the plugin is a snapshot, not a stream |
| Network | Connectivity | pull the cable or toggle Wi-Fi; the change arrives on the event, not on a poll |
| Secrets | SecureStorage | write, read back, delete against the real keyring (`secret-tool` on Linux) |
| Share | Share | desktop answers `Unavailable` and leaves the payload on the clipboard — paste it |
| Files | FilePicker | the native open/save/folder dialogs |
| Links | UrlLauncher, AppLinks | see below |
| Notifications | Notifications | a real desktop notification |
| Location | Geolocation, Sensors | both unavailable on desktop, and both say so instead of throwing |
| Mobile | Push, Biometrics, Haptics, Permissions, AppSettings | the four that only mean something on a phone |

**The deep-link demo.** Start the app, then in another terminal:

```bash
dotnet run --project plugins/Demo/PluginsDemo -- myapp://hello/from/the/shell
```

No second window opens. The second process finds the first over a named pipe, hands it the link
and exits; the running window shows it on the Links page. That is `AppLinksPlugin.StartAsync`
returning false, which is what `Program.Main` acts on.

Signals do the plumbing: plugin callbacks arrive on the OS's thread and write straight to a
`Signal<T>`, and `Watch` marshals the rebuild onto the UI thread.
