# Platform interop and plugins

How one shared body of C# talks to five platforms — Windows, macOS, Linux, Android, iOS — and how
that talking gets packaged into reusable plugins, internal and external. Three layers, each one
small:

1. **`PlatformChannel`** — the transport. Named channels between app code and platform code, the
   same shape as a Flutter method channel: a name, a UTF-8 payload, two directions, plus an
   awaitable request/reply for answers that cannot come synchronously.
2. **`IPlatformPlugin` + `PluginHost`** — the contract. A plugin owns channels and platform
   resources, starts when the app starts, stops in reverse order when it ends.
3. **Packaging** — nothing custom. NuGet target-framework multi-targeting selects the right
   per-platform implementation at build time; external native plugins on desktop are a shared
   library with one C entry point.

## The transport: `PlatformChannel`

The shared project cannot reference platform types (`net10.0` code cannot see `Android.App`), so
the two halves meet at a name and a payload. Payloads are bytes with an explicit length — in
practice a short string or a small JSON object, but binary (an image, an audio clip) crosses as-is
with no base64 detour; the transport never interprets them, and errors travel inside the payload
in whatever shape the two ends of a channel agree on.

```csharp
// App code, shared across all platforms:
string? route = PlatformChannel.Invoke("zigote.audio/route");        // ask, answer now
PlatformChannel.Listen("zigote.audio/focus", OnFocusChanged);        // platform → app events
string? reply = await PlatformChannel.Request("zigote.permissions/camera"); // ask, answer later
```

| Call | Direction | When |
|---|---|---|
| `Invoke(name, payload)` | app → platform, synchronous | The platform can answer now ("what is the audio route?"). Null means nobody implements the channel here — a normal answer, carry on without it. |
| `Invoke(name, ReadOnlySpan<byte>, IBufferWriter<byte>)` | app → platform, synchronous | The binary shape of the same call: the payload crosses as-is, the reply lands in your writer, a reused `ArrayBufferWriter` makes it allocation-free. False is the byte-shaped null. |
| `Send(name, payload)` / `Listen` | platform → app, queued | OS events (headset button, focus loss). Arrives on any thread, delivered on the app thread by the frame pump. Listeners multicast; `Listen` returns an `IDisposable` — dispose on detach (or use `Unlisten(name, handler)`). |
| `Request(name, payload, ct)` | app → platform, awaitable | The answer needs a dialog, a picker, an activity result. No built-in timeout — pass a `CancellationToken` if you want one. |
| `Handle(name, handler)` | implement in C# | A platform head written in C# (Android/iOS heads) implements a channel; managed handlers win over native ones. Text-shaped (`Func<string, string?>`) or byte-shaped (`ChannelByteHandler`) — callers reach either transparently, whichever shape they invoke with. |

A request handler receives `"token\npayload"` — split it with `PlatformChannel.ParseRequest`,
return non-null immediately to acknowledge (null declines), and deliver the answer later with
`PlatformChannel.Respond(token, reply)`. From Kotlin/Swift/C the reply is one call:
`zigote_channel_send("zigote/reply", reply, reply_len)` with `reply` being
`"<token>\n<answer>"`.

Native code implements channels through the engine's C ABI (`Zigote.Engine/src/ffi/channel.zig`):
`zigote_channel_register(name, handler)` to answer invokes, `zigote_channel_send(name, payload,
payload_len)` to emit events. Payload pointers always travel with a length and are **not**
guaranteed NUL-terminated — honor `payload_len`, never `strlen`. Reachable from Kotlin/Java via
JNI, from Swift/Objective-C and C/C++ directly. The native registry is fixed-size: at most 32
registered channels, names up to 63 bytes — `register` returns false past either limit. The
managed side has no such limits.

**Naming.** One channel per topic, prefixed by owner: `zigote.audio/route`,
`yourcompany.analytics/event`. The prefix doubles as the plugin's `Name`, which is what
replacement-on-relaunch keys off.

**Threading.** `Invoke`/`Request` are called from the app thread; handlers registered with
`Handle` run on the caller's thread and must not block — a handler that needs a worker reports
back with `Send`. `Send` is safe from any thread. Listeners and request completions run on the app
thread, inside the per-frame `Dispatch`, so they may touch widgets and blocs freely.

## Typed channels

Raw strings are the transport, not the API a plugin should expose. `JsonChannel<TArgs, TReply>`
puts types on both ends using what .NET already has — System.Text.Json **source generation**
(AOT-safe, no reflection, required for iOS publishing) and `async`/`await` in place of result
callbacks. The plugin declares its payload types, one serializer context, and one channel
definition that both sides share:

```csharp
public sealed record ShareArgs(string Text, string? Url);
public sealed record ShareResult(bool Completed);

[JsonSerializable(typeof(ShareArgs))]
[JsonSerializable(typeof(ShareResult))]
internal sealed partial class ShareJson : JsonSerializerContext;

internal static readonly JsonChannel<ShareArgs, ShareResult> Share =
    new("zigote.share/send", ShareJson.Default.ShareArgs, ShareJson.Default.ShareResult);
```

```csharp
// Shared code — one awaitable call, errors arrive as exceptions:
ShareResult? result = await Share.Request(new ShareArgs("Hello", null));

// A managed head implements the same channel with an async handler:
Share.HandleRequests(async args => await ShowShareSheet(args));
```

