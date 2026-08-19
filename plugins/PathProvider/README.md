# PathProvider

Well-known directories for Zigote apps — the `path_provider` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
PathProviderPlugin.AppName = "timbre";          // optional; defaults to the entry assembly name
PathProviderPlugin.Data();                       // ~/.local/share/timbre (XDG_DATA_HOME honored)
PathProviderPlugin.Cache("covers");              // ~/.cache/timbre/covers
PathProviderPlugin.Config();                     // ~/.config/timbre
PathProviderPlugin.Documents();                  // localized via user-dirs.dirs on Linux
PathProviderPlugin.Downloads();
PathProviderPlugin.Temp();
```

Paths are returned, never created — call `Directory.CreateDirectory` before the first write.
Static facts, so no `PluginHost.Register`.

| Platform | Data / Cache / Config | Documents / Downloads |
|---|---|---|
| Linux | XDG base dirs + AppName | `user-dirs.dirs` (localized), `~/Documents`·`~/Downloads` fallback |
| Windows | Roaming / Local AppData + AppName (no config split) | known folders |
| macOS | `~/Library/Application Support`·`Caches`·`Preferences` + AppName | `~/Documents`·`~/Downloads` |
| Android | `filesDir` / `cacheDir` / `files/config` (sandbox — no AppName) | app-specific external storage |
| iOS | sandbox `Library/…` | sandbox `Documents` |
