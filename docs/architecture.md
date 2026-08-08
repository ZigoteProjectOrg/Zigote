# Zigote Architecture

**v1.1 — refactored against the code as it exists.** Sections marked *(not built)* are proposals with
no implementation; everything else names a type that ships today.

---

## Vision

Zigote is a retained-mode UI framework for .NET focused on native applications, built on a Zig/wgpu
backend (`Zigote.Engine`) through `Zigote.Core`.

Goals: Native AOT friendly, high performance, deterministic behaviour, low allocations, clear
ownership, cross-platform, modular by default.

**Retained mode is the load-bearing choice.** Widgets are long-lived objects; you build the tree once
and mutate properties. There is no per-frame diff — hover, press, focus and scroll live on the widget
instances themselves. This shapes every rule below.

---

## Core Principles

**1. Application state is explicit.** It lives in `Signal<T>`, one source of truth. *Interaction*
state (hover, caret, scroll offset) lives on widgets — that is what retained mode buys.

**2. Logic is explicit.** Business logic belongs to a `Bloc`. Widgets call `bloc.Add(evt)` and read
`bloc.State.Value`.

**3. Dependencies are explicit.** Constructor injection at the composition root. No reflection, no
service locator, no container. (`record AppEnv(...)` is a convention you may adopt — there is no `Env`
type and nothing needs one.)

**4. Concurrency is owned by the bloc.** A `Bloc` holds a `Lifetime` token, `Restart()` for
latest-wins work, and `Track()` for subscriptions; `Dispose` cancels the lot. There is no `Scope` type.

**5. Heavy work never blocks the frame.** Async handlers, `Task.Run` at the few real call sites
(font enumeration, image decode, asset load). Signal writes from the resulting thread are legal.

---

## Core Primitives

What actually ships in `Zigote.Core/State/`:

| Primitive | Question it answers | Type |
|---|---|---|
| `Signal<T>` | What is true now? | `Zigote.Core.State.Signal<T>` |
| `Trigger` | That it happened (valueless source) | `Zigote.Core.State.Trigger` |
| `Computed<T>` | What can be derived? | `Computed.From(...)` |
| `LinkedSignal<T>` | Derived, but locally overridable | `Linked.From(...)` |
| `Effect` | What imperative work reacts to state? | `new Effect(body, affinity)` |
| `Bloc<TEvent, TState>` | How does the app behave? | `Zigote.Bloc` |
| `Watch` | How does a signal reach the tree? | `Zigote.UI.Widgets.Watch` |

`Env` and `Scope` from the original proposal do not exist and are not planned — principles 3 and 4
cover their jobs with plain C#.

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
(no handler awaits), `Bloc<TEvent>` (several independent signals). Guarantees: ordered and never
nested, synchronous when the handler is, allocation-free on that synchronous path, one throwing event
does not take the screen down (`BlocErrors.OnError`), one unit of work in flight (`Restart()`),
dispose is the off switch. `Emit` writes state under the graph lock via `Reactive.Sync`.

### Watch — the bridge to the tree

```csharp
new Watch(() => new Text($"{bloc.State.Value.Count}"))
```

Wraps the builder in a `Computed<Widget>` and swaps the subtree when a signal it read changes. On the
UI thread it swaps in place; an off-thread change (or one arriving mid-walk) is flagged and applied in
the next `Measure`. Replaces the old `BlocBuilder`/`Cubit` pattern. F# apps use the same widget through `watch`.

---

## Threading Model

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
Also: `Reactive.Untracked(fn)` to read without subscribing, `Reactive.Sync(fn)` for a composed
multi-step write.

*(not built)* — the `using Signal.Batch()` disposable form from the proposal. `Reactive.Batch` covers
it; a disposable scope would need to own the lock across arbitrary user code, which is exactly the
shape that deadlocks.

### Background work — `Zigote.Core.Threading.Background`

Built, once an app had enough call sites to make the policy worth centralising (Timbre had nineteen).
It is **not** a scheduler and not a concurrency model: work goes to the thread pool, in order, with no
priorities. `async`/`await` in a bloc handler remains correct for work the handler awaits. This is for
work nobody is waiting on — which is where the failures were silent.

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
  dropped frame. `Dispatchers.Main` posts to a looper and runs the lot; a Dart isolate cannot touch
  the UI at all. Neither *can* offer this — a general-purpose runtime does not know what a frame is.
  This is what a UI framework has that a language runtime does not, and it deletes the per-app
  hand-rolled chunkers that grow in its absence.
- **Supervised by default.** A child scope dies with its parent, and a child's failure is reported
  without cancelling its parent or its siblings. Kotlin's default is the opposite — a failing child
  cancels the scope unless you ask for `SupervisorJob` — which for a UI means one bad thumbnail stops
  the library scan. Cascade is the special case here, not the default.
- **Deterministic under test.** `Background.Manual()` plus `Drain(timeout)` give a test the frame
  loop's side of this with no window, so "the result landed" is an assertion rather than a sleep.
