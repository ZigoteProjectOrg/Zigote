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
