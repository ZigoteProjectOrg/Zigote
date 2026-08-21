# Push

Remote notifications — the `firebase_messaging` slot from the
[plugin roadmap](../../docs/plugin-roadmap.md), deliberately **without a Firebase dependency**.

```csharp
string? token = await PushPlugin.RegisterAsync(ct);   // send it to your server
using var messages = PushPlugin.OnMessage(m => app.Post(() => Route(m)));
using var tokens   = PushPlugin.OnToken(t => api.UpdateDeviceToken(t));
```

The plugin owns the app-facing half — registration, the device token, the message stream. The
transport half is a **contract**, because it has to be: Android push means FCM, FCM means the
Firebase SDK and `google-services.json` in your own build, and no plugin can vendor that. Anything
that can reach a `PlatformChannel` feeds the plugin:

```
zigote.push/token     payload: the device token
zigote.push/message   payload: {"title":…,"body":…,"tapped":false,"data":{"k":"v"}}
```

A payload that is not a JSON object is taken as a bare body, so a minimal transport can send a
string. Non-string data values keep their JSON text rather than being dropped.

| Platform | What the plugin does | What you wire |
|---|---|---|
| Android | receives on the two channels | your `FirebaseMessagingService` (or HMS, or your own socket) sends `onNewToken` / `onMessageReceived` onto them — ~10 lines of Kotlin |
| iOS | asks for authorization and calls `RegisterForRemoteNotifications` | one line in the app delegate (below) |
| Desktop | `Available` is false | the channel contract still works if you have your own transport |

```csharp
// iOS head — the token is delivered to the app delegate, not to the plugin:
public override void RegisteredForRemoteNotifications(UIApplication app, NSData token)
    => PushPlugin.DeliverToken(Convert.ToHexString(token.ToArray()).ToLowerInvariant());
```

`Token` is remembered and replayed to late `OnToken` listeners, and re-delivering the same token
is silent — tokens get reissued, and a server holding a stale one silently stops delivering.