- **Payload-checked in DEBUG.** Handing a `List<T>` across the boundary and continuing to hold it is
  the race isolates make unrepresentable by copying. Copying is the wrong trade here — the point of
  immutable state records is handing 50k of them over by reference — so instead the known-mutable
  shapes are named in `DebugLog` at the delivery, and compiled out of release.

`Latest` replaces the cancel-the-old-CTS-make-a-new-one dance: a search box, a regroup, a debounced
save. `Bloc.Restart()` still covers the bloc-wide "one unit of work in flight"; hold a `Latest` per
unit when a bloc has more than one (a library that scans *and* filters needs two).

The editor owns one on `EditorState` (`EditorState.Background`), drained once per frame from
`Zigote.Editor/Program.cs`. The project panel's tree walk hangs off a `Child("assets")` scope — it
used to run inside `Measure`, which meant a recursive enumeration of the whole project during layout
on every keystroke in the search box and on every filesystem event.

What is deliberately **not** here: no `Deferred<T>`/`awaitAll` composition (nothing has needed to join
two background results yet), no priorities, and no cancel-my-parent cascade. Each is a day's work the
day something needs it.

---

## Data Flow

```
User Input → Widget → bloc.Add(event) → Repository → result → Emit → Signal → Watch rebuild
```

One direction. The only way state changes is an event through the pump.

### Widget rules

Widgets render state and send commands. Widgets never mutate application signals directly, contain
business logic, touch a database, or do networking. They *do* own their own interaction state — that
is retained mode, not a violation.

### State rules

```csharp
record PlayerState(Song? CurrentSong, bool IsPlaying, double Position);
Emit(Current with { IsPlaying = true });
```

Immutable record, updated by replacement.

### Domain layer

Repositories, services, validation, business rules. No dependency on signals, widgets, or UI —
`Zigote.Persistence` is the model: BCL only, opaque `string → string`, no reactive graph.

---

## Package Layout

The real one:

| Package | Contents |
|---|---|
| `Zigote.Core` | `Signal`/`Computed`/`Effect`/`LinkedSignal`/`Trigger`/`Reactive`, `Threading.Background`, plus the native seam: paint & event ABI, math, animation, assets, diagnostics registries |
| `Zigote.UI` | Widgets, layout, theming, navigation (`Widgets/Navigation` — Navigator 2.0), focus, semantics, `Watch` |
| `Zigote.UI.FSharp` | F# reactive ergonomics (`signal`/`computed`/`effect`/`watch`) + `Host.run` |
| `Zigote.UI.Material`, `.Adwaita`, `.Charts`, `.Localizations` | Design languages and add-ons |
| `Zigote.UI.DevTools` | Debug overlay: panels, charts, diagnostics |
| `Zigote.Bloc` | The event pump and its three bases. `Zigote.Core` only, no other dependencies |
| `Zigote.Persistence` (+ `.SQLite`) | `IKeyValueStore` — "where do strings live" |
| `Zigote.Preferences` | `Preference<T> : IReadableSignal<T>` — persisted signals |
| `Zigote.Network` | Transport, replication, prediction, sync |
| `Zigote.Reactive.R3` | Optional R3 bridge |
| `Zigote.Logging` | Serilog wiring (`AppLog`) |
| `Zigote.Render2D`, `Zigote.Physics2D`, `Zigote.ECS`, `Zigote.World`, `Zigote.Vfx`, `Zigote.Cinematics`, `Zigote.Scripting`, `Zigote.Graphs.*` | The game-side stack |
| `Zigote.Editor`, `Zigote.Player`, `Zigote.Runtime`, `Zigote.Game` | Hosts |

Renamed from the proposal: `Zigote.R3` → `Zigote.Reactive.R3`, `Zigote.Storage` →
`Zigote.Persistence` + `Zigote.Preferences`. Never built: `Zigote.Navigation` (it lives inside
`Zigote.UI`), `Zigote.Graphics` (2D paint is in `Zigote.Core`, rendering in `Zigote.Render2D` and the
native engine), `Zigote.Audio` (see below), `Zigote.Markdown` (nothing in tree).

### Engine domains

`ZigoteEngine` is one class because there is one native handle behind all of it, but it covers four
unrelated jobs. Rather than split the package — every method would move and every caller would break,
to gain a project reference — the domains are named on the class:

| Domain | Reached through | Shape |
|---|---|---|
| **UI** | `Zigote.UI` (`App`, widgets, `App.Post`) | Already its own package; the engine below it is windowing and paint |
| **Audio** | `engine.Audio` → `IAudioApi` | An **interface**: files, transport, equalizer chains, offline decode. Game spatial audio (listener, positioned one-shots, voices, buses) stays on `ZigoteEngine` — an app that never places a sound in a world should not stub it |
| **3D** | `engine.Scene` → `Scene3D` | A zero-allocation `readonly struct`. Nodes, transforms, materials, lights, cameras |
| **Background** | `Background` / `Latest` | Above, and the only one that is not a facade — it has policy of its own |

