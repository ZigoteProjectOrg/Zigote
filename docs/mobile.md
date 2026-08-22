# iOS & Android

Mobile is **in bring-up**: the platform layer works and the Gallery runs on both the iOS simulator
and the Android emulator, but neither is a shipped target yet. This page is the honest state of it —
what works, how to run it, and what is still open. The full engineering record, decision by
decision, lives in [`notes/mobile-port.md`](notes/mobile-port.md) and
[`notes/mobile-port-android.md`](notes/mobile-port-android.md).

## What works today

| Layer | State |
|---|---|
| **Touch** | End-to-end: finger events through the engine into pooled `TouchEvent`s, primary-finger promotion with slop, drag-to-scroll with fling, long-press → context menus, pointer cancel. A drag is arbitrated **once**, at the slop — a scrub control inside a scrolling page must claim it by overriding `Widget.CanTouchDrag`, or the page takes it. |
| **Lifecycle** | Background/foreground/low-memory arrive as `AppLifecycleState`; override `ZigoteApp.OnPause` / `OnResume` / `OnLowMemory`. A paused app stops layout, paint and present entirely while still draining events. |
| **Safe area** | The `SafeArea` widget insets by the real device values via `MediaQueryData.Padding`, re-queried on resize and rotation. |
| **iOS simulator** | The Gallery runs at 60 fps on the iPhone simulator. |
| **Android emulator** | The Gallery runs on the API-34 arm64 emulator; `zigote add android` scaffolds the head for your own app. |
| **Game export** | The editor's exporter produces `iossimulator-arm64` and `android-arm64` player bundles (JIT); the 3D test scene runs on both. |

## Running the Gallery

**iOS simulator** (needs Xcode and the `ios` .NET workload):

```sh
dotnet build Zigote.UI.Gallery.iOS -p:ZigTargetRid=iossimulator-arm64
xcrun simctl install booted <built .app>   # then launch com.zigote.gallery
```

The native engine must be built `ReleaseFast` or `ReleaseSafe` for iOS — debug builds reference a
symbol the simulator runtime does not export.

**Android emulator** (needs the NDK and `dotnet workload install android`):

```sh
dotnet build mobile/android -t:Run -f net10.0-android
```

For your own app, `zigote add android` generates the head. **3D content on the emulator needs a
host-GPU Vulkan ICD**: emulator 36+ launched with `ANDROID_EMU_VK_ICD=moltenvk … -gpu host` on
Apple Silicon — the default software Vulkan dies under real 3D load. The plain widget Gallery does
not need this.

## The loop on a real device

`zigote device` is the day-to-day loop, and it exists because three things have to agree before an
edit reaches a phone — the RID (which selects the managed target *and* cross-compiles the engine),
the head's hot-reload switch, and which adb every step talks to. Getting one wrong produces an app
that installs and dies, so the command reads them off the device instead:

```sh
zigote device            # what is attached
zigote device run        # deploy to it, then reload edits into the running app
zigote device logs       # this app's logcat and nothing else
```

`run` reads the device's ABI (`ro.product.cpu.abi`), picks the matching RID, builds the head with
`-p:ZigoteHotReload=true`, and hands the whole thing to `dotnet watch`. From there the Android
workload does the rest: it passes the app a startup hook and a websocket endpoint, `adb reverse`s
the port, and the delta applier in the app receives each metadata update. Zigote's own bridge picks
it up — `ZigoteHotReloadHandler` flags the frame, `App.Frame` re-runs every `Build()` in the tree,
and widget instances (and the state in their fields) survive. **Saving is what triggers it**: in
Rider use **Tools → Run on Device (Zigote)**, which runs the same command and saves your edits for
you a moment after you stop typing — Rider otherwise only saves on window deactivation, which never
happens while you edit and watch the phone.

> **Reload needs .NET SDK 10.0.300 or newer.** Deploy, run and logs work on any 10.0 SDK; the delta
> channel does not. The Android workload has its half (`Microsoft.Android.Sdk.HotReload.targets`),
> but the variables that drive it are written by `dotnet watch`, and that landed later
> ([dotnet/sdk#52581](https://github.com/dotnet/sdk/pull/52581)). On an older SDK a watch session
> still builds, deploys and runs — it just never sends a delta, and nothing says so. `zigote device
> run` checks the SDK and says so, then deploys without the watcher. `global.json` rolls forward to
> the latest feature band, so installing a newer SDK is the whole fix.

What hot reload cannot do on a device it also cannot do on desktop: constructors, field initialisers
and `OnMount` run once per mount, and a rude edit (a new field, a changed signature) is refused by
the runtime. Those need a redeploy, which is why the switch also turns on Fast Deployment — the
fallback costs seconds instead of a fresh apk.

The switch is opt-in for one reason: Mono ignores every delta unless
`DOTNET_MODIFIABLE_ASSEMBLIES` is set, and the Android SDK writes it only for an **interpreted**
debug build. An interpreted frame loop does not hold 60 fps on a phone, so a plain
`dotnet run`/`--no-reload` stays JIT and stays fast, and you opt into the slower frames only while
you are reshaping UI. `--release` implies `--no-reload`.

For a debugger rather than a reload, `zigote device run --debug` forwards the Mono soft-debugger
port (10000 by default, `--debug-port` to change it) and starts the app suspended on it, so a
debugger can attach before any app code has run.

Two export gotchas that look like bugs and are not: simulator RIDs must go through `dotnet build`
(`publish` is device-only), and Android Release builds need `RunAOTCompilation=false`.

## Still open

- **iOS hardware.** The `ios-arm64` static-link path is generated but unverified on a device —
  signing and a test device are the missing pieces.
- **Soft keyboard geometry.** The keyboard shows on focus, but its height is not yet wired into
  `MediaQueryData.ViewInsets`, so nothing scrolls out of its way.
- **Desktop-only code paths** (file dialogs, process launching, native menus) still need platform
  guards before a store submission would pass.
- **Screen readers** — the platform-neutral semantics tree exists, but no VoiceOver/TalkBack bridge
  does. This is true on desktop too; see the main [README](../README.md#platforms).

Touch behaviour is testable without hardware: the engine promotes any touchscreen's events on
desktop, and on a Mac you can drive `App.DispatchTouchEvent` directly to reproduce gesture bugs.
