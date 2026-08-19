# UrlLauncher

Open URLs in whatever handles them — the `url_launcher` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
UrlLauncherPlugin.Open("https://example.org");            // fire and forget
bool ok = await UrlLauncherPlugin.TryOpenAsync("mailto:x@y.z"); // false = no handler / blank
```

Never throws; blank input and a missing handler both answer `false`. The desktop launch runs on
a worker because `xdg-open` can block the calling frame while the handler starts. Static, so no
`PluginHost.Register`.

| Platform | How |
|---|---|
| Desktop | `Process.Start` with `UseShellExecute` (`xdg-open` / `ShellExecute` / `open`) |
| Android | `ACTION_VIEW` intent from the application context |
| iOS | `UIApplication.OpenUrlAsync` |