`Invoke`/`Handle` are the synchronous pair. Arguments serialize straight to UTF-8 and ride the
byte-shaped channel path — no intermediate JSON strings. A faulted request handler surfaces on the
awaiting side as `PlatformChannelException` carrying the handler's message. For platform→app
events there is `JsonEvents<T>` — `Send(T)` on the platform side, `Listen(Action<T>)` returning an
`IDisposable` on the app side.

A **native** implementation of a typed request channel follows the same wire convention the typed
layer uses: reply payload `k<json>` on success, `e<message>` on failure (one prefix character,
then the body). Plain typed `Invoke` carries raw JSON both ways, no prefix.

## The contract: `IPlatformPlugin`

```csharp
public sealed class BatteryPlugin : IPlatformPlugin
{
    public string Name => "zigote.battery";

    public void Start()  // app thread, engine is up, before the first frame
    {
        PlatformChannel.Handle("zigote.battery/level", _ => ReadLevel());
    }

    public void Stop()   // reverse registration order, engine still alive
    {
        PlatformChannel.Unhandle("zigote.battery/level");
    }
}
```

The head registers instances explicitly — no reflection, no assembly scanning (AOT publishing on
iOS forbids it, and an explicit list is shorter than the attribute it would replace):

```csharp
PluginHost.Register(new BatteryPlugin());
using var app = new App("My app");   // App calls PluginHost.StartAll()
```

Rules the host enforces:

- **Order.** Plugins start in registration order and stop in reverse, so later plugins may depend
  on earlier ones.
- **Replacement.** Registering a plugin whose `Name` is already present replaces it (stopping the
  old instance if running) — on Android the process outlives the activity, and a relaunch
  re-registers everything.
- **Late registration.** After `StartAll`, `Register` starts the plugin immediately, so a plugin
  loaded on demand behaves like one registered at startup.
- **Failure.** A plugin that throws in `Start` fails startup loudly; a plugin that throws in
  `Stop` does not stop the rest of shutdown.
- **Lifecycle.** A plugin that also implements `IAppLifecycleObserver` is wired into app
  lifecycle delivery automatically for as long as it runs — pause/resume/low-memory arrive with
  no subscription code, the way a .NET host picks up the optional interfaces a hosted service
  chose to implement. (`PluginHost.PluginStarted`/`PluginStopped` are the hooks the app uses;
  other layers can watch them for their own capability interfaces.)

## Packaging a cross-platform plugin

Don't hand-build the layout — the CLI generates it, example app included:

```
zigote create plugin BatteryLevel --platforms android,ios
```

A plugin package is one project that multi-targets; NuGet and the SDK pick the implementation that
matches the consuming head. No loader, no manifest:

```
YourCompany.Zigote.Battery/
├── YourCompany.Zigote.Battery.csproj   TargetFrameworks: net10.0;net10.0-android;net10.0-ios
├── BatteryPlugin.cs                    shared API: calls the channels, defines the payloads
├── Platforms/
│   ├── Android/BatteryChannels.cs     net10.0-android only: Handle() over Android.OS.BatteryManager
│   ├── iOS/BatteryChannels.cs         net10.0-ios only: Handle() over UIDevice
│   └── Desktop/BatteryChannels.cs     net10.0: /sys/class/power_supply, IOKit, Win32 — or nothing
```

The shared `net10.0` compilation is what desktop heads get; the Android/iOS compilations replace
the desktop channel implementations with platform ones behind the same channel names. A platform
where nobody registers the channel simply answers null — the shared API's callers already handle
"not here". Kotlin/Java or Swift sources ride along the usual .NET for Android / .NET for iOS
mechanisms (AndroidJavaSource, xcframework bindings) when the implementation needs them.

Internal systems (audio player, notifications, media session) follow exactly the same pattern
inside this repository — the only difference is that nobody packs a nupkg.

## External native plugins (desktop)

For SDKs written in C/C++/Zig/Rust, a plugin is a shared library the app loads at runtime:

```csharp
PluginHost.LoadNative("plugins/libdiscord_bridge.so");
```

The library exports one required and one optional symbol:

```c
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* payload is payload_len bytes, NOT guaranteed NUL-terminated (it may be binary). Write the
 * reply into reply[0..reply_cap) and return the length written — or the length needed when it
 * does not fit (the host retries once with a bigger buffer), or negative for "failed". */
typedef int32_t (*zigote_channel_handler)(
    const uint8_t* payload, size_t payload_len, uint8_t* reply, size_t reply_cap);

typedef struct ZigoteHostApi {
    uint32_t version;  /* currently 1; fields only ever get appended */
    bool (*channel_register)(const char* name, zigote_channel_handler handler);
    void (*channel_unregister)(const char* name);
    bool (*channel_send)(const char* name, const uint8_t* payload, size_t payload_len);
    bool (*channel_has)(const char* name);
} ZigoteHostApi;

static ZigoteHostApi host;  /* the pointer is only valid during init — copy it */

bool zigote_plugin_init(const ZigoteHostApi* api) {
    if (api->version < 1) return false;
    host = *api;
    return host.channel_register("discord/presence", on_presence);
}

void zigote_plugin_shutdown(void) {          /* optional but strongly recommended */
    host.channel_unregister("discord/presence");
}
```

A plugin without `zigote_plugin_shutdown` is never unloaded — freeing a library whose handlers
are still registered would leave the registry pointing into unmapped code.

Desktop only, by nature: iOS forbids loading code at runtime, and on Android native code arrives
through the head's own build (an `.aar`, `jniLibs`) and registers its channels itself at startup.
