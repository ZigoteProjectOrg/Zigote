# Zigote

A cross-platform **game engine and application framework** written in C# and F#, running on a
native **Zig + wgpu** rendering backend. Zigote pairs a Flutter-style reactive UI toolkit with a
full 3D engine, a visual editor, and an ECS gameplay layer — all in one .NET 10 solution.

> **The split:** the native half (GPU, windowing, text shaping, physics, ECS, audio, model import)
> lives in Zig and is vendored as the
> [`Zigote.Engine`](https://github.com/ZigoteProjectOrg/Zigote.Engine) submodule, which builds
> `libzigote` and exposes a C ABI. Everything above the GPU — widgets, scenes, scripting, the editor,
> and gameplay — is C#/F# and lives in this repository.

---

## Highlights

- **Pure-wgpu 3D renderer** — a forward+ EEVEE-style pipeline: shadows, SSAO/GTAO, SSR, bloom, AgX
  tonemapping, TAA, and glass refraction, over a backend-agnostic render graph.
- **Flutter-style UI** — a retained-mode widget toolkit with fine-grained reactive state
  (`Signal` / `Computed` / `Effect`), used the same way from **C#** and **F#**.
- **Gameplay stack** — flecs-backed ECS, 3D (Jolt) and 2D physics, a scene/world model, save system,
  and C# **hot reload** for scripts.
- **Visual editor** — scene hierarchy, inspector, asset browser, docked code editor, and node-based
  shader / VFX graphs that codegen to WGSL.
- **Game export** — package and run standalone games via the runtime/player (JIT and AOT).
- **Cross-platform** — macOS, Windows, and Linux on the same SDL3 + wgpu backend
  (see [Platform support](#platform-support)).

## Modules

### Core & runtime
| Project | Purpose |
| --- | --- |
| `Zigote.Core` | Engine core: lifecycle, native FFI (`[LibraryImport]` into `libzigote`), scene graph, assets. |
| `Zigote.Runtime` | Shared runtime host that drives the frame loop for shipped games. |
| `Zigote.Player` | Standalone player that loads and runs an exported game's content bundle. |
| `Zigote.Generators` | Roslyn source generators (FFI bindings, DSL codegen). |

### UI framework
| Project | Purpose |
| --- | --- |
| `Zigote.UI` | Retained-mode widget toolkit: layout, painting, input, reactive state, navigation. |
| `Zigote.UI.Material` | Material Design widget set. |
| `Zigote.UI.Charts` | Swift-Charts-style charting library. |
| `Zigote.UI.BottomSheet` | Draggable, resizable bottom sheets (pub.dev `bottom_sheet` API), design-language agnostic. |
| `Zigote.UI.DevTools` | Widget inspector and debug menu (Flutter DevTools style). |
| `Zigote.UI.Localizations` | i18n framework — locales, plural rules, typed message codegen. |
| `Zigote.UI.FSharp` | F# ergonomics for the reactive core (`signal`/`computed`/`effect`/`watch`) + window bootstrap. The widgets are the C# API, called directly. |
| `Zigote.Modules.UI.CodeEditor` | F# code editor with FParsec-based syntax highlighting. |

### App services
The non-game half of the engine — what an app built on the UI framework needs that a game does not.
| Project | Purpose |
| --- | --- |
| `Zigote.Bloc` | The BLoC pattern: events in, ordered, one at a time; state out as signals. |
| `Zigote.Logging` | Serilog wiring — console, rolling file, DevTools ring, and the failures that are otherwise silent. |
| `Zigote.Reactive.R3` | Bridge between `Signal<T>` and R3 `Observable<T>`, for data layers that want operators. |
| `Zigote.Preferences` | Declarative, reactive, persisted settings. |
| `Zigote.Persistence` | Key-value stores (memory, JSON file) behind one interface. |
| `Zigote.Persistence.SQLite` | The SQLite backing for the above. |

### Rendering, gameplay & graphs
| Project | Purpose |
| --- | --- |
| `Zigote.Render2D` | 2D sprite rendering layer. |
| `Zigote.Cinematics` | Physically-based camera simulation (film-look controls). |
| `Zigote.Vfx` | Particle / VFX system. |
| `Zigote.ECS` | flecs-backed entity-component-system. |
| `Zigote.Physics2D` | 2D physics. |
| `Zigote.World` | Gameplay building blocks — spawning, tags, spatial queries. |
| `Zigote.Save` | Save / load system. |
| `Zigote.Scripting` | Component lifecycle and C# hot reload (Edit & Continue). |
| `Zigote.Game` | Gameplay layer that ties the above together. |
| `Zigote.Network` | Networking and authority/replication. |
| `Zigote.Graphs.*` | Visual node-graph system (`Core`, `Commands`, `Registry`, `Editor`, `Shading`, `Vfx`) powering the shader-graph material builder and VFX graphs. |

### Editor & native
| Project | Purpose |
| --- | --- |
| `Zigote.Editor` | The visual editor application. |
| `Zigote.Engine` | Native Zig + wgpu backend (submodule) — rendering, text, physics, ECS, audio, input. |

### Tests, benchmarks & tooling
`Zigote.Tests`, `Zigote.UI.FSharp.Tests`, `Zigote.UI.Localizations.Tests`, `Zigote.SmokeTest`,
`Zigote.Ecs.Benchmark`; plus `tools/` (icon-font generator) and `build/` (MSBuild targets for font
subsetting and AOT).

## Getting started

**Prerequisites:** the [.NET 10 SDK](https://dotnet.microsoft.com/) (`global.json` pins 10.0.110; C# 14.0 / F# 10.0),
[Zig **0.16.0**](https://ziglang.org/download/) on `PATH` (the solution builds the native engine for
you), and `git`. Release publishing additionally wants a font subsetter (`hb-subset` or
fonttools' `pyftsubset`) — plain builds don't.

```sh
# Clone with the native engine submodule
git clone --recurse-submodules https://github.com/ZigoteProjectOrg/Zigote.git
cd Zigote
# (or, if already cloned) fetch the submodule:
git submodule update --init --recursive

# Build everything — this runs `zig build shared-lib` for the native engine
# automatically (see Zigote.Engine/docs/building.md for native build options)
dotnet build Zigote.sln
```

### Run it

```sh
# Visual editor
dotnet run --project Zigote.Editor

# Smallest complete app — start here if the toolkit is new to you
dotnet run --project Zigote.UI.HelloWorld

# Widget galleries (living examples of the UI toolkit)
dotnet run --project Zigote.UI.Gallery          # C#
dotnet run --project Zigote.UI.FSharp.Gallery   # F#
```

## Platform support

| Platform | Status |
| --- | --- |
| **macOS** (arm64, x64) | Primary development platform — where the engine is built, run, and tested day to day. x64 cross-builds from arm64. |
| **Linux** (x64, arm64) | Builds natively in CI (`linux-x64`) and cross-compiles from any host. arm64 is wired as a target but sees less exercise. |
| **Windows** (x64) | Builds natively in CI (`win-x64`, MSVC ABI). Cross-compiling from macOS/Linux uses Zig's bundled MinGW (GNU ABI). wgpu ships as `wgpu_native.dll` beside `zigote.dll`. |

All platforms share the one SDL3 + wgpu code path — wgpu picks the graphics backend per OS (Metal on
macOS, Vulkan on Linux, D3D12/Vulkan on Windows). Per-OS self-contained editor bundles come from
[`.github/workflows/release.yml`](.github/workflows/release.yml), or locally via
`build/publish.sh <rid>`. Windows and Linux builds are CI-verified but get less real-hardware time
than macOS — platform bug reports are very welcome.

## Examples

- **[`Zigote.UI.HelloWorld`](Zigote.UI.HelloWorld/README.md)** — hello world plus a counter in one
  documented file: the Material shell, a `Signal`, and `Watch` as the bridge to the widget tree. The
  place to start.
- **`Zigote.UI.Gallery` / `Zigote.UI.FSharp.Gallery`** — interactive galleries that exercise the
  widget toolkit, reactive state, theming, navigation, and localization.
- **`examples/PorscheDemo`** — a full 3D driving demo (physics, materials, tracks). It carries large
  binary assets and is **not committed to this repository** (see `.gitignore`); it is distributed
  separately.

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — the engine's structure and core principles.
- [`docs/migration/`](docs/migration/README.md) — **coming from Flutter, Jetpack Compose, SwiftUI, or
  WPF/Avalonia?** Start here: the execution-model differences that matter, per-framework API maps,
  and a cookbook of worked solutions (async loading, large lists, adaptive layout, forms, testing).
- [`Zigote.UI.DevTools/README.md`](Zigote.UI.DevTools/README.md) — the in-app debug overlay
  (**Shift+D**): panels, the reactive counters worth watching, console commands.
- [`docs/preferences-and-persistence.md`](docs/preferences-and-persistence.md) — settings and storage.
- [`docs/mobile-port.md`](docs/mobile-port.md) — Android / iOS bring-up status.

## License

Zigote is released under the [MIT License](LICENSE). Bundled third-party components (fonts, native
libraries) retain their own licenses — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
