# UrlLauncher

Open URLs in whatever handles them — the `url_launcher` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
UrlLauncherPlugin.Open("https://example.org");            // fire and forget
bool ok = await UrlLauncherPlugin.TryOpenAsync("mailto:x@y.z"); // false = no handler / blank
```

Only absolute URLs with an allowed scheme are opened — `http`, `https`, `mailto`, `tel` and `sms`
by default. The platform launcher runs whatever it is handed (on a desktop a file path, a `file:`
URL or a UNC path starts a program), and links usually come from content the app did not write, so
anything else answers `false` without reaching it. To open another app's deep link, name its
scheme:

```csharp
UrlLauncherPlugin.Open("myapp://callback", allowedSchemes: new HashSet<string> { "myapp" });
```

Never throws; blank input, a refused scheme and a missing handler all answer `false`. The desktop launch runs on
a worker because `xdg-open` can block the calling frame while the handler starts. Static, so no
`PluginHost.Register`.

| Platform | How |
|---|---|
| Desktop | `Process.Start` with `UseShellExecute` (`xdg-open` / `ShellExecute` / `open`) |
| Android | `ACTION_VIEW` intent from the application context |
| iOS | `UIApplication.OpenUrlAsync` |
