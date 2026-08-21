# AppLinks

The links that open your app — OAuth redirects, universal links, custom schemes. The `app_links`
slot from the [plugin roadmap](../../docs/plugin-roadmap.md).

```csharp
if (!await AppLinksPlugin.StartAsync("dev.zigote.MyApp", args)) return;   // handed off — exit
using var links = AppLinksPlugin.Listen(uri => app.Post(() => Route(uri)));
if (AppLinksPlugin.InitialLink is { } cold) Route(cold);
```

Call `StartAsync` before building any UI. On desktop it answers the question that comes first: is
another copy already running? If so, the links from this command line are handed to that copy and
the call returns **false** — exit, because a second window is not what "click a link" means.

| Platform | Single instance | Links |
|---|---|---|
| Desktop | one named pipe per app id: first to listen owns the app, later launches connect and post their links. .NET named pipes are Windows named pipes and Unix domain sockets, so one implementation covers all three | argv, plus the handoff socket |
| Android | the OS handles it (`launchMode`) | launch intent, then `OnNewIntent` → `AppLinksPlugin.Deliver` |
| iOS | the OS handles it | app delegate `OpenUrl` / `ContinueUserActivity` → `AppLinksPlugin.Deliver` |

Native code can also send a link on the `zigote.applinks/link` channel.

Registering the scheme is packaging, not runtime: a `.desktop` file with
`MimeType=x-scheme-handler/myapp`, a registry key, `CFBundleURLTypes`, an intent filter, an
associated-domains entitlement. Only absolute non-`file:` URIs count as links — argv is full of
flags and paths that are none of the app's business.
