# Platform Channels

One body of shared C# runs on Windows, macOS, Linux, Android and iOS. Platform-specific code (Kotlin on Android, Swift on iOS, C, Zig, or C# inside a platform head) talks to it through **platform channels**: named pipes that carry a payload in both directions. If you know Flutter's method channels, this is the same idea.

| Call | Direction | Use when |
|---|---|---|
| `PlatformChannel.Invoke(name, payload)` | app → platform, synchronous | The platform can answer now ("what is the audio route?") |
| `PlatformChannel.Request(name, payload)` | app → platform, awaitable | The answer needs a dialog, a picker, an OS callback |
| `PlatformChannel.Send(name, payload)` + `Listen` | platform → app | Events: headset button, focus loss, a push message |
| `PlatformChannel.Handle(name, handler)` | implement a channel | In a platform head written in C#, or in a desktop implementation |

Payloads are UTF-8 strings (in practice short text or a small JSON object) or raw bytes, which cross as-is with no base64. A channel nobody implements answers `null`, which is a normal answer: "not available here, carry on without it".

## Invoke: ask and answer now

Here the "platform side" is a C# handler registered in the same process. On a phone it would be Kotlin or Swift registered with the engine, and the calling code would not change at all.

```csharp run height=240
class Demo : ComposedWidget
{
    private readonly Signal<string> _answer = new("—");

    protected override void OnMount()
    {
        // The platform side: implements "demo.math/add". Payload "3,4" → reply "7".
        PlatformChannel.Handle("demo.math/add", payload =>
        {
            var parts = payload.Split(',');
            return (int.Parse(parts[0]) + int.Parse(parts[1])).ToString();
        });
    }

    protected override void OnUnmount() => PlatformChannel.Unhandle("demo.math/add");

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            new AdwButton(label: "Invoke demo.math/add(20, 22)", onPressed: () =>
                _answer.Value = PlatformChannel.Invoke("demo.math/add", "20,22") ?? "(not implemented here)"),
            new AdwButton(label: "Invoke a channel nobody implements", onPressed: () =>
                _answer.Value = PlatformChannel.Invoke("demo.nothing/here") ?? "null: not available on this platform"),
            new Watch(() => new Label(_answer.Value, fontSize: 20)),
        ]));
}
```

## Listen: events from the platform

OS events arrive on whatever thread the OS chose. `Send` is safe from any thread; listeners run on the UI thread, during the frame's dispatch, so they may touch widgets and signals directly. `Listen` returns an `IDisposable`; tie it to the widget with `Own`.

```csharp run height=220
class Demo : ComposedWidget
{
    private readonly Signal<string> _last = new("Waiting for events…");
    private readonly Signal<int> _count = new(0);

    protected override void OnMount()
    {
        Own(PlatformChannel.Listen("demo.sensor/reading", reading =>
        {
            _count.Value++;
            _last.Value = $"Reading: {reading}";     // UI thread: safe to touch anything
        }));

        // Stand-in for the OS: a background thread sending events. On a phone this is a
        // Kotlin/Swift sensor callback calling zigote_channel_send.
        var cts = new CancellationTokenSource();
        Own(cts);
        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(700);
                PlatformChannel.Send("demo.sensor/reading", $"{Random.Shared.NextDouble() * 40:0.0} °C");
            }
        });
    }

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 10, children: [
            new Watch(() => new Label(_last.Value, fontSize: 24)),
            new Watch(() => new Label($"{_count.Value} events received", fontSize: 13)),
        ]));
}
```

## Request: answers that take time

Some answers can't come synchronously: a permission prompt, a file picker, a purchase sheet. `Request` returns a `Task`. The handler receives `"token\npayload"`, acknowledges immediately by returning non-null, and answers later with `Respond(token, reply)`, from any thread.

```csharp run height=240
class Demo : ComposedWidget
{
    private readonly Signal<string> _state = new("Not asked yet");

    protected override void OnMount() =>
        PlatformChannel.Handle("demo.permissions/camera", envelope =>
        {
            var (token, _) = PlatformChannel.ParseRequest(envelope);
            // Stand-in for an OS permission dialog the user answers 1.5 s later.
            Task.Delay(1500).ContinueWith(_ => PlatformChannel.Respond(token, Random.Shared.Next(2) == 0 ? "granted" : "denied"));
            return "";                                   // non-null = "accepted, answer follows"
        });

    protected override void OnUnmount() => PlatformChannel.Unhandle("demo.permissions/camera");

    private async void Ask()
    {
        _state.Value = "Asking the user…";
        string? answer = await PlatformChannel.Request("demo.permissions/camera");
        _state.Value = answer is null ? "No permission system on this platform" : $"Camera permission: {answer}";
    }

    protected override Widget Build(BuildContext context) =>
        new Center(new Column(mainAxisSize: MainAxisSize.Min, spacing: 12, children: [
            new AdwButton(label: "Request camera permission", onPressed: Ask) { Style = AdwButtonStyle.Suggested, Pill = true },
            new Watch(() => new Label(_state.Value, fontSize: 18)),
        ]));
}
```

There is no built-in timeout, deliberately: a permission dialog may stay on screen for minutes. Pass a `CancellationToken` if you want one.

## Typed channels

Strings are the transport, not the API. `JsonChannel<TArgs, TReply>` puts types on both ends using System.Text.Json source generation (AOT-safe, required for iOS) and async/await:

```csharp
public sealed record ShareArgs(string Text, string? Url);
public sealed record ShareResult(bool Completed);

[JsonSerializable(typeof(ShareArgs))]
[JsonSerializable(typeof(ShareResult))]
internal sealed partial class ShareJson : JsonSerializerContext;

internal static readonly JsonChannel<ShareArgs, ShareResult> Share =
    new("acme.share/send", ShareJson.Default.ShareArgs, ShareJson.Default.ShareResult);

// Shared code: one awaitable call, failures arrive as PlatformChannelException.
ShareResult? result = await Share.Request(new ShareArgs("Hello", null));

// A C# platform head implements it:
Share.HandleRequests(async args => await ShowShareSheet(args));
```

`JsonEvents<T>` is the typed form of `Send`/`Listen`. (Source-generated serializers need a compiler with source generators, which this playground's in-app compiler doesn't run, so this one isn't live.)

## From native code

Kotlin, Swift, C and Zig reach the same registry through the engine's C ABI:

```c
// Answer invokes:
zigote_channel_register("acme.battery/level", on_level);   // int32 (*)(payload, len, reply, cap)
// Emit events:
zigote_channel_send("acme.battery/changed", payload, payload_len);
// Answer a Request later:
zigote_channel_send("zigote/reply", "<token>\n<answer>", len);
```

Payloads always travel with a length and are **not** NUL-terminated, so never use `strlen`. The native registry holds up to 32 channels with names up to 63 bytes; the managed side has no limits.

## Conventions

- **One channel per topic, prefixed by owner:** `zigote.audio/route`, `acme.analytics/event`. The prefix doubles as the plugin's name.
- **Handlers must not block.** One that needs a worker should answer through `Send` or `Respond` later.
- **Treat `null` as "not here"** and degrade gracefully. Desktop builds often have no implementation for phone-only features.
- **Errors travel in the payload**, in whatever shape the two ends agree on (typed channels turn them into exceptions).
