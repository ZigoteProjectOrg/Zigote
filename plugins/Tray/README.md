# Tray

Status-area icon for Zigote desktop apps — the tray_manager slot from the
[plugin roadmap](../../docs/plugin-roadmap.md). Lifted from Timbre's proven implementation.

```csharp
using var icon = await ZigoteTray.CreateAsync(
    appId: "dev.zigote.MyApp",   // desktop entry / hicolor theme icon name (Linux)
    title: "My App",
    tooltip: "My App",
    onSelect: tag => app.Post(() => OnMenu(tag)),   // callbacks arrive off the UI thread — post!
    onActivate: () => app.Post(ShowWindow));
if (icon is null) { /* no status area here — carry on without one */ }

icon.SetMenu([
    new TrayMenuItem(1, "Play"),
    TrayMenuItem.Separator,
    new TrayMenuItem(2, "Quit"),
]);
icon.SetTooltip("Now playing…");
```

| Platform | Backend |
|---|---|
| Windows | `Shell_NotifyIcon` (engine's `TrayIcon`) |
| macOS | `NSStatusItem` (engine's `TrayIcon`) |
| Linux | `org.kde.StatusNotifierItem` + `com.canonical.dbusmenu` over D-Bus (this package, on Tmds.DBus.Protocol) |

Degrades to `null`, never throws: plain GNOME without the AppIndicator extension has no status
area, and that is a normal desktop. `StatusNotifierItem.LastError` says why when you need to know.
`ITrayIcon`/`TrayMenuItem` come from `Zigote.Core.Engine` — this package adds the Linux backend
and the one-call chooser.
