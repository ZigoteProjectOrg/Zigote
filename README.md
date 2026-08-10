<div align="center">

# Zigote

**Native applications and games for .NET — with the renderer in the box.**

A retained-mode UI toolkit, a 3D engine and a visual editor in one .NET 10 solution.
No web view, no OS toolkit underneath, no XAML — widgets, layout, text and paint are the
framework's own, drawn by a Zig + wgpu backend.

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Zig 0.16](https://img.shields.io/badge/Zig-0.16.0-F7A41D)](https://ziglang.org/)
[![Platforms](https://img.shields.io/badge/platforms-macOS%20%7C%20Linux%20%7C%20Windows-informational)](#platform-support)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[Quick start](#quick-start) · [Adwaita](#adwaita--a-first-class-design-system) ·
[Reactivity](#the-reactive-core) · [Architecture](#architecture) · [Modules](#modules) ·
[Migrating](#migrating-from-another-toolkit) · [Docs](#documentation)

<img src="docs/images/adwaita-welcome.png" alt="The Adwaita gallery running on macOS" width="900">

<sub>`Zigote.UI.Adwaita.Gallery` — 37 live pages of the GNOME design system, rendered by Zigote's own widget tree.</sub>

</div>

---

## What Zigote is

Two halves, one seam. The **native half** — GPU, windowing, text shaping, physics, ECS, audio, model
import — is Zig and lives in the [`Zigote.Engine`](https://github.com/ZigoteProjectOrg/Zigote.Engine)
submodule, which builds `libzigote` and exposes a C ABI. Everything above the GPU — widgets, layout,
scenes, scripting, the editor, gameplay — is C# and F# and lives in this repository.

Nothing is delegated to the platform's toolkit. Text is shaped with HarfBuzz and rasterised with
FreeType inside the engine, so a window looks the same on macOS, Linux and Windows; wgpu picks Metal,
Vulkan or D3D12 underneath without the code above knowing.

Two things make the framework itself distinctive:

- **Retained mode.** `Build` runs **once**. The tree it returns is kept and mutated — hover, focus,
  caret and scroll live on the widget instances. There is no per-frame diff.
- **Fine-grained reactivity.** `Signal<T>` / `Computed<T>` / `Effect` drive the tree through `Watch`,
  which re-runs one subtree — the one that read the signal — and not a page.

---

## Quick start

**Prerequisites:** the [.NET 10 SDK](https://dotnet.microsoft.com/) (`global.json` pins 10.0.110 —
C# 14, F# 10), [Zig **0.16.0**](https://ziglang.org/download/) on `PATH`, and `git`. Release
publishing also wants a font subsetter (`hb-subset` or fonttools' `pyftsubset`); plain builds do not.

```sh
git clone --recurse-submodules https://github.com/ZigoteProjectOrg/Zigote.git
cd Zigote
# already cloned without submodules?  git submodule update --init --recursive

# Builds the native engine (zig build shared-lib) and the whole managed solution.
dotnet build Zigote.sln
```

```sh
dotnet run --project Zigote.UI.Adwaita.Gallery   # the GNOME widget set, 37 pages
dotnet run --project Zigote.UI.HelloWorld        # smallest complete app — start here
dotnet run --project Zigote.UI.Gallery           # the core toolkit: charts, i18n, navigation
dotnet run --project Zigote.UI.FSharp.Gallery    # the same toolkit from F#
dotnet run --project Zigote.Editor               # the scene editor
```

Every one of those but the Adwaita gallery references `Zigote.UI.DevTools`, so
<kbd>Shift</kbd>+<kbd>D</kbd> opens the [debug overlay](Zigote.UI.DevTools/README.md) —
inspector, frame timeline and live reactive counters.

---

## Adwaita — a first-class design system

`Zigote.UI.Adwaita` is a full re-implementation of the **GNOME Adwaita** design language on Zigote's
widget kernel: **83 `Adw*` types**, the two appearances, the nine GNOME 47 system accents, the
boxed-list row vocabulary, adaptive navigation, and client-side decorations the app draws itself.

It is not a GTK binding. Nothing links against GTK, GLib or libadwaita — the widgets are Zigote
widgets, so an Adwaita app builds and runs unchanged on macOS and Windows, which is where the
screenshots on this page were taken.

<table>
<tr>
<td width="50%"><img src="docs/images/adwaita-buttons.png" alt="Buttons page: style classes, shapes, button content and split buttons"></td>
<td width="50%"><img src="docs/images/adwaita-preferences.png" alt="Preferences dialog with appearance and accent settings"></td>
</tr>
<tr>
<td><sub><b>Style classes.</b> One <code>AdwButton</code>, the whole GNOME vocabulary: suggested, destructive, flat, pill, circular, compact, split.</sub></td>
<td><sub><b>Preferences.</b> <code>AdwPreferencesDialog</code> with real pages and groups. Changing the accent rebuilds a <code>ThemeData</code> and every open window follows.</sub></td>
</tr>
<tr>
<td><img src="docs/images/adwaita-bottom-sheet.png" alt="Bottom sheet page with a drag-up sheet over the content"></td>
<td><img src="docs/images/adwaita-reactivity.png" alt="Reactivity page showing signals, a computed subtotal and recompute counts"></td>
</tr>
<tr>
<td><sub><b>Sheets and overlays.</b> A draggable <code>AdwBottomSheet</code> over the page, plus toasts, banners, alert dialogs and popovers — animated by the framework's own transitions.</sub></td>
<td><sub><b>Reactivity, on screen.</b> Rows write one signal each, a <code>Computed</code> caches the subtotal, and the page counts its own recomputes.</sub></td>
</tr>
</table>

### The window belongs to the app

`AdwaitaApp` decides window chrome from the desktop it is on, and an Adwaita header bar *is* the
titlebar:

| Host | Chrome |
| --- | --- |
| **GNOME** | Client-side decorations. The window buttons are drawn by `AdwWindowControls` inside your header bars, honouring the system `button-layout` — which buttons exist, in which order, on which side. |
| **macOS** | Client-side decorations, with the traffic lights drawn in the header bar and vertically centred the way macOS centres them. |
| **Windows, KDE, other** | System decorations, Adwaita content. |

Corners are rounded through a real alpha-composited window (and squared automatically while
maximized or fullscreen), from the same `AdwMetrics.WindowRadius` that dialogs and sheets round to.

### Theming: two appearances × nine accents, live

On GNOME the app tracks `color-scheme` and `accent-color` as they change — through `gsettings`
normally, or `org.freedesktop.portal.Settings` inside a Flatpak or Snap sandbox, where the host's
dconf is not reachable. Elsewhere it starts in the light appearance and does what you tell it.

```csharp
// Follows the desktop appearance and accent for as long as it runs.
var app = new AdwaitaApp(new Shell(), title: "My App");
app.SystemStyleChanged += () => Log(app.SystemPrefersDark, app.SystemAccent);

// Or pin one of the nine hues and ignore the desktop entirely.
new AdwaitaApp(new Shell(),
               theme: AdwTheme.Create(AdwAccent.Purple, dark: true),
               followSystem: false).Run();

// Extra OS windows inherit the chrome and re-theme with the app.
app.OpenWindow(new Shell(), "Second Window");
```

An accent is a whole `ThemeData`, not a tint applied late — which is why switching one repaints every
open window in a frame.

### Adaptive by construction

<img src="docs/images/adwaita-narrow.png" alt="The gallery at phone width, folded to a single page with a back button" align="right" width="270">

The same tree serves a phone width and a desktop one:

- `AdwNavigationSplitView` — sidebar + content that **folds into one navigable page** below a
  breakpoint, growing a back button on the way.
- `AdwOverlaySplitView` — the sidebar becomes an overlay instead of a column.
- `AdwBreakpointBin` / `AdwMultiLayoutView` — swap whole layouts on a size condition.
- `AdwClamp` — reading-width content inside a wide window.
- `AdwWrapBox` — children that flow onto new lines.

```csharp
new AdwNavigationSplitView {
    Sidebar = pages,
    Content = detail,
    AutoCollapseBelow = 620f,   // the gallery's phone breakpoint
}
```

<br clear="right">

### A complete app

This compiles as written — an app, a header bar, a boxed list, and one signal on screen:

```csharp
using Zigote.Core.State;
using Zigote.UI.Adwaita;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;

new AdwaitaApp(new CounterPage(), title: "Counter").Run();

sealed class CounterPage : ComposedWidget
{
    private readonly Signal<int> _count = new(0);
    private readonly Signal<bool> _big = new(false);

    protected override Widget Build(BuildContext context) =>
        new AdwToolbarView(
            new AdwClamp(
                new AdwPreferencesGroup("Counter", "One signal, two rows and a header") {
                    Rows = {
                        new AdwActionRow("Count", "Activate the row to increment") {
                            OnActivated = () => _count.Value += _big.Value ? 10 : 1,
                            Suffixes    = { new Watch(() => new Label($"{_count.Value}")) },
                            ShowChevron = true,
                        },
                        new AdwSwitchRow("Big steps", "Add ten at a time", false,
                                         on => _big.Value = on),
                    },
                })) {
            TopBars = {
                new AdwHeaderBar {
                    TitleWidget = new Watch(() =>
                        new AdwWindowTitle("Counter", $"{_count.Value} so far")),
                },
            },
        };
}
```

### What ships

| Family | Types |
| --- | --- |
| **Navigation** | `AdwNavigationView`, `AdwNavigationPage`, `AdwNavigationSplitView`, `AdwOverlaySplitView`, `AdwPaned`, `AdwViewStack` with `AdwViewSwitcher` / `AdwViewSwitcherBar` / `AdwViewSwitcherSidebar` / `AdwInlineViewSwitcher`, `AdwTabView`/`AdwTabBar`/`AdwTabOverview`, `AdwCarousel`, `AdwSidebar` |
| **Chrome** | `AdwHeaderBar`, `AdwToolbarView`, `AdwWindowTitle`, `AdwWindowControls`, `AdwDragArea` |
| **Rows & lists** | `AdwPreferencesPage`/`Group`, `AdwActionRow`, `AdwEntryRow`, `AdwPasswordEntryRow`, `AdwComboRow`, `AdwSpinRow`, `AdwSwitchRow`, `AdwExpanderRow`, `AdwButtonRow` |
| **Controls** | `AdwButton`, `AdwButtonContent`, `AdwSplitButton`, `AdwToggleButton`, `AdwToggleGroup`, `AdwMenuButton`, `AdwLinkButton`, `AdwSwitch`, `AdwCheckButton`, `AdwRadioButton`, `AdwSlider`, `AdwSpinButton`, `AdwLevelBar`, `AdwProgressBar`, `AdwEntry`, `AdwSearchEntry`, `AdwPasswordEntry`, `AdwSuggestionEntry`, `AdwColorButton`, `AdwDropDown` |
| **Feedback** | `AdwToast`/`AdwToastOverlay`, `AdwBanner`, `AdwAlertDialog`, `AdwStatusPage`, `AdwSpinner`, `AdwAvatar` |
| **Dialogs & overlays** | `AdwDialog`, `AdwPreferencesDialog`, `AdwAboutDialog`, `AdwShortcutsDialog`, `AdwShortcutLabel`, `AdwBottomSheet`, `AdwMenuItem` (popovers are raised by `AdwMenuButton` / `AdwSplitButton`) |
| **Layout & style** | `AdwClamp`, `AdwClampScrollable`, `AdwWrapBox`, `AdwBreakpoint`/`Bin`, `AdwMultiLayoutView`, `AdwSeparator`, `AdwTheme`, `AdwPalette`, `AdwTypography`, `AdwMetrics`, `AdwStyle` |

The gallery is the spec: **37 pages**, each one live, with a headless self-test that constructs every
page and checks the catalogue.

```sh
dotnet run --project Zigote.UI.Adwaita.Gallery                 # search, preferences, extra windows
dotnet run --project Zigote.UI.Adwaita.Gallery -- --self-test  # headless; the exit code is the result
```

Shortcuts use the platform command modifier (⌘ on macOS, Ctrl elsewhere): **F** search, **N** new
window, **,** preferences, **D** toggle dark, **W** close window, **⇧/** about.

### Honest scope

- **A design system, not a binding.** Names mirror libadwaita so the GNOME HIG and its docs transfer,
  but this is not API-compatible with the C library, and not every libadwaita widget exists.
- **Bundled type and icons.** Text renders in Inter and icons come from the bundled Material Icons
  font — the system icon theme and Adwaita Sans / Cantarell are not read.
- **`button-layout` is honoured; window-manager gestures are not.** Double-click-to-maximize and
  drag-to-snap follow the framework's own chrome handling, not the compositor's full policy set.

---

## The reactive core

Everything above is driven by a handful of primitives: four in `Zigote.Core.State` — usable on their
own, with no UI in sight — plus the two that connect them to a tree and to app logic.

```csharp
var query    = new Signal<string>("");
var results  = Computed.From(() => Index.Search(query.Value));   // lazy, cached, auto-tracked
using var io = new Effect(() => Save(results.Value),             // re-runs when a source changes…
                          EffectAffinity.Deferred);              // …on the frame loop, not the writer

Reactive.Batch(() => { name.Value = n; age.Value = a; });        // one pass, one layout, one redraw

new Watch(() => new Label($"{results.Value.Count} hits"))        // the bridge into the widget tree
```

| Primitive | Answers | Lives in |
| --- | --- | --- |
| `Signal<T>` | What is true now? | `Zigote.Core` |
| `Computed<T>` | What can be derived from it? (glitch-free; leak-free while unobserved) | `Zigote.Core` |
| `Effect` | What imperative work reacts to it? (`Inline`, or `Deferred` to the frame loop) | `Zigote.Core` |
| `Trigger` / `LinkedSignal<T>` | Valueless events; derived-but-locally-overridable values | `Zigote.Core` |
| `Watch` | How does a signal reach the widget tree? | `Zigote.UI` |
| `Bloc<TEvent, TState>` | How does the app behave? Events in, ordered; state out as signals | `Zigote.Bloc` |

**Signals are not thread-affine.** The graph takes one re-entrant lock, so a network, audio or timer
thread may write freely; what you declare is where the *reaction body* runs. `Zigote.Core.Threading.Background`
adds the frame-aware half — results delivered against a per-frame budget, `Slice` for work spread
over frames, `Latest` for debounced latest-wins — with failures reported instead of swallowed.

Rebuild counts are measurable, not claimed: `ui.watch_rebuilds`, `reactive.writes` and
`reactive.runs` are live counters in DevTools, and `Reactive.TrackReactions` names the hottest body.

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Apps        Editor · Player · your app · galleries                      │
├──────────────────────────────────────────────────────────────────────────┤
│  Design      Zigote.UI.Adwaita (GNOME)  ·  Zigote.UI.Material            │
│  systems     Charts · DevTools · Localizations · CodeEditor              │
├──────────────────────────────────────────────────────────────────────────┤
│  Kernel      Zigote.UI — widgets, layout, paint, input, focus,           │
│              navigation, animation, semantics       (headless-testable)  │
├──────────────────────────────────────────────────────────────────────────┤
│  Core        Zigote.Core — Signal/Computed/Effect, Background,           │
│              math, assets, diagnostics, the paint & event ABI            │
├──────────────────────────────────────────────────────────────────────────┤
│  Gameplay    ECS (flecs) · Physics · World · Scripting · Vfx · Graphs    │
├──────────────────────────────────────────────────────────────────────────┤
│  Native      libzigote (Zig)  —  wgpu · SDL3 · HarfBuzz+FreeType ·       │
│              Jolt · flecs · miniaudio · Assimp        [C ABI, submodule] │
└──────────────────────────────────────────────────────────────────────────┘
```

Design systems are **surfaces over one kernel, not forks** — mixing `Zigote.UI`, `.Adwaita` and
`.Material` in one app is normal and supported. `Zigote.UI` depends on nothing above the GPU, so the
whole widget layer is testable headlessly; every test in `Zigote.Tests` runs without a window.

Full detail, including the threading model and the diagnostics surface:
**[`docs/architecture.md`](docs/architecture.md)**.

---

## Modules

<details open>
<summary><b>UI framework</b></summary>

| Project | Purpose |
| --- | --- |
| `Zigote.UI` | The kernel: layout, painting, input, focus, navigation (Navigator 2.0), animation, semantics, `Watch`. |
| `Zigote.UI.Adwaita` | The GNOME Adwaita design system — 83 `Adw*` types, live system theming, client-side decorations. |
| `Zigote.UI.Material` | Material Design widget set, with the Flutter names (`Scaffold`, `AppBar`, `ListTile`, `TextField`). |
| `Zigote.UI.Charts` | Declarative charting, Swift-Charts shaped. |
| `Zigote.UI.BottomSheet` | Draggable, resizable sheets; design-language agnostic. |
| `Zigote.UI.DevTools` | Widget inspector, performance timeline, reactive counters, console. |
| `Zigote.UI.Localizations` | Locales, plural rules, typed message codegen from ARB. |
| `Zigote.UI.FSharp` | F# ergonomics for the reactive core (`signal`/`computed`/`effect`/`watch`) and window bootstrap. |
| `Zigote.Modules.UI.CodeEditor` | Code editor with FParsec-based syntax highlighting. |

</details>

<details>
<summary><b>Core & runtime</b></summary>

| Project | Purpose |
| --- | --- |
| `Zigote.Core` | Reactive primitives, background work, native FFI (`[LibraryImport]` into `libzigote`), scene graph, assets, diagnostics. |
| `Zigote.Runtime` | Shared runtime host that drives the frame loop for shipped games. |
| `Zigote.Player` | Standalone player that loads and runs an exported content bundle. |
| `Zigote.Generators` | Roslyn source generators (FFI bindings, DSL codegen). |
| `Zigote.Engine` | The native Zig + wgpu backend (git submodule). |

</details>

<details>
<summary><b>App services</b></summary>

| Project | Purpose |
| --- | --- |
| `Zigote.Bloc` | The BLoC pattern: events in, ordered, one at a time; state out as signals. |
| `Zigote.Logging` | Serilog wiring — console, rolling file, DevTools ring, and the failures that are otherwise silent. |
| `Zigote.Preferences` | Declarative, reactive, persisted settings. |
| `Zigote.Persistence` (+`.SQLite`) | Key-value stores behind one interface. |
| `Zigote.Reactive.R3` | Bridge between `Signal<T>` and R3 `Observable<T>`. |
| `Zigote.Audioplayer` | Media playback over `IAudioApi` — queue, transport, gapless advance, equalizer. Testable against an in-memory fake, no device needed. |
| `Zigote.Videoplayer` | Video playback and controls, decoded by driving the `ffmpeg` / `ffprobe` executables ([README](Zigote.Videoplayer/README.md)). |

</details>

<details>
<summary><b>Rendering, gameplay & graphs</b></summary>

| Project | Purpose |
| --- | --- |
| `Zigote.Render2D` | 2D sprite rendering. |
| `Zigote.Cinematics` | Physically-based camera simulation (film-look controls). |
| `Zigote.Vfx` | Particle / VFX system. |
| `Zigote.ECS` | flecs-backed entity-component-system. |
| `Zigote.Physics2D` | 2D physics (3D physics is Jolt, in the native engine). |
| `Zigote.World` / `Zigote.Save` | Spawning, tags, spatial queries; save & load. |
| `Zigote.Scripting` | Component lifecycle and C# hot reload (Edit & Continue). |
| `Zigote.Game` / `Zigote.Network` | Gameplay layer; transport, authority and replication. |
| `Zigote.Graphs.*` | Node-graph system (`Core`, `Commands`, `Registry`, `Editor`, `Shading`, `Vfx`) behind the shader-graph material builder and VFX graphs — codegen to WGSL. |
| `Zigote.Editor` | The visual editor: hierarchy, inspector, asset browser, docked code editor, graphs. |

</details>

The 3D side is a forward+ EEVEE-style pipeline — cascaded shadows, SSAO/GTAO, SSR, bloom, TAA, AgX
tonemapping, glass refraction — over a backend-agnostic render graph, with GPU instancing, frustum
culling, LOD and asset streaming. Games export to a standalone runtime + player bundle (JIT or AOT).

---

## Platform support

| Platform | Status |
| --- | --- |
| **macOS** (arm64, x64) | Primary development platform — built, run and tested daily. x64 cross-builds from arm64. |
| **Linux** (x64, arm64) | Builds natively in CI (`linux-x64`) and cross-compiles from any host. arm64 is wired as a target but sees less exercise. |
| **Windows** (x64) | Builds natively in CI (`win-x64`, MSVC ABI). Cross-compiling from macOS/Linux uses Zig's bundled MinGW (GNU ABI). wgpu ships as `wgpu_native.dll` beside `zigote.dll`. |
| **iOS / Android** | In bring-up. Touch, lifecycle, safe area and both native builds work; the gallery runs on the iOS simulator and the Android emulator. See [`docs/mobile-port.md`](docs/mobile-port.md). |

One SDL3 + wgpu code path everywhere; wgpu picks Metal, Vulkan or D3D12 per OS. Per-OS self-contained
bundles come from [`.github/workflows/release.yml`](.github/workflows/release.yml) or locally via
`build/publish.sh <rid>`. Windows and Linux are CI-verified but get less real-hardware time than
macOS — platform bug reports are very welcome.

**Not here yet, stated up front:** no accessibility bridge (the semantics tree and `ISemanticsBridge`
seam exist; no AT-SPI / UIA / VoiceOver implementation does, so screen readers will not see your
app), no web target, and no third-party widget ecosystem.

---

## Migrating from another toolkit

[`docs/migration/`](docs/migration/README.md) is written for people arriving from a declarative
toolkit they already know. Every sample in it compiles against types that ship today.

| Coming from | Guide | The translation that does most of the work |
| --- | --- | --- |
| Flutter / Dart | [`from-flutter.md`](docs/migration/from-flutter.md) | The vocabulary is the same — `Widget`, `BuildContext`, `Column`, `Navigator`. The execution model is not: `Build` runs once, and there is no stateless/stateful split. |
| Jetpack Compose | [`from-compose.md`](docs/migration/from-compose.md) | `remember { … }` becomes a plain field — there is no recomposition to survive, no slot table, no delta. |
| SwiftUI | [`from-swiftui.md`](docs/migration/from-swiftui.md) | A `View` struct is a description; a `Widget` *is* the thing — it owns its hover, focus, scroll and in-flight animation, so you hold onto it. |
| WPF / Avalonia / WinUI | [`from-wpf-avalonia.md`](docs/migration/from-wpf-avalonia.md) | Retained mode is what you already do. XAML becomes C#, `Binding` becomes `Signal<T>` + `Watch`, the view model becomes a `Bloc`. |

Read [`concepts.md`](docs/migration/concepts.md) first whichever you came from: it covers retained
mode and the four rules that follow from it, which is where nearly every newcomer bug traces back to.
Then [`cookbook.md`](docs/migration/cookbook.md) has worked solutions for async loading with retry,
virtualized lists, debounced search, forms, master/detail, dialogs with results, theming and headless
tests.

---

## Documentation

| Document | What is in it |
| --- | --- |
| [`docs/architecture.md`](docs/architecture.md) | Structure, core principles, threading model, diagnostics — marked against what actually ships. |
| [`docs/migration/`](docs/migration/README.md) | Per-framework guides, concepts, cookbook. |
| [`Zigote.UI.Adwaita/README.md`](Zigote.UI.Adwaita/README.md) | The Adwaita kit in depth: theming, chrome, rows, adaptive layout, libadwaita differences. |
| [`Zigote.UI.Adwaita.Gallery/README.md`](Zigote.UI.Adwaita.Gallery/README.md) | The gallery's shell, shortcuts, page catalogue and self-test. |
| [`Zigote.UI.HelloWorld/README.md`](Zigote.UI.HelloWorld/README.md) | The smallest complete app, annotated line by line. |
| [`Zigote.UI.DevTools/README.md`](Zigote.UI.DevTools/README.md) | The <kbd>Shift</kbd>+<kbd>D</kbd> overlay: panels, counters worth watching, console commands. |
| [`docs/preferences-and-persistence.md`](docs/preferences-and-persistence.md) | Settings and storage. |
| [`docs/mobile-port.md`](docs/mobile-port.md) · [`docs/mobile-port-android.md`](docs/mobile-port-android.md) | iOS / Android bring-up status. |
| [`docs/devtools-widget-tree.md`](docs/devtools-widget-tree.md) | The widget inspector's tree model. |

The XML doc comments on the types are the reference manual, and most of them explain *why*, not just
*what* — `Zigote.UI/Widgets/Widget.cs`, `Zigote.Core/State/Signal.cs` and `Zigote.UI/App/App.cs` are
the three worth reading first.

---

## Examples

- **[`Zigote.UI.HelloWorld`](Zigote.UI.HelloWorld/README.md)** — hello world plus a counter in one
  documented file. The place to start.
- **[`Zigote.UI.Adwaita.Gallery`](Zigote.UI.Adwaita.Gallery/README.md)** — 37 pages of the GNOME
  design system, an adaptive shell, multi-window, and a headless `--self-test`.
- **`Zigote.UI.Gallery`** — the core toolkit end to end: widgets, navigation, theming, charts,
  localization and DevTools.
- **`Zigote.UI.FSharp.Gallery`** — the same kernel from F#, through `signal`/`computed`/`watch`.
- **`examples/PorscheDemo`** — a full 3D driving demo (vehicle physics, materials, tracks). It
  carries large binary assets, so it is **not committed to this repository** (see `.gitignore`) and
  is distributed separately.

---

## Tests & tooling

```sh
dotnet test Zigote.Tests                                        # headless; no window required
dotnet run  --project Zigote.UI.Adwaita.Gallery -- --self-test  # every gallery page constructs
dotnet run  --project Zigote.SmokeTest                          # boots the real renderer (needs a GPU)
```

`Zigote.Tests` is 159 files covering the kernel, the Adwaita kit, blocs, preferences and the reactive
graph — all of it without a window. `Zigote.SmokeTest` is the other half: it opens a real SDL3 window
and presents frames through wgpu, so it is deliberately **not** part of `dotnet test`; with
`ZIGOTE_SMOKE_SCENE=1` it drives the full 3D path and can dump a golden image.

`Zigote.Ecs.Benchmark`, `Zigote.Bloc.Benchmark` and `Zigote.Reactive.Benchmark` are BenchmarkDotNet
projects; `tools/` holds the icon-font generator and `build/` the MSBuild targets for font subsetting
and AOT publishing.

---

## License

Zigote is released under the [MIT License](LICENSE). Bundled third-party components — fonts, native
libraries — retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
