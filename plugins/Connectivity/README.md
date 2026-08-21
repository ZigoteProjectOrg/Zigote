# Connectivity

Network reachability and change events — the `connectivity_plus` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
Connection now = ConnectivityPlugin.Current;
if (now.IsCellular) AskBeforeDownloading();

using var subscription = ConnectivityPlugin.Listen(c => app.Post(() => ShowBanner(c.Online)));
```

`Online` means "there is a route out", not "the internet answers" — a captive portal reads as
online. Whether a particular server responds is a request that works or does not, and that is
Zigote.Http's business. Callbacks arrive on the OS's thread: post before touching widgets.
The platform watcher starts with the first listener and stops with the last; static, so no
`PluginHost.Register`.

| Platform | How |
|---|---|
| Desktop | `System.Net.NetworkInformation` — interfaces that are up and have a gateway, plus `NetworkChange` events. Wired beats Wi-Fi beats cellular when several are up |
| Android | `ConnectivityManager` capabilities + a default-network callback. **Needs `ACCESS_NETWORK_STATE` in the app manifest** — without it every reading is offline |
| iOS | `NWPathMonitor` |