Audio is the interface because the device is the one part of a media app that **cannot exist in CI**:
behind that seam a queue, a transport and an equalizer are pure state machines a fake can drive.
Nothing tests a scene without a GPU, so `Scene3D` is a struct — a vtable and a second implementation
nobody writes would be the cost of symmetry for its own sake.

Applications reference only what they need — `Zigote.UI` depends on nothing above the GPU and is
headless-testable.

---

## R3 Integration

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

Shipping today:

- `Zigote.Core/Diagnostics` — `DebugLog`, `DebugCommands`, `DebugVariables`, `DebugProfiler`,
  `Profiler`. Engine-neutral registries.
- `Zigote.UI.DevTools` — one-line install over an `App`: overlay with panels and charts. The
  proposal's **layout inspector** is `UiInspectorPanel` (select-on-screen, live tree, box model,
  constraints, property dump) and its **performance timeline** is `PerformancePanel` (rolling frame
  chart + hottest scopes).
- **Rebuild counters** — the **Reactive** panel (General tab), plus the same numbers as read-only
  variables under `reactive` in the Variables panel and the console (`get reactive.runs`):

  | Variable | Counts |
  |---|---|
  | `reactive.writes` | Signal writes + trigger fires that passed their equality check |
  | `reactive.runs` | Computed recomputes + effect runs (`Reactive.Runs`) |
  | `reactive.deferred` | Deferred effects parked at the last frame's drain |
  | `ui.watch_rebuilds` | `Watch` subtree swaps, excluding first build |

  The panel charts the first two as **rates** over 60 s. The number to read is the idle one: churn
  while nothing on screen changes is a value-type signal missing `IEquatable<T>`, a computed
  rebuilding a collection every run, or an effect writing a signal it reads. One increment per
  reaction body, under a lock already held — kept in release builds because the number is worth more
  than the increment costs.

- **Texture residency** — `gpu.textures`, `gpu.texture_bytes`, `gpu.texture_cpu_bytes` under `gpu`
  in the Variables panel and the console (`get gpu.textures`), read from the engine's own image
  cache.

  Texture handles are **caller-owned**: `LoadTexture*` hands one out and nothing but
  `ReleaseTexture` frees it, so a widget or panel that loads one and forgets is a leak for the
  process's lifetime — at 2000×3000 that is 24 MB a click. Nothing surfaced that number outside the
  smoke test, which is exactly how three of them accumulated in the editor (the asset preview's
  widget, its dimensions probe, and the tile palette's sheet). Watch the count while browsing an
  asset folder or scrolling a gallery: it should come back down. A number that only climbs names a
  missing release.

- **Attribution** — `Reactive.TrackReactions` (the panel's toggle) aggregates runs by the body's
  declaring `Type.Method`, unwrapping the compiler's closure classes, so `Reactive.HottestReactions()`
  names *which* computed or effect is churning. Opt-in: it costs a dictionary lookup per body run.
  Toggling it on resets the counts, so the table answers "what churned while I did that".

- **Per-`Watch` counts** — a `Watch` swap bumps the inherited `Widget.RebuildCount`, so the inspector
  shows `12 rebuilds` inline on the row and in its `R:` counter. Global counter says *something* is
  churning; the tree row says which subtree.
- `Zigote.Logging` — `AppLog.Bootstrap()`, file sink, `CaptureFailures()` routes `Reactive.OnError`
  and `BlocErrors.OnError` into Serilog.
- `Reactive.OnError` / `BlocErrors.OnError` — failure isolation seams; unset, failures land in
  `DebugLog`.
- `Reactive.LockTimeoutMs`, the DEBUG boxing-equality assert, the DEBUG slow-cross-thread-effect
  warning.

*(not built)*: the node-and-edge **signal-graph visualiser**. Deliberately: drawing the graph needs
every live node discoverable (a registry of weak references, maintained on the hot path) to answer a
question the hottest-bodies table already answers from a dictionary. Build it only if a real debugging
session gets stuck on graph *shape* rather than graph *volume*.

---

## Non-Goals

Reflection-based DI. Stream-based UI state. Global mutable state. Widget-centric business logic.
Mandatory MVVM.

Amended: **"no hidden thread switching"** is *declared* thread switching, not none. `Watch` marshals
off-thread rebuilds to the UI thread and `EffectAffinity.Deferred` parks bodies for the frame loop —
both visible at the call site, and both the reason a background write is safe at all.

---

## Summary

Six primitives ship — `Signal`, `Computed`, `Effect`, `Bloc`, `Watch`, and the reactive runtime that
batches them — plus `Trigger` and `LinkedSignal` for the two shapes a plain signal handles badly.
`Env` and `Scope` were designed out: constructor injection and the bloc's own lifetime cover them
without a framework type. The threading rule is not "signals belong to the UI thread" but "the graph
takes a lock; effect bodies declare where they run".
