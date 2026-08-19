# Profiling and performance

How to find out where a frame's time and memory go — from inside the app, from an agent over the
inspect socket, and from external .NET profilers (Rider's dotTrace, `dotnet-counters`,
`dotnet-trace`).

## The layers

| Layer | What it gives you |
|---|---|
| `Profiler` (Zigote.Core.Diagnostics) | Scoped CPU timings: `using (Profiler.Scope("UI.Layout")) { … }`. Zero-alloc, double-buffered; the frame loop brackets each frame with `BeginFrame`/`EndFrame`. |
| DevTools Profiler panel (<kbd>Shift</kbd>+<kbd>D</kbd>) | Frame-time chart with 60/30 fps budget lines, min/avg/max, **UI alloc / frame** (near zero on a healthy steady-state app), the hottest scopes (self · total), and a 120-frame Chrome-Trace capture button. |
| `stats` / `profile` (inspect protocol + MCP tools) | The same numbers from outside a running app: `stats` is one JSON line of frame health (fps, frame ms, `alloc_kb_per_frame`, GC counts, jank); `profile N` captures N frames, writes a Chrome-Trace JSON and returns the per-scope self/total table averaged per frame. |
| `Zigote-Engine` EventSource | The bridge external profilers subscribe to — see below. |

## External profilers

The engine publishes a `Zigote-Engine` EventSource (`Zigote.Core.Diagnostics.ZigoteEventSource`).
It costs nothing until a session subscribes.

**Live counters** — frame time (ms), UI-thread alloc per frame (KB), jank frames:

```sh
dotnet-counters monitor -n YourApp --counters Zigote-Engine,System.Runtime
```

**Traces with engine scopes** — keyword `0x1` at Verbose mirrors every `Profiler.Scope` as
start/stop events:

```sh
dotnet-trace collect -n YourApp --providers Zigote-Engine:0x1:5
```

**Rider / dotTrace** — profile the run configuration (or attach to the process) with the Timeline
profiler as usual; dotTrace's own sampling shows the managed hot methods, and the `Zigote-Engine`
events line up engine scopes against them in the Events view. For allocation hunting, use the
Memory profiler or the in-app "UI alloc / frame" readout to catch a regression first, then
dotTrace to attribute it.

**Perfetto / chrome://tracing** — the Chrome-Trace JSON written by the DevTools capture button,
`Profiler.Capture(frames, path)`, or the `profile` inspect command opens directly in
[ui.perfetto.dev](https://ui.perfetto.dev).

## Keeping the hot path allocation-free

The frame loop's steady state — measure, layout, paint, hit test, animation tick, damage diff,
scroll, ECS iteration — must allocate zero managed bytes per frame. Two guards enforce it:

- **In app**: the Profiler panel's "UI alloc / frame" readout (also `alloc_kb_per_frame` in
  `stats`). Sustained non-zero while nothing rebuilds is a regression.
- **In tests**: `AllocGuard.AssertZeroAlloc(() => …)` (Zigote.Tests) — warm past tiered JIT, then
  assert exactly zero bytes. `HotPathAllocationTests` gates the Measure→Layout→Paint pass;
  `FrameHotPathAllocationTests` gates hit testing, ticker advance, scrolling frames, the paint
  snapshot diff, attach/detach cascades, ECS `ForEach` and the 2D character controller. Every new
  hot loop gets one.

Benchmarks for throughput (as opposed to allocation) live in the standalone
`Zigote.Ecs.Benchmark`, `Zigote.Bloc.Benchmark` and `Zigote.Reactive.Benchmark` console apps.
