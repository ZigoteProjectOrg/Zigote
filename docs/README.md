# Zigote documentation

The map of everything written down, here and around the repository. If you are new, take the
[repository README](../README.md) first, then [`migration/concepts.md`](migration/concepts.md) —
retained mode is the one idea everything else follows from.

## Using the framework

| Document | What is in it |
|---|---|
| [`../Zigote.UI/README.md`](../Zigote.UI/README.md) | The widget framework in depth: frame phases, widget kinds, invalidation, theming, focus. |
| [`architecture.md`](architecture.md) | How the whole solution fits together — layering, the reactive core, threading, diagnostics. |
| [`migration/`](migration/README.md) | Arriving from Flutter, Compose, SwiftUI or WPF/Avalonia — concepts, per-framework guides, cookbook. |
| [`assets.md`](assets.md) | Fonts, images, content bundling, and publish-time asset & font tree shaking. |
| [`svg.md`](svg.md) | `SvgPicture`, the resvg binding behind it, and compiling SVGs ahead of time. |
| [`preferences-and-persistence.md`](preferences-and-persistence.md) | Reactive persisted settings (`Preference<T>`) and the key-value storage under them. |
| [`mobile.md`](mobile.md) | iOS / Android: what works, how to run the Gallery on both, what is open. |
| [`http.md`](http.md) | `Zigote.Http` — requests as values, the middleware stack, the cache, typed clients, the frame-loop queue. |
| [`plugins.md`](plugins.md) | Platform interop — `PlatformChannel`, the plugin contract, packaging cross-platform and native plugins. |

## The games stack

| Document | What is in it |
|---|---|
| [`games-and-3d.md`](games-and-3d.md) | The 3D renderer, gameplay layer, editor and export pipeline — a separate stack apps never link. |
| [`../Zigote.Engine/docs/`](../Zigote.Engine/docs/README.md) | The native Zig + wgpu backend: rendering, FFI, subsystems, building. |

## Tooling

| Document | What is in it |
|---|---|
| [`profiling.md`](profiling.md) | Profiling and performance — the in-app profiler, the `stats`/`profile` remote tools, the `Zigote-Engine` EventSource for dotTrace/`dotnet-trace`, and the zero-alloc test guards. |
| [`mcp-server.md`](mcp-server.md) | The MCP server — LLM agents launch, drive and screenshot a running app over the inspect protocol. |
| [`../tools/rider/README.md`](../tools/rider/README.md) | The Rider plugin — widget preview, trees, colour swatches — and the inspect protocol behind it. |
| [`../Zigote.UI.DevTools/README.md`](../Zigote.UI.DevTools/README.md) | The in-app DevTools overlay (<kbd>Shift</kbd>+<kbd>D</kbd>). |

## Engineering notes

[`notes/`](notes/) holds design documents and bring-up journals — records of how decisions were
made, kept because the reasoning stays useful. They describe the state of the world *when they were
written*, which the code may since have moved past; the user documentation above is what is kept
current.

| Note | What it records |
|---|---|
| [`notes/fsharp-module-simplification.md`](notes/fsharp-module-simplification.md) | Why the F# VDOM, attr codegen and MVU loop were deleted — and the rules that replaced them (now user-facing in [`Zigote.UI.FSharp/README.md`](../Zigote.UI.FSharp/README.md)). |
| [`notes/devtools-widget-tree.md`](notes/devtools-widget-tree.md) | The design behind the DevTools widget-tree inspector: virtualised rows, rainbow guidelines, live selected-info. |
| [`notes/mobile-port.md`](notes/mobile-port.md) | The iOS/Android bring-up journal — walls hit and how each fell. |
| [`notes/mobile-port-android.md`](notes/mobile-port-android.md) | The Android implementation plan in full detail: libc recipe, SDL patch, the .NET head. |
