# Engine-level optimization: parallelism, atomics and SIMD

A design document for spreading Zigote's frame across more than one core, and for the data-layout
work that has to happen first. Written against the tree at `5e59c82`, measured on a Ryzen 7 PRO
8840U (16 threads, Radeon 780M).

Everything here is grounded in code that exists today. Where an item was already assessed and
deferred, this document says so and either upgrades it or explains why it stays deferred.

> **Status (2026-09-01).** Phase 1 has landed. Phase 2 was **measured and dropped** — the numbers
> are below, and the probe that produced them ships as `Zigote.Runtime.Benchmark`. Phase 3's
> prerequisite audit ran and found a blocker the first draft did not know about; it is written up
> in [W1](#w1--the-render-thread). Nothing in this document is now an unmeasured assertion except
> where it says so.

**Contents:** [Status](#what-landed) · [Verdict](#verdict) · [What runs where today](#what-runs-where-today) ·
[The UI pipeline](#the-ui-pipeline-measured-and-there-is-nothing-to-optimise) ·
[The frame is one dependency chain](#the-frame-is-one-dependency-chain) ·
[Invariants](#invariants-anything-here-must-not-break) · [Work streams](#work-streams) ·
[The reactive lock](#the-reactive-lock-is-the-ceiling) · [Atomics review](#atomics-review) ·
[SIMD review](#simd-review) · [Measurement](#measurement) · [Phasing](#phasing) ·
[Not building](#deliberately-not-building)

---

## Verdict

Zigote runs **one fat frame thread**. Event pump, reactive drain, background delivery, the
fixed-step gameplay loop, layout, paint recording, damage diff, the FFI submit, GPU command
encoding, the vsync-blocking swapchain acquire and present all happen serially on it. The only
in-frame parallelism is two fork-join bursts — Jolt's job system inside `PhysicsStep`, and the
texture-decode worker pool — and both block their caller.

The measured worst case (`ZIGOTE_IMAGEGRID_BENCH` scroll) is **73% of one core with fifteen threads
idle**. The ceiling is not CPU throughput. It is that the frame is a single chain.

Ranked by payoff over risk:

| # | Item | Payoff | Risk | Verdict |
|---|---|---|---|---|
| 1 | **Render thread** — move `FrameEnd` off the UI thread | Largest. Recovers the whole vsync block | Medium | **Blocked** on a text-renderer split — see [W1](#w1--the-render-thread) |
| 2 | **Atomics hardening** (TTAS spin, budget from frame budget, Jolt thread clamp) | Small but free | Very low | ✅ **Landed** |
| 3 | **Scene-graph flattening** in `world.zig` | Unlocks 1 and 5 on the Zig side | Medium | Next, once measured |
| 4 | **Parallel paint recording** | Real on big trees | Medium-high | Only if 1 doesn't clear the budget |
| 5 | **flecs multithreading** | Native systems only | Low | Narrow scope, say the contract |
| 6 | ~~**SoA + SIMD** (particles, 2D colliders, sprite packing)~~ | ~~4–8× on the arithmetic~~ | Low | ❌ **Dropped — measured.** Both loops are under 1% of a core. [Numbers](#simd-review) |
| 7 | **Parallel layout** | Small | High | Park — the prerequisite is a `BuildContext` rewrite |
| 8 | **Reactive lock redesign** | Would raise the ceiling | Very high | Stays deferred |

The one-line version: **the frame does not need more threads so much as it needs its tail cut
off.** The first draft of this table also said "and its hot arrays made contiguous" — that half was
wrong, and the probe that disproved it is the most useful thing this exercise produced.

---

## What landed

| Change | Where | Measured effect |
|---|---|---|
| `SpinLock` → test-and-test-and-set | `Zigote.Engine/src/core/sync.zig` | **Neutral to 1.26×, by contention.** 2 threads 0.99×, 4 threads 1.05×, 8 threads 1.06×, **16 threads 1.26×** (88.9 → 70.3 ns per acquisition, best of 5 interleaved). Today's contention is near zero — the comment on the lock says the image-registry sections "never get past the spin phase" — so this buys nothing yet. It is insurance for [W1](#w1--the-render-thread), where a render thread starts contending the image registry for real. |
| Background slice takes what is left of the frame | `App.BackgroundBudget()`, `App.cs` | **Not a throughput change — a worst-case frame-time change**, so there is no "×" to quote. The flat 4 ms was 24% of a 60 Hz frame but **58% of a 144 Hz one**; a full slice on a frame that also laid out is what dropped it. Now `min(4 ms, budget − spent)`. An over-budget frame yields 0, which `RunFrame` degrades to one unit per queue rather than starvation. |
| Jolt worker count derived from the body budget | `PhysicsWorld.WorkerCount`, `Zigote.Core` | **Unmeasured — reasoned only.** `zigote_physics_init` requires an initialized engine (window + GPU), so there is no headless A/B. The argument: `-1` means `hardware_concurrency − 1`, i.e. a 15-thread Jolt barrier for a scene with forty crates, twice per rendered frame at the 120 Hz fixed step. Now `clamp(maxBodies / 256, 1, cores − 1)` — 4 workers for the default 1024-body world. Explicit counts still pass through, so an over-tight heuristic is one argument away from being overridden. **Verify with `ZIGOTE_SMOKE_OBJECTS` before trusting it.** |
| `ApplyRange` on the five modules that lacked it | `Zigote.Vfx/VfxUpdateModule.cs` | **1.00×–1.64×, per module.** See the correction below — the first draft of this row claimed −13% on the whole step, which was frequency-scaling noise, not a result. |
| `Zigote.Runtime.Benchmark` | new project, in `Zigote.sln` | The probe harness. `-- vfx`, `-- dispatch`, `-- physics2d`, `-- all`. Every number in this document is its output. |

### Correction: the `ApplyRange` number

The first version of this document reported the `ApplyRange` overrides as **−13% on the whole
particle step**, from a 100.9 µs reading before and 87.6 µs after. That was wrong. Repeat runs of the
*unchanged* build produced 87.6, 89.0 and 98.8 µs — the spread across identical binaries is as large
as the effect being claimed. This machine is a laptop on battery and its frequency scaling swamps a
10% effect measured across process invocations.

`-- dispatch` measures it honestly instead: both shapes declared in one process, alternated
run-by-run so drift hits each equally, best of seven. Three consecutive runs:

| Module | Inherited `ApplyRange` | Overridden | Speedup |
|---|---|---|---|
| size-over-life | 25.9 / 25.3 / 24.7 µs | 25.9 / 25.2 / 24.6 µs | **1.00×** |
| color-over-life | 18.1 / 20.3 / 20.2 µs | 16.6 / 18.7 / 18.3 µs | **1.09×** |
| vortex | 20.7 / 20.7 / 20.1 µs | 12.8 / 12.8 / 12.3 µs | **1.62×** |

Reproducible to two decimal places, and more interesting than the number it replaced:

- **`size-over-life` gains nothing.** .NET 10's dynamic PGO already devirtualizes the monomorphic
  call site, and the body's real cost is the non-inlinable `Curve.Evaluate` — which the override
  cannot help. The "one virtual call per particle" story is not automatically true on a modern JIT.
- **`vortex` gains 1.62%.** Its body is pure arithmetic (a cross product), so once the loop sees the
  sealed type the whole thing inlines into a tight loop. The win is inlining, not dispatch.
- **Whole-step effect with the default module set** (gravity, drag, size-over-life, color-over-life)
  is only `color-over-life`'s ~1.7 µs out of ~88 µs — **about 2%**. An emitter that uses vortex or
  turbulence gets meaningfully more.

The change is still right — it makes seven modules consistent where five were accidentally paying
for a base-class fallback the doc comment says exists to be overridden — but it is a 2% change on a
subsystem worth 1.1% of a core, not a 13% one.

Tests: 1668 run, 3 failures, all three pre-existing — `VfxEditorTests.GraphSerializer_RoundTripsPreset`
(already failing before this work), the `VideoPlayerTests` HTTPS network flake, and
`ReactiveCountersTests.Attribution_names_the_body_that_ran`, which passes in isolation and fails
only under parallel collections when another test's `ReactiveExtensions.Observe` leaks into the
attribution table.

---

## What runs where today

| Thread | Owner | Lifetime | Runs | Synchronisation |
|---|---|---|---|---|
| **UI / main** | `App.Frame()` | process | SDL poll, `Reactive.DrainDeferred`, `AudioUpdate`, posted work, `Background.RunFrame(4ms)`, `GameSession.Update` (120 Hz fixed loop: scripts → physics → vfx → anim), layout, event dispatch, tickers, paint walk, damage diff, `SubmitPaintCommands`, `FrameEnd` (render graph + present) | owns everything |
| **.NET thread pool** | `Background` scopes, `async` continuations | process | asset IO, image decode, HTTP, `Latest` work | `ConcurrentQueue` (`_posted`, `_crossThreadInvalidations`), signal writes under `Reactive.Gate`, `Background.RunFrame` on the frame thread |
| **Jolt job workers** | `JPC_JobSystem_Create(2048, 8, -1)` | process | broad/narrow phase, solver | Jolt-internal barriers; the calling thread participates |
| **Texture decode** | `textureDecodeWorker` (root.zig:3862) | per batch — spawned and joined each call | stb decode | `std.atomic.Value(usize)` cursor |
| **miniaudio device** | miniaudio | while audio open | the node graph | `state.audio_lock` (`SpinLock`) |
| **netstream decode** | `netstream.zig:170` | per stream | video decode | `SpinLock` + atomic counters |
| **stderr drain** | `root.zig:1506` | process | pipe drain | none |

Two things stand out. First, the engine's own architecture doc states the contract plainly:
*"C# owns the frame loop. Zig is a passive library — it never spins its own render thread."*
Any render thread is therefore **C#'s to create**, calling the same FFI. That is a feature: it
keeps the engine's contract intact.

Second, `root.zig:517` already carries `image_lock: SpinLock`, documented as *"Guards the image
registry against a worker thread decoding while the render thread paints."* The seam a render
thread needs is already there and already locked. Somebody was thinking about this.

---

## The frame is one dependency chain

```
WaitEvents ─ PollEvents ─ DrainDeferred ─ AudioUpdate ─ DrainPosted ─ Background.RunFrame(≤4ms)
   └─ [game] N× ( Scripts ─ Physics ⟨fork/join⟩ ─ Vfx ─ Anim ) ─ transform read-back
   └─ LayoutTree ─ UI.Dispatch ─ Ticker.AdvanceAll
   └─ UI.Paint(root) ─ UI.Paint(overlay) ─ UI.Damage (snapshot diff)
   └─ Render.Submit2D ⟨FFI memcpy⟩ ─ SubmitFrameDamage
   └─ Render.GPU = zigote_frame_end
        └─ fillPaintList ─ RTPrePass ─ BlurPass ─ Scene3D×6 ─ CompositeAndPresent
             └─ surface.getCurrentTexture()   ← BLOCKS ON VSYNC (present_mode = .fifo)
             └─ surface.present()
   └─ PaceFrame
```

The last block is the interesting one. Everything from `Render.Submit2D` onward reads **no widget
state** — it consumes a finished byte stream and a damage rect list. And `getCurrentTexture` under
`.fifo` blocks the calling thread until the compositor releases an image. On a 60 Hz panel that is
up to ~16 ms of a core doing nothing, spent on the thread that also has to do all the layout.

That is the shape of the problem, and it is why the render thread is item 1.

---

## Invariants anything here must not break

Collected from code comments, the perf backlog, and one reverted change. Violating any of these
produces a bug that reproduces on one machine and one compositor.

1. **SDL event polling, window ops, chrome and file dialogs are main-thread only.** macOS AppKit.
   Whatever topology emerges, the OS event pump stays on the process main thread.
2. **`PaintSnapshot` content-compares blob arrays and uses array length as data length.** Pinned
   blob reuse or arena allocation breaks damage diffing. Any parallel paint that pools buffers
   must preserve per-blob array identity.
3. **Do not reintroduce the partial swapchain blit.** Reverted after visible corruption on
   Wayland/Mesa — WebGPU exposes no buffer age and the image-rotation assumption is unsound. There
   is a comment at the blit pass saying so.
4. **One `EngineState` per process** (`live_engine: std.atomic.Value(u64)`). A render thread does
   not make it shared-with-a-lock; it makes it *owned by the render thread*, with the UI thread
   reaching it only through the already-locked image registry.
5. **`Reactive.Gate` is held across user code.** A worker that reads a signal *inside a reaction*
   serialises the whole process on one lock word. Workers read snapshots, never reactions.
6. **The steady frame allocates zero managed bytes** (`HotPathAllocationTests`,
   `FrameHotPathAllocationTests`). Parallel paths need per-worker preallocated buffers and their
   own `AllocGuard` tests. No exceptions — a per-frame `Parallel.For` closure allocation is a
   regression even if it is fast.
7. **`frame.zig` deliberately deleted its dependency metadata.** `reads`/`writes`, the lifetime
   solver, `validate()` and `setEnabled` were removed because nothing used them. Executing passes
   in parallel means putting all of that back. That is the cost, and it is why the render *graph*
   is not where parallelism should start — the render *thread* is.

---

## Work streams

### W1 — The render thread

**What moves.** After paint and the damage diff, the UI thread hands off `(paint bytes, overlay
bytes, damage rects, dt, scale, scene dims)` and returns. A dedicated thread calls `BeginFrame` →
`SubmitPaintCommands` → `SubmitOverlayCommands` → `SubmitFrameDamage` → `FrameEnd`. All wgpu
access and all `EngineState` mutation live there permanently, including the `Scene3D` command
stream (today submitted from the UI thread).

**Double buffering.** `App` holds two `PaintList` instances per layer instead of one. The UI thread
records into A while the render thread consumes B, then flips. `_rootSnapshot`/`_overlaySnapshot`
stay on the UI thread — the diff is a UI-thread step against what was last *handed over*, which is
exactly what it already means.

**Backpressure: one frame in flight, and skip rather than block.** If the render thread has not
finished the previous frame, the UI thread does not produce a new one. Damage is left dirty — the
`RepaintTracker` accumulates correctly across frames, so the skipped frame's damage folds into the
next one for free. Blocking instead would re-serialise the chain and gain nothing.

**What stays on the UI thread.** `PollEventsInto`, `WaitEvents`, window chrome, file dialog pump,
`GetSafeArea`, and any widget-initiated `LoadTexture*` / `ReleaseTexture` — those go through
`image_lock`, which is what it is for.

**What follows the render thread.** `drainImageUploads` / `drainImageReleases` (they run inside
`zigote_end_frame`), `drainImageUpdates`, the transient texture pool, the GPU-memory logger,
`tryAutoCapture`.

**The synchronous-capture paths need a flush.** `ZIGOTE_SHOT`, the inspect protocol's `shot`,
`Profiler.Capture`, `SmokeTest` goldens and `RenderToTexture` all assume the frame is done when
`FrameEnd` returns. Give them one method — `Engine.FlushFrame()` — that waits for the in-flight
frame. Cheap, and it keeps every existing test honest.

**Resize.** The surface reconfigure must happen with no frame in flight: flush, reconfigure, resume.
This is also where `scene_contents_lost` must be set (existing invariant).

**Expected gain.** The vsync block and all GPU encoding leave the UI thread. On the ImageGrid scroll
bench that should turn one core at 73% into two cores at roughly 35–45% each, and — more usefully —
it roughly doubles the layout/paint budget a frame can afford before it drops one.

### The audit, and what it found

The prerequisite this document named — *"find a widget that touches `EngineState` during `Paint()`
before moving anything"* — has been run across `Zigote.UI`, `Zigote.UI.Material`,
`Zigote.UI.Adwaita` and `Zigote.UI.Charts`.

**Good news: paint recording is already engine-free.** Not one `Paint()` method in any of those
layers calls `LoadTexture*`, `ReleaseTexture`, `RegisterShader`, `LoadFont` or `MeasureText`. The
paint walk produces a byte stream and nothing else, which is exactly the seam the handoff needs.

**Bad news, and it is a real blocker: layout mutates the text renderer.** `MeasureText` is called
during *layout*, on the UI thread, which is correct and must stay that way — a parent cannot size
itself without its children's measured text. But follow where it goes:

```
zigote_measure_text            (root.zig:2950)
  └─ FreeTypeTextRenderer.measureText          — takes *FreeTypeTextRenderer, NOT *const
       └─ advanceShapedSegment
            └─ getShapedRun
                 ├─ self.shaped_run_cache.put(key, run)        (freetype_text.zig:1094)
                 └─ return self.shaped_run_cache.getPtr(key).?  ← interior pointer, escapes
```

So UI-thread layout **writes** `shaped_run_cache` and hands back a pointer *into the hash map*. A
render thread rasterising the previous frame reads that same renderer and mutates its siblings —
`layout_cache`, `atlas_pixels`, `atlas_generation`, the colour atlas. A concurrent `put` that
rehashes does not merely race the reader; it frees the storage the returned pointer names. That is
a use-after-free, not a torn read, and it would surface as intermittent glyph corruption or a crash
under text-heavy scrolling — the worst possible failure mode to debug.

`image_lock` does not cover this. It guards the *image* registry, and the text renderer has no lock
at all because until now nothing needed one.

### The prerequisite: split the text renderer's CPU and GPU halves

The fix is not a lock. A mutex on `FreeTypeTextRenderer` sits on the hottest text path in the
framework, is taken per glyph run by both threads, and cannot protect an interior pointer that
outlives the critical section anyway.

The fix is to separate the two things that renderer is:

| Half | Owns | Thread | Needed by |
|---|---|---|---|
| **Shaping** | `faces`, `shaped_run_cache` | UI | layout, synchronously |
| **Raster** | `atlas_pixels`, `layout_cache`, colour atlas, `atlas_generation`, the GPU texture | render | the frame's glyph draws |

**The architecture is already half-way there.** `freetype_text.zig:191` documents the shaping cache
as deliberately independent: *"HarfBuzz output cached independently of raster size and atlas
coordinates. This is important for … atlas resets never invalidate the expensive shaping result."*
The separation exists conceptually; what does not exist is separate *ownership*, and the interior
pointers are what stand in the way.

Concretely, in order:

1. Stop returning `getPtr` out of `getShapedRun`. Return a copy, or an index into a stable
   append-only arena. This is the load-bearing change — everything else is mechanical, and this one
   is worth doing on its own merits even if the render thread never lands.
2. Split the struct in two, with the raster half holding a pointer to the shaping half.
3. Establish the read discipline: the render thread only *reads* shaped runs, and only for text the
   UI thread already measured this frame. It never shapes. A glyph the layout pass never measured
   cannot appear in the paint stream, so this holds by construction.
4. Then, and only then, move `FrameEnd`.

**Revised risk.** Not medium — medium *after step 1–3*. Metal's `nextDrawable` off the main thread
is supported and normal, and Vulkan/D3D12 present from a worker is standard, so the platform side
was never the hard part. The text renderer is.

---

### W2 — Parallel paint recording

Only after W1, and only if W1 leaves paint as the critical path.

`Root.Paint(_paint)` is one depth-first walk appending into one growable tagged byte stream with an
`_offsets` index and sparse blob side-channels. Two things make it forkable:

- **`PaintList.Append(other)` already exists** (`PaintList.cs:~883`) and already rebases the blob
  lists' indices. That is the join primitive, written.
- **The UTF-8 cache is already `[ThreadStatic]`**, with a comment saying *"Per-thread so parallel
  painters never share it."* The affordance is in the code.

**The split.** Fork at the root's top-level children, one `PaintList` segment each, join in child
index order — deterministic, so `PaintSnapshot.Diff`'s ordinal addressing still holds.

**What must be seeded, not inherited.** Each segment starts with its own clip / transform / opacity
stack primed from the segment root's ancestry. That is a pure function of the ancestor chain,
computed on the UI thread before the fork. Getting this wrong is silent — wrong clip, right pixels,
until it isn't.

**What must be audited.** Any `Paint()` that mutates shared state: `MarkNeedsPaint`, a signal write,
a `LoadTexture` call, a lazily-built cache on a shared object. `InTreeWalk` already flags the walk;
add an `InParallelPaint` thread-static and assert in those entry points in DEBUG. That audit *is*
most of the work.

**Gate it on size.** Fork only when the previous frame's command count crosses a threshold. Not a
config knob — a heuristic read from `DebugStats.UiPaintCommands`, which is already sampled.

---

### W3 — Layout: mostly no

Layout is the obvious thing to parallelise and the wrong one.

- It is already mostly skipped. `ComposedWidget.Measure` early-returns on
  `!NeedsLayout && c == LastConstraints && _measuredGen == gen`. A steady frame re-measures almost
  nothing.
- It is inherently parent→child→parent: a parent sizes itself from its measured children.
- `BuildContext.Current` is a **mutable ambient stack** (`ctx.Push`/`ctx.Pop` around the theme
  scope, `MediaQuery` written per pass). It is shared, order-dependent and thread-hostile.

The one real opportunity is **measure-only fan-out for uniform children**: `Column`, `Row`, `Wrap`,
`ListView` measuring N children against identical constraints with no inter-child dependency is a
parallel map. The prerequisite is turning `BuildContext` from a mutable stack into an immutable
snapshot threaded through `Measure` — a change that touches every widget in the framework and every
design system on top of it.

**Verdict: park.** Named cost, named prerequisite. The cheaper wins in this area are viewport
culling and measure-cache hit rate, not threads.

---

### W4 — Physics

Jolt already forks internally. The problems are *where* and *how many*.

**Where.** `PhysicsStep` is called inside `GameSession.Update`'s `while (_accumulator >= FixedDt)`
loop at 120 Hz, synchronously, on the UI thread. At 60 Hz display that is two full fork/join
barriers per frame with the UI thread as one of the workers.

**How many.** `numThreads: -1` auto-detects — 15 workers on this machine. For the body counts a
Zigote game actually runs, Jolt spends more in barrier sync than it saves. Clamp it:
`min(hardwareThreads - 1, max(1, bodies / 256))`, exposed on `PhysicsWorld.Initialize`. Cheap,
contained, measurable with `ZIGOTE_SMOKE_OBJECTS`.

**A physics thread** is the classic next step, and the read-back path is already written for it:
`Time._interpolationAlpha` (`GameSession.cs:559`) exists precisely so rendering can interpolate
between two sim states. But scripts run in the same fixed loop and touch scene nodes, so moving
physics alone yields `scripts (UI) → physics (worker) → readback (UI)` — still serial. It only pays
once scripts have a declared data contract saying what they may touch.

**Verdict:** do the thread-count clamp now. Defer the physics thread, with the prerequisite named.

`Zigote.Physics2D.CollisionWorld2D` is a separate problem and threading is not its fix — see
[SIMD](#simd-review).

---

### W5 — ECS

`zigote_ecs_set_threads` exists and forwards straight to `ecs_set_threads`. Nothing in C# calls it,
and the C# surface is not built for it:

- `EcsWorld.ForEach` invokes a **managed delegate per entity**. Calling that from N flecs worker
  threads is a reverse-P/Invoke storm and a GC-suspension hazard.
- `_queryCache` and `_componentIds` are plain `Dictionary`, unlocked.
- `ffi/ecs.zig` states the contract: *"ECS calls are single-threaded by contract today."*

**What to do:** expose `set_threads` for **native systems only**, and write the contract into
`EcsWorld`'s xmldoc — `ForEach` is single-threaded, and enabling threads does not change that.

**The bigger ECS win is not threads.** It is not calling a managed delegate per entity at all.
flecs iterates archetype *tables*; a `ForEach` overload that hands the caller `Span<T>` per table
turns N delegate invocations into one per table, and opens each component array to vectorisation.
That is a `Zigote.Ecs.Benchmark`-measurable change with no threading risk.

---

### W6 — Audio: nothing to do, and here is why

*Investigated and closed.* An earlier draft of this document claimed the audio lock was a
priority-inversion risk on the realtime callback thread. It is not, and the proposed fix was
unsound. Both corrections are worth recording, because the reasoning is what stops the next person
re-opening it.

**There is no Zig code on the callback thread.** `src/ffi/audio.zig` registers no `callconv(.c)`
data callback and no custom node vtable — miniaudio's engine traverses the node graph internally,
under miniaudio's own locks. `state.audio_lock` is therefore acquired only by FFI callers (the
frame thread and loader threads), never by the device thread. No inversion, no xrun.

**The long hold is real, but its victim is a frame, not a buffer.**
`zigote_audio_sound_create_file` holds the lock across `engine.createSoundFromFile`, which is the
container-header parse `SpinLock`'s comment refers to. The waiter is whatever the frame thread
calls next — usually `zigote_audio_update`, which is a cheap ageing pass. So the cost is one
hitched frame while a track opens, at load time.

**Moving the parse outside the lock is not a hardening item — it is a lifetime redesign.**
`createSoundFromFile` needs `AudioState.engine`, and `zigote_audio_reopen` frees the whole
`AudioState` out from under both threads. The lock is exactly what prevents that use-after-free,
and the codebase already guards the discipline at the source level: the test *"every audio FFI
export takes audio_lock"* fails the build when a new `zigote_audio_*` export forgets it, with a
two-name exemption list for the stateless offline decode. Parsing outside the lock means
refcounting `AudioState` across `reopen` — a real design change, for a hitch on a load-time call.

**Verdict: leave it.** The TTAS change below makes the waiting itself cheaper, which is the whole
of the available win. Do not add threads to audio; it is the one subsystem that already got this
right.

---

### W7 — Asset and decode pipeline

Already partly parallel; the gaps are structural, not algorithmic.

- **`textureDecodeWorker` spawns and joins threads per batch** (`root.zig:3939-3946`, with an
  `alloc.alloc(std.Thread, want)` per call). During a scroll that decodes continuously, that is
  thread-create churn. A persistent 2–4 worker pool with a bounded ring matches the house style —
  this Zig version has no condvar, so park with `std.Thread.yield`, same as `SpinLock` does.
- **Single-image decodes never use the pool.** They go through `Background` and the .NET thread
  pool, which is correct. The ImageGrid bench's 552 MB RSS and ~73 MB/s alloc during scroll say the
  problem there is allocation and cache policy, not parallelism. Do not thread it; measure the
  allocations.
- **Upload** (`drainImageUploads`) follows the render thread under W1.

---

### W8 — Background and the frame budget

`Background` is already the right shape — frame-aware delivery, `Slice`, `Deliver.WhenIdle`,
supervised children, deterministic under test. Two small gaps:

- The budget is hardcoded: `bg.RunFrame(TimeSpan.FromMilliseconds(4))` (App.cs:1401). On a 144 Hz
  panel that is 58% of the whole frame budget. Derive it:
  `min(4ms, frameBudget - timeSpentSoFarThisFrame)`. One line, and it is the difference between
  background work smoothing a frame and background work dropping it.
- `Background` has no priorities by design. **Keep it that way.** A priority queue here is the
  feature that gets built, then tuned forever.

---

## The UI pipeline: measured, and there is nothing to optimise

The natural question after Phase 2 died is whether the *UI* side has the same problem or a real one.
It has neither: it has no problem at all.

`Zigote.Runtime.Benchmark -- ui` runs a scrolling frame headless — a 2,000-row virtualised
`ListView` of ImageGrid-shaped cards (a picture-sized block plus two `Label`s) in a 1200×900
viewport — and times the six phases separately. The existing `UiPerformanceTests` /
`ComplexUiPerformanceTests` time a *settled* tree; a scroll frame additionally runs the scroll
physics tick, row realisation/eviction, and the `PaintSnapshot` damage diff.

| Phase | µs/frame | % of 60 Hz | % of the pipeline |
|---|---|---|---|
| scroll tick | 0.18 | 0.00% | 2% |
| **measure** | **7.4** | 0.04% | **69%** |
| layout | 0.53 | 0.00% | 5% |
| paint walk | 1.95 | 0.01% | 18% |
| damage diff + capture | 0.56 | 0.00% | 5% |
| hit test | 0.10 | 0.00% | 1% |
| **TOTAL** | **~11** | **0.07%** | 100% |

**Eleven microseconds, and zero managed bytes** (`SteadyState_ZeroAllocation_AcrossSizes` and
`RealisticSettingsPage_SteadyStateAllocation` both report 0.00 B/frame over 300–500 frames). A 60 Hz
frame is 16,670 µs. The C# widget pipeline is **0.07%** of it.

So the ImageGrid bench's 73% of a core **is not in Measure → Layout → Paint**. Whatever it is —
image decode and upload, the FFI submit, the render graph, or the `.fifo` swapchain acquire that
blocks on the compositor — it is downstream of everything `Zigote.UI` does. That is now measured
rather than assumed, and it is what makes [W1](#w1--the-render-thread) the only item on this list
that can move the number.

### The one real cliff: cold text measurement

Within that 11 µs, `measure` dominates and text is about half of it. Interleaved round-by-round
(see [the variance note](#correction-the-applyrange-number) for why that matters), two consecutive
runs agree to two decimals:

| | run 1 | run 2 |
|---|---|---|
| measure, with `Label`s | 3.03 µs | 3.05 µs |
| measure, `Label`s swapped for same-sized boxes | 1.58 µs | 1.55 µs |
| → text's share of measure | 47.8% | 49.3% |

But that is the **warm** cache — the oscillating scroll revisits the same rows, so every
`TextMeasure` lookup hits. Scrolling *monotonically* through the list, where no row is ever
revisited and every label is a miss:

| | run 1 | run 2 |
|---|---|---|
| measure, warm | 3.03 µs | 3.05 µs |
| measure, cold | 69.4 µs | 66.1 µs |
| → **cost of a never-before-seen row** | **22.9×** | **21.6×** |

`TextMeasure`'s two-generation cache is doing enormous work, and the cliff behind it is steep.

**And this number is a floor, not the answer.** Headless there is no `ZigoteEngine`, so a miss falls
back to the coarse proportional estimate; the 22× is C#-side only — string materialisation, hashing,
the dictionary insert, generation eviction. With a live engine a miss *also* pays an FFI call and
HarfBuzz shaping through the very `getShapedRun` path that [blocks the render
thread](#the-audit-and-what-it-found). The probe structurally cannot measure that half.

**No optimisation is warranted yet.** Even the cold 66 µs is 0.4% of a 60 Hz frame, and the fix that
suggests itself — prefetching text measurements for rows about to enter the viewport, which
`Background`'s `Deliver.WhenIdle` already exists to schedule — should not be built against a number
whose expensive half is unmeasured. Capture the ImageGrid trace first and see whether text shaping
shows up in it at all.

---

## The reactive lock is the ceiling

One global re-entrant `object Gate`, held across compute and effect bodies. Reads *outside* a
reaction take a seqlock fast path: `Volatile.Read(_seq)` → copy → re-read `_seq`, with the writer
bumping odd/even via `Interlocked.Increment` + `Volatile.Write`. The write-side comment is correct
about why `Interlocked` and not `Volatile.Write` for the opening bump — arm64 would otherwise let
the `_value` store drift ahead of the counter.

**The consequence for every design above:** the read side is already solved for worker threads,
*provided they are not inside a reaction*. So the rule to write down and enforce is:

> A worker thread reads application state through untracked snapshot reads. It never runs a
> reaction body, and it never writes a signal from inside a parallel region — it writes once, on
> join, ideally inside a `Reactive.Batch` so N results become one effect pass.

Follow that and the lock is irrelevant to parallelism. Break it and every added thread queues on
one lock word.

**Do not replace the global gate with per-node locks.** Lock ordering across a dependency DAG is
where reactive systems go to die, and glitch-freedom — "a fan-out settles it once" — is precisely
the property fine-grained locking loses. It is benchmarked (`Zigote.Reactive.Benchmark/Contention.cs`)
and it stays deferred.

---

## Atomics review

| # | Site | Finding | Action |
|---|---|---|---|
| 1 | `core/sync.zig` `SpinLock.lock` | Spins on `cmpxchgWeak`, so **every spin iteration takes the cache line exclusive**. N waiters ping-pong the line. | Spin on a relaxed `load` until it reads `false`, *then* attempt the exchange (test-and-test-and-set). Few lines, measurable under contention, no behaviour change. |
| 2 | `audio.zig` audio lock | ~~Priority inversion on the callback thread~~ — **investigated, closed.** No Zig code runs on the miniaudio callback thread, so the lock is never taken there. The long hold (`sound_create_file`) stalls a frame, not a buffer. | None. See [W6](#w6--audio-nothing-to-do-and-here-is-why). |
| 3 | `Signal.Snapshot` | Unbounded retry loop with `Thread.SpinWait(1)`. A reader can in principle livelock against a hot writer. The write section is two stores, so this is theoretical. | Note it. If it ever shows up, bound the retries and fall back to `Reactive.Hold()`. |
| 4 | `Signal.Write` seqlock | Correct, including the arm64 ordering rationale. | None. |
| 5 | `ffi/ecs.zig` `table_lock` | Uncontended atomic per ECS call, deliberately traded for not having to remember the contract. | Keep. |
| 6 | `App._inWaitEvents` (`Volatile.Write`) | Right primitive. The race (a post landing just after the check) is inherent and bounded by `WaitEvents`' frame-budget timeout. | Keep; make sure the bound is stated in the comment. |
| 7 | `textureDecodeWorker` cursor | `std.atomic.Value(usize)` fetch-add over a job array — the correct pattern. The cost is the per-batch spawn, not the atomic. | See W7. |
| 8 | `EcsWorld._queryCache` / `_componentIds` | Unlocked `Dictionary`, safe only under the single-threaded contract, which is now reachable from C# via `set_threads`. | State the contract in xmldoc (W5). |
| 9 | `live_engine` | An atomic used as a one-entry registry. Fine and documented. | None. |

Nothing here is a live bug. #1 and #2 are the two worth doing, and both are small.

---

## SIMD review

**Where SIMD exists today:**

- **C#** — `Zigote.Core/Math3D/{Vec2,Vec3,Vec4,Quat}.cs` wrap `System.Numerics`. That is the
  complete list. No `Vector128<T>`, no `Vector256<T>`, no `Vector<T>`, no `TensorPrimitives`
  anywhere else in the managed tree.
- **Zig** — `engine/math/frustum.zig` packs all six planes into `@Vector(6, f32)` and tests a sphere
  with one `@reduce(.Or, …)`; `engine/math/mat.zig` bridges to zmath `F32x4` for mul/inverse, with a
  scalar reference test pinning the column-major transpose convention. Both are genuinely good. Every
  other `@splat` hit in `src/` is array initialisation, not SIMD.

So the SIMD ceiling is a **data-layout problem**, everywhere it matters. Ranked:

### ❌ 1. `ParticlePool` → SoA — measured, dropped

`dotnet run -c Release --project Zigote.Runtime.Benchmark -- vfx`, 10,000 particles, AVX2
(`Vector<float>.Count = 8`), on the 8840U:

```
per-pass, over a saturated pool:
  module gravity                 5.3 µs    0.53 ns/particle
  module drag                    5.4 µs    0.54 ns/particle
  module size-over-life         15.9 µs    1.59 ns/particle
  module color-over-life        37.1 µs    3.71 ns/particle
  module turbulence             82.7 µs    8.27 ns/particle   (3× MathF.Sin — not in the default set)
  module vortex                 32.0 µs    3.20 ns/particle
  integrate                     15.4 µs    1.54 ns/particle
  compaction scan                8.8 µs    0.88 ns/particle

whole step (4 modules + integrate + compact)      87.6 µs/step   8.76 ns/particle
  elementwise only, AoS                           25.3 µs/step   2.53 ns/particle
  elementwise only, SoA scalar                    43.1 µs/step   4.31 ns/particle
  elementwise only, SoA + Vector<float>            5.9 µs/step   0.59 ns/particle
```

Three things fall out, and two of them contradict the first draft of this document.

**SoA *scalar* is 1.7× slower than AoS** (43.1 vs 25.3 µs). Nine separate array streams defeat the
prefetcher worse than one contiguous 84-byte stride does. Every bit of the win is the vectorisation,
and the vectorisation is only available because of the layout — so it is all or nothing, not a
gradual improvement.

**The elementwise passes are only 29% of a step.** 4.3× on 29% is a 1.25× whole-step ceiling, and
that ceiling assumes SoA costs nothing anywhere else — which is false, because the over-life
modules read `Age`, `Lifetime`, `StartSize` and `StartColor` together and would gather from four
arrays instead of one cache line.

**And the whole subsystem is 1.1% of a core.** At the 120 Hz fixed step, 10,000 particles cost
87.6 µs × 120 = 10.5 ms per second. The entire SoA rewrite — breaking the public
`ref Particle` / `Apply` module API that third-party modules extend — would give back **0.22% of one
core**. That is the answer, and no ratio makes it a different answer.

*What the probe did find:* five of the seven shipping modules never overrode `ApplyRange`. Fixing
that took twenty lines and is worth 1.00×–1.64× depending on the module — about 2% of a step with
the default set. See [the correction](#correction-the-applyrange-number), which is also a lesson
about measuring a 10% effect across process invocations on a battery-powered laptop.

### ❌ 2. `CollisionWorld2D` → dense struct colliders — measured, dropped

`dotnet run -c Release --project Zigote.Runtime.Benchmark -- physics2d`, 20 characters
sweep-and-sliding through a solid tilemap:

| Static tiles | `Controller.Move` × 20 | Share of one core @ 120 Hz |
|---|---|---|
| 2,500 | 27.8 µs/step | 0.33% |
| 10,000 | 34.3 µs/step | 0.41% |
| 40,000 | 53.7 µs/step | 0.64% |

The backlog's diagnosis is correct — every broadphase candidate costs a
`Dictionary<uint, Collider>` lookup in `GatherCandidates` and **a second one in the query loop**,
plus a pointer chase into a heap object each time — and the mild growth from 0.33% to 0.64% as the
world grows 16× is exactly that dictionary getting colder. But the base is a rounding error, and
16× the world costs under two thirds of one percent of one core.

*If it ever does matter*, the cheap half is worth doing before the SoA rewrite: `GatherCandidates`
already holds the `Collider` when it stamps it, so making `_candidates` a `List<Collider>` (with the
id on the collider) removes the second lookup and the second chase per candidate, changes no public
API, and is perhaps thirty lines. Do that and re-measure before considering a dense array.

**A caveat on both numbers.** These were taken on a battery-powered laptop and the run-to-run spread
on the 2,500-tile case reached 4.8× — frequency scaling, not measurement error in the harness. The
conclusion survives it easily (the gate is 5% of a core and the readings are all under 1%), but do
not quote these to two significant figures.

### 3. `Renderer2D` sort-key build and instance packing — unmeasured

`Renderer2D.cs:101-140`. Keys are built scalar, sorted with `Array.Sort(keys, indices)` (8-byte
keys, introsort — fine), then 14 floats per instance are written one at a time. If `SpriteDraw` were blittable and its field order matched the
instance layout, packing becomes a `MemoryMarshal.Cast` and a copy per run. The backlog names the
prerequisite: fold the material index into the sort key's free 32 bits so materials intern. Worth
it once sprite counts reach the thousands.

**Unmeasured, deliberately.** After particles and colliders both came back under 1% of a core, a
third probe was not written — the prior is now that these gameplay loops are not where the frame
goes, and the one workload that *is* measured to hurt is a pure-UI scroll with no sprites in it. If
2D games become a real target, add a `-- sprites` probe to `Zigote.Runtime.Benchmark` and let it
decide, the same way the other two were decided.

### 4. `Zigote.Engine` scene graph → flat SoA — the one still worth doing

The backlog calls this *"the one genuinely
data-oriented gap on the Zig side"* and it is the highest-leverage item in this document after the
render thread. Each `SceneNode` is individually `allocator.create`d with an `ArrayListUnmanaged` of
child pointers, and `world.zig` walks that pointer tree 3–5× per frame (`updateTransforms`,
`collectRenderables`, `collectLights`, plus `isMeshReferenced` / `isMaterialReferenced` subtree
scans). Flattening to parent-sorted arrays — `parents: []u32`, `local: []Transform`,
`world: []Mat4` — makes `updateTransforms` **a single forward pass with no recursion** (a
parent's index is always below its children's), makes it vectorisable, and makes it parallelisable
by depth level. This is where SIMD and threading stop being separate projects.

### 5. Not SIMD, and shouldn't be

The paint-stream decode (`fillPaintList`, `wgpu.zig`
2300–2650) is a tagged-byte walk with a switch per record — the existing `appendOrMerge*`
run-length merging is the right optimisation, and the backlog is correct that full sorting needs an
overlap test to stay correct under alpha blending. Text raster belongs to FreeType. Blur weights
are already incremental; the backlog's downsample-to-½/¼ is the real 4–8× there, not wider vectors.

**Package note:** `TensorPrimitives` lives in `System.Numerics.Tensors`, a separate NuGet package.
`Vector<float>` is in the BCL and costs nothing. Prefer `Vector<float>` — do not take a dependency
for three loops.

---

## Measurement

Nothing above ships without a before and an after from a harness that already exists.

| Item | Harness | Baseline |
|---|---|---|
| Render thread, paint recording | `ZIGOTE_GALLERY_PAGE="Image Grid"` + `ZIGOTE_IMAGEGRID_BENCH=1` | load 35% CPU / 16% GPU / 281 MB · scroll 73% CPU / 19% GPU / 552 MB · idle 2.2% CPU |
| Particle simulation | `Zigote.Runtime.Benchmark -- vfx` | 87.6 µs/step at 10k particles = 1.1% of a core |
| 2D collision | `Zigote.Runtime.Benchmark -- physics2d` | 27.8–53.7 µs/step for 20 characters = 0.33–0.64% of a core |
| Virtual dispatch in `ApplyRange` | `Zigote.Runtime.Benchmark -- dispatch` | 1.00× / 1.09× / 1.62× by module, interleaved in one process |
| UI scroll frame, per phase | `Zigote.Runtime.Benchmark -- ui` | ~11 µs total = 0.07% of a 60 Hz frame; measure 69%, text ~half of that; cold text 22× warm |
| `SpinLock` contention | the throwaway in `docs/notes/` history; re-create from `sync.zig` | 0.99× at 2 threads → 1.26× at 16 |
| Sprite batching | `Zigote.Runtime.Benchmark -- sprites` | **not written yet** — the third Phase 2 item |
| Scene graph, physics threads | `ZIGOTE_SMOKE_OBJECTS` | to capture |
| ECS span iteration | `Zigote.Ecs.Benchmark` | existing |
| Reactive contention | `Zigote.Reactive.Benchmark/Contention.cs` | existing |
| Every parallel path | a new `AllocGuard.AssertZeroAlloc` test | zero bytes |

**The profiler is already thread-aware.** `Profiler.Scope` uses a `[ThreadStatic] _depth`, records
`ThreadId` per event, and the Chrome-Trace writer emits it as `tid` — so a render thread shows up
in Perfetto as its own track with no profiler work at all. That is worth knowing before starting:
phase 0 can produce the multi-track trace that justifies (or kills) each item below.

One caveat that costs hours if forgotten: **the bench ticker must stop in the idle phase.** An
active non-marking ticker gets a conservative full repaint every frame (App.cs ~1457, which carries
its own `ponytail:` note), which fakes ~15% CPU at "idle".

---

## Phasing

**✅ Phase 0 — measure.** Done for the gameplay side: `Zigote.Runtime.Benchmark` ships and its
output is quoted throughout. **Still owed:** the multi-track trace of the ImageGrid scroll bench,
which is the only workload measured to be a real problem (73% of one core) and the only one that
needs a running window to capture. That one is the user's to run — `ZIGOTE_GALLERY_PAGE="Image Grid"`
with `ZIGOTE_IMAGEGRID_BENCH=1`, under `dotnet-trace collect --providers Zigote-Engine:0x1:5`, then
open the Chrome-Trace in Perfetto. Remember the idle-phase ticker caveat below.

**✅ Phase 1 — free wins.** TTAS spin lock, `Background` budget from the frame budget, Jolt
thread-count clamp. All landed. *(A fourth item, an audio callback assert, was investigated and
dropped — see [W6](#w6--audio-nothing-to-do-and-here-is-why).)*

**❌ Phase 2 — data layout.** *Cancelled by measurement.* Particle SoA would return 0.22% of a core
for a public API break; the 2D collider chase is under 1% of a core at 40,000 tiles. The
`ApplyRange` fix the probe turned up (−13% on the particle step) landed instead. See
[SIMD review](#simd-review).

**Phase 3 — the render thread.** Now the top item, and now known to be gated on splitting
`FreeTypeTextRenderer` into a UI-owned shaping half and a render-owned raster half. Step 1 of that
split — not returning an interior hash-map pointer from `getShapedRun` — is worth landing on its own
merits regardless. See [W1](#w1--the-render-thread).

**Phase 4 — scene-graph flattening in Zig.** Unblocks parallel transform update and vectorised
culling. Measure it first with `ZIGOTE_SMOKE_OBJECTS` — Phase 2 is the standing reminder that the
backlog's data-layout intuitions do not always survive a probe.

**Phase 5 — parallel paint recording.** Only if 3 and 4 leave paint on the critical path.

**Parked, with reasons stated above:** parallel layout, the reactive lock redesign, C#-side ECS
multithreading, a physics thread.

---

## What this exercise taught, in one line

Two of the three data-layout items the perf backlog had been carrying were worth **0.22%** and
**under 1%** of a core, and the actual win in that subsystem was a missing `override` keyword on
five classes. The backlog's own note said *"profile first"*. It was right, and the probe that says
so now ships next to the code.

---

## Deliberately not building

- **A task or job framework in C#.** `Background` plus `Parallel.For` and the thread pool cover
  every item here. A custom work-stealing scheduler is the classic thing that gets built and then
  benchmarks slower than the pool.
- **A real render graph in `frame.zig`.** Its metadata was deleted for cause: nothing read it. The
  parallelism worth having is a render *thread*, not parallel passes.
- **The partial swapchain blit.** Reverted; see invariant 3.
- **Per-node reactive locks.** See above.
- **`System.Numerics.Tensors`** as a dependency for three loops.
- **Threads in audio.** It is already right. Harden the one rule and leave it alone.
