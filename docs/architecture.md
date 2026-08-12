# Architecture

How the pieces fit together and why they are arranged this way. Everything named here **ships
today**; the few places where something was deliberately *not* built say so and give the reason.

New to the framework? Read [`migration/concepts.md`](migration/concepts.md) first — it covers
retained mode, which is the decision the rest of this document follows from.

**Contents:** [The stack](#the-stack) · [Principles](#principles) ·
[The reactive core](#the-reactive-core) · [Threading](#threading) ·
[Background work](#background-work) · [Data flow](#data-flow) · [Packages](#packages) ·
[The engine seam](#the-engine-seam) · [Diagnostics](#diagnostics) · [Non-goals](#non-goals)

---

## The stack

Zigote is a **retained-mode UI framework for .NET**, drawn by a Zig + wgpu backend it ships with.
Each layer depends only on the one below it:

```
┌────────────────────────────────────────────────────────────────────────┐
│  Your app                                                              │
├────────────────────────────────────────────────────────────────────────┤
│  Design systems   Zigote.UI.Adwaita (GNOME) · Zigote.UI.Material       │
│  and add-ons      Charts · Localizations · DevTools · BottomSheet      │
├────────────────────────────────────────────────────────────────────────┤
│  Kernel           Zigote.UI — widgets, layout, paint, input, focus,    │
│                   navigation, animation, semantics  (headless-testable)│
├────────────────────────────────────────────────────────────────────────┤
│  Core             Zigote.Core — Signal/Computed/Effect, Background,    │
│                   math, assets, diagnostics, the paint & event ABI     │
├────────────────────────────────────────────────────────────────────────┤
│  Native           libzigote (Zig) — wgpu · SDL3 · HarfBuzz+FreeType ·  │
│                   Jolt · flecs · miniaudio · Assimp   [C ABI, submodule]│
└────────────────────────────────────────────────────────────────────────┘
```

Two things follow from that shape:

- **Design systems are surfaces over one kernel, not forks.** Adwaita and Material compose the same
  primitives and share the theme, focus, semantics and hot-reload machinery, so mixing them in one
  app is normal and supported.
- **`Zigote.UI` depends on nothing above the GPU.** The whole widget layer is headlessly testable —
  build a tree, lay it out, dispatch synthetic input, assert on the emitted paint commands. Every
  test in `Zigote.Tests` runs without a window.

**Retained mode is the load-bearing choice.** Widgets are long-lived objects: you build the tree once
and mutate properties. There is no per-frame diff, because hover, press, focus and scroll live on the
widget instances themselves.

Goals throughout: Native AOT friendly, low allocations on the steady path, deterministic behaviour,
clear ownership, cross-platform, modular by default.

### The separate stack

The 3D renderer, gameplay layer and visual editor also live in this repository, *beside* the UI
framework rather than under it. They are built with `Zigote.UI` — the editor is an ordinary Zigote
app — but nothing in `Zigote.UI` or `Zigote.Core` depends on them, and an app that only draws widgets
links none of it. See [`games-and-3d.md`](games-and-3d.md).

---

## Principles

**1. Application state is explicit.** It lives in `Signal<T>`, one source of truth. *Interaction*
state (hover, caret, scroll offset) lives on widgets — that is what retained mode buys.

**2. Logic is explicit.** Business logic belongs to a `Bloc`. Widgets call `bloc.Add(evt)` and read
`bloc.State.Value`.

**3. Dependencies are explicit.** Constructor injection at the composition root. No reflection, no
service locator, no container. (`record AppEnv(...)` is a convention you may adopt; the framework has
no type for it and needs none.)

**4. Concurrency is owned by the bloc.** A `Bloc` holds a `Lifetime` token, `Restart()` for
latest-wins work, and `Track()` for subscriptions; `Dispose` cancels the lot.

**5. Heavy work never blocks the frame.** Async handlers, and `Task.Run` at the few real call sites
(font enumeration, image decode, asset load). Signal writes from the resulting thread are legal — see
[Threading](#threading).

---

## The reactive core

Six primitives, in `Zigote.Core/State/` unless noted:

| Primitive | Question it answers | Type |
|---|---|---|
| `Signal<T>` | What is true now? | `Zigote.Core.State.Signal<T>` |
| `Trigger` | That it happened (valueless source) | `Zigote.Core.State.Trigger` |
| `Computed<T>` | What can be derived? | `Computed.From(...)` |
| `LinkedSignal<T>` | Derived, but locally overridable | `Linked.From(...)` |
| `Effect` | What imperative work reacts to state? | `new Effect(body, affinity)` |
| `Bloc<TEvent, TState>` | How does the app behave? | `Zigote.Bloc` |
| `Watch` | How does a signal reach the tree? | `Zigote.UI.Widgets.Watch` |

### Signal

```csharp
var selected = new Signal<SceneNode?>(null);
selected.Value = node;                 // equality-gated
selected.Set(node);                    // unconditional
selected.Update(n => n with { .. });   // read-modify-write
selected.Peek();                       // read WITHOUT subscribing
using var sub = selected.Subscribe(n => ...);   // fires now, then on change
```

Synchronous, deterministic, lightweight. Reads inside a `Computed`/`Effect` subscribe automatically.
An optional `IEqualityComparer<T>` decides what counts as a change; DEBUG asserts that a value type
implements `IEquatable<T>` so equality does not box twice per write.

### Computed

```csharp
var visible = Computed.From(() => songs.Value.Where(...).OrderBy(...).ToList());
```

Lazy, cached, auto-tracked, side-effect free. Leak-free: while unobserved it neither recomputes nor is
retained by its sources. Glitch-free while observed — a fan-out settles it once, and an intermediate
whose value is unchanged does not wake its observers. A throwing compute caches the exception and
rethrows on read until a dependency changes; a cycle throws.

### Effect

```csharp
using var e = new Effect(() => { Save(doc.Value); return () => ...cleanup... },
                         EffectAffinity.Deferred);
```

Runs immediately, re-runs when a source it read changes, returns a cleanup thunk (React-`useEffect`
style). Always watched, so it drives the subscription of the computeds it reads.

### Bloc

```csharp
public sealed class CounterBloc(ICounters counters) : Bloc<CounterEvent, CounterState>(new(0, false))
{
    protected override async ValueTask OnEventAsync(CounterEvent e, CancellationToken ct) => ...
}
```

Three bases: `Bloc<TEvent, TState>` (one immutable record — the default), `SyncBloc<TEvent, TState>`
(no handler awaits), `Bloc<TEvent>` (several independent signals).

Guarantees: events are ordered and never nested, synchronous when the handler is, allocation-free on
that synchronous path; one throwing event does not take the screen down (`BlocErrors.OnError`); one
unit of work in flight (`Restart()`); dispose is the off switch. `Emit` writes state under the graph
lock via `Reactive.Sync`. `BlocObserver.OnEvent`/`OnChange` put every event and every real transition
on one ordered timeline when an app assigns them, and cost a null check when it does not.

There is no `droppable` transformer: `Add` is `virtual` and `Current` is in scope, so "ignore the
second tap while busy" is a guard at the top of an override rather than a policy the base models.

### Watch — the bridge to the tree

```csharp
new Watch(() => new Label($"{bloc.State.Value.Count}"))
```

Wraps the builder in a `Computed<Widget>` and swaps the subtree when a signal it read changes. On the
UI thread it swaps in place; an off-thread change (or one arriving mid-walk) is flagged and applied
in the next `Measure`. F# apps use the same widget through `watch`.

---

## Threading

**Signals are not thread-affine.** Every graph mutation runs under one re-entrant global lock
(`Reactive.Gate`), so a signal may be written from any thread — a timer, an async completion, an
audio or network thread. The lock is uncontended in single-threaded UI use.

What *is* constrained is where reaction bodies run:

| | Runs on |
|---|---|
| `EffectAffinity.Inline` (default) | Whichever thread wrote the signal, at drain time, holding the graph lock. For bodies that only touch reactive state. |
| `EffectAffinity.Deferred` | The host thread, at `Reactive.DrainDeferred()` (once per frame). **Required** for any body that touches the UI, blocks, or takes another lock. |

The sanctioned cross-thread pattern: the background thread writes signals and nothing else; every
effect that turns those writes into real work is `Deferred`; the frame loop drains once at frame
start. `Watch` marshals itself, so a bloc emitting from a background continuation is already safe.

Debug aids: `Reactive.LockTimeoutMs` turns a hang into a `ReactiveDeadlockException` naming both
threads; a slow `Inline` body reached cross-thread logs a warning.

### Transactions

```csharp
Reactive.Batch(() =>
{
    name.Value    = ...;
    age.Value     = ...;
    address.Value = ...;
});                          // one effect pass, one layout, one redraw
```

`Reactive.Batch(Action)` / `Batch<T>(Func<T>)`, nestable, composing with the implicit per-write batch.
Also `Reactive.Untracked(fn)` to read without subscribing, and `Reactive.Sync(fn)` for a composed
multi-step write.

There is no `using`-scoped batch disposable: a disposable scope would have to hold the lock across
arbitrary user code, which is the shape that deadlocks.

---

## Background work

`Zigote.Core.Threading.Background` centralises the policy for work nobody is awaiting — which is
where failures were silently swallowed. It is **not** a scheduler and not a concurrency model: work
goes to the thread pool, in order, with no priorities. `async`/`await` in a bloc handler remains
correct for work the handler awaits.

```csharp
var background = new Background(app.Post, app.RequestLayout);   // root, at the composition root
var library    = background.Child("library");                   // one per feature or screen

library.Run(() => Storage.Save(file, snapshot));                // fire and forget; failures reported
library.Run(() => Load(path), data => bloc.Add(new Loaded(data)));           // → UI thread
library.Post(() => bloc.Add(new Progress(n)), Deliver.WhenIdle);             // → when the frame has room
library.RunAsync(ct => client.FetchAsync(ct));                  // ct dies with the scope
library.Slice(list, 50_000, i => list.AddItem(Row(i)));         // N units per frame until done

var search = library.Latest();                                  // one slot per unit of work
search.Run(ct => Filter(query, ct), Emit, delay: 120.ms);       // debounced, latest-wins
```

**The floor** — what a bare `_ = Task.Run(...)` gets wrong:

| Without | With |
|---|---|
| The task's exception is never observed — a failed scan looks like an empty one | `Background.OnError`, defaulting to `DebugLog`, tagged `app/library.StartScan` |
| Nothing is cancelled at shutdown, so a result lands on a disposed bloc | `Dispose()` cancels the scope and its children; in-flight syscalls finish, callbacks are dropped |
| Every call site hand-rolls `Task.Run` → `App.Post` → check a token | `Run(work, onUi)` and `Latest` are those lines, written once |

**Above the floor** — the parts that are not a re-implementation of `CoroutineScope`:

- **Frame-aware delivery.** `Deliver.WhenIdle` results and `Slice` work run against a per-frame time
  budget: the host calls `RunFrame(budget)` once per frame before layout, each queue makes at least
  one unit of progress, and whatever does not fit asks for another frame and continues there, in
  order. Four hundred results landing at once become several frames of filling in rather than one
  dropped frame. A general-purpose runtime cannot offer this, because it does not know what a frame
  is — this is what a UI framework has that a language runtime does not.
- **Supervised by default.** A child scope dies with its parent, and a child's failure is reported
  without cancelling its parent or its siblings. (Kotlin's default is the opposite — a failing child
  cancels the scope unless you ask for `SupervisorJob` — which for a UI means one bad thumbnail stops
  the library scan.)
- **Deterministic under test.** `Background.Manual()` plus `Drain(timeout)` give a test the frame
  loop's side of this with no window, so "the result landed" is an assertion rather than a sleep.
- **Payload-checked in DEBUG.** Handing a `List<T>` across the boundary and continuing to hold it is
  a race. Copying everything is the wrong trade — the point of immutable state records is handing 50k
  of them over by reference — so the known-mutable shapes are named in `DebugLog` at delivery, and
  compiled out of release.

`Latest` replaces the cancel-the-old-CTS-make-a-new-one dance: a search box, a regroup, a debounced
save. `Bloc.Restart()` still covers the bloc-wide "one unit of work in flight"; hold a `Latest` per
unit when a bloc has more than one.

Deliberately absent: no `Deferred<T>`/`awaitAll` composition, no priorities, no cancel-my-parent
cascade. Each is a day's work the day something needs it.

---

## Data flow

```
User input → Widget → bloc.Add(event) → Repository → result → Emit → Signal → Watch rebuild
```

One direction. The only way state changes is an event through the pump.

**Widgets** render state and send commands. They never mutate application signals directly, contain
business logic, touch a database, or do networking. They *do* own their own interaction state — that
is retained mode, not a violation.

**State** is an immutable record, updated by replacement:

```csharp
record PlayerState(Song? CurrentSong, bool IsPlaying, double Position);
Emit(Current with { IsPlaying = true });
```

**The domain layer** — repositories, services, validation, business rules — has no dependency on
signals, widgets or UI. `Zigote.Persistence` is the model: BCL only, opaque `string → string`, no
reactive graph.

---

## Packages

**The UI framework**

| Package | Contents |
|---|---|
| `Zigote.Core` | `Signal`/`Computed`/`Effect`/`LinkedSignal`/`Trigger`/`Reactive`, `Threading.Background`, plus the native seam: paint & event ABI, math, animation, assets, diagnostics registries |
| `Zigote.UI` | Widgets, layout, theming, navigation (Navigator 2.0), focus, semantics, `Watch` |
| `Zigote.UI.Adwaita` | The GNOME Adwaita design system on the kernel — 94 `Adw*` types, live system theming, client-side decorations ([README](../Zigote.UI.Adwaita/README.md)) |
| `Zigote.UI.Material` | The Material vocabulary with the Flutter names ([README](../Zigote.UI.Material/README.md)) |
| `Zigote.UI.Charts`, `.Localizations`, `.BottomSheet` | Charting, i18n, draggable sheets |
| `Zigote.UI.FSharp` | F# reactive ergonomics (`signal`/`computed`/`effect`/`watch`) + `Host.run` |
| `Zigote.UI.DevTools` | Debug overlay: panels, charts, diagnostics |

**App services**

| Package | Contents |
|---|---|
| `Zigote.Bloc` | The event pump and its three bases. Depends on `Zigote.Core` only |
| `Zigote.Persistence` (+ `.SQLite`) | `IKeyValueStore` — "where do strings live" |
| `Zigote.Preferences` | `Preference<T> : IReadableSignal<T>` — persisted signals |
| `Zigote.Logging` | Serilog wiring (`AppLog`) |
| `Zigote.Audioplayer` | Media playback over `IAudioApi`: queue, transport, gapless, equalizer |
| `Zigote.Videoplayer` | Playback and controls, decoded by driving `ffmpeg`/`ffprobe` |
| `Zigote.Network` | Transport, replication, prediction, sync |
| `Zigote.Reactive.R3` | Optional R3 bridge |
| `Zigote.Cli` | `zigote create` / `zigote add android` — project scaffolding, no framework dependency |

**Games, 3D and hosts** — the separate stack, documented in [`games-and-3d.md`](games-and-3d.md):
`Zigote.Runtime`, `Zigote.Scripting`, `Zigote.ECS`, `Zigote.World`, `Zigote.Save`,
`Zigote.Physics2D`, `Zigote.Render2D`, `Zigote.Vfx`, `Zigote.Cinematics`, `Zigote.Graphs.*`,
`Zigote.Game`, `Zigote.Editor`, `Zigote.Player`.

Navigation lives inside `Zigote.UI` rather than in a package of its own; 2D paint is in `Zigote.Core`,
with rendering in `Zigote.Render2D` and the native engine. Applications reference only what they
need.

---

## The engine seam

`ZigoteEngine` is one class because there is one native handle behind all of it, but it covers four
unrelated jobs. Splitting the package would move every method and break every caller to gain a
project reference, so the domains are named on the class instead:

| Domain | Reached through | Shape |
|---|---|---|
| **UI** | `Zigote.UI` (`App`, widgets, `App.Post`) | Already its own package; the engine below it is windowing and paint |
| **Audio** | `engine.Audio` → `IAudioApi` | An **interface**: files, transport, equalizer chains, offline decode. Game spatial audio (listener, positioned one-shots, voices, buses) stays on `ZigoteEngine` — an app that never places a sound in a world should not stub it |
| **3D** | `engine.Scene` → `Scene3D` | A zero-allocation `readonly struct`. Nodes, transforms, materials, lights, cameras |
| **Background** | `Background` / `Latest` | Above, and the only one that is not a facade — it has policy of its own |

Audio is an interface because the device is the one part of a media app that **cannot exist in CI**:
behind that seam a queue, a transport and an equalizer are pure state machines a fake can drive, which
is how `Zigote.Audioplayer` is tested end to end without a sound card. Nothing tests a scene without a
GPU, so `Scene3D` is a struct — a vtable and a second implementation nobody writes would be the cost
of symmetry for its own sake.

### R3 integration

Optional, one bridge package. Streams where streams naturally exist: timers, sockets, file watchers,
progress.

```csharp
prefs.Units.AsStream().Select(...).Subscribe(...);            // signal → stream, current value first
query.AsChangeStream().Debounce(250.ms).SubscribeAwait(...);  // edges only
using var rows = repository.Rows.ToSignal(initial: []);       // stream → signal, handle owns the sub
```

Writes into the graph go through `Reactive.Sync`. The signal stays the single source of truth.

---

## Diagnostics

- **`Zigote.Core/Diagnostics`** — `DebugLog`, `DebugCommands`, `DebugVariables`, `DebugProfiler`,
  `Profiler`. Engine-neutral registries.
- **`Zigote.UI.DevTools`** — one line installs an overlay over an `App` (<kbd>Shift</kbd>+<kbd>D</kbd>):
  `UiInspectorPanel` (select-on-screen, live tree, box model, constraints, property dump) and
  `PerformancePanel` (rolling frame chart + hottest scopes).
- **Rebuild counters** — the Reactive panel, also readable as variables and from the console
  (`get reactive.runs`):

  | Variable | Counts |
  |---|---|
  | `reactive.writes` | Signal writes + trigger fires that passed their equality check |
  | `reactive.runs` | Computed recomputes + effect runs |
  | `reactive.deferred` | Deferred effects parked at the last frame's drain |
  | `ui.watch_rebuilds` | `Watch` subtree swaps, excluding first build |

  The panel charts the first two as **rates** over 60 s. The number to read is the idle one: churn
  while nothing on screen changes is a value-type signal missing `IEquatable<T>`, a computed
  rebuilding a collection every run, or an effect writing a signal it reads. One increment per
  reaction body, under a lock already held — kept in release builds because the number is worth more
  than the increment costs.

- **Attribution** — `Reactive.TrackReactions` (the panel's toggle) aggregates runs by the body's
  declaring `Type.Method`, unwrapping compiler closure classes, so `Reactive.HottestReactions()` names
  *which* computed or effect is churning. Opt-in: it costs a dictionary lookup per body run. Toggling
  it on resets the counts, so the table answers "what churned while I did that".
- **Per-`Watch` counts** — a swap bumps `Widget.RebuildCount`, so the inspector shows `12 rebuilds`
  inline on the row. The global counter says *something* is churning; the tree row says which subtree.
- **Texture residency** — `gpu.textures`, `gpu.texture_bytes`, `gpu.texture_cpu_bytes`, read from the
  engine's image cache. Texture handles are **caller-owned**: `LoadTexture*` hands one out and nothing
  but `ReleaseTexture` frees it, so a widget that loads one and forgets is a leak for the process's
  lifetime — at 2000×3000 that is 24 MB a click. Watch the count while browsing an asset folder: it
  should come back down. A number that only climbs names a missing release.
- **Failure seams** — `Reactive.OnError`, `BlocErrors.OnError`, `Background.OnError`; unset, failures
  land in `DebugLog`. `Zigote.Logging`'s `AppLog.Bootstrap()` + `CaptureFailures()` routes them into
  Serilog.
- **The bloc timeline** — `BlocObserver.OnEvent`/`OnChange`: every event as it comes off the pump and
  every transition it caused, in order, deduplicated emits omitted. Unset by default (one null check
  per event), so an app can turn it on in a release build by assigning a hook. This is the input a
  replay or time-travel panel would need; neither is built.
- `Reactive.LockTimeoutMs`, the DEBUG boxing-equality assert, the DEBUG slow-cross-thread-effect
  warning.

Deliberately not built: a node-and-edge **signal-graph visualiser**. Drawing the graph needs every
live node discoverable (a registry of weak references, maintained on the hot path) to answer a
question the hottest-bodies table already answers from a dictionary. Worth building only if a real
debugging session gets stuck on graph *shape* rather than graph *volume*.

---

## Non-goals

Reflection-based DI. Stream-based UI state. Global mutable state. Widget-centric business logic.
Mandatory MVVM.

One qualification on **"no hidden thread switching"**: it means *declared* thread switching, not none.
`Watch` marshals off-thread rebuilds to the UI thread and `EffectAffinity.Deferred` parks bodies for
the frame loop — both visible at the call site, and both the reason a background write is safe at all.

---

## In one paragraph

Six primitives ship — `Signal`, `Computed`, `Effect`, `Bloc`, `Watch`, and the reactive runtime that
batches them — plus `Trigger` and `LinkedSignal` for the two shapes a plain signal handles badly.
There is no DI container and no scope type: constructor injection and the bloc's own lifetime cover
both without a framework type. The threading rule is not "signals belong to the UI thread" but "the
graph takes a lock; effect bodies declare where they run". And the tree is retained, which is why
none of this needs a diff.
