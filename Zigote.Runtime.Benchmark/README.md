# Performance probes

The existing `vfx`, `dispatch`, `physics2d`, and `ui` probes explore individual hot loops.
`performance` and `performance-window` provide repeatable observations for the performance and
scheduling roadmap. They execute production code and fail if the expected work did not happen.

```sh
dotnet build Zigote.Runtime.Benchmark -c Release --disable-build-servers -m:1
DOTNET_TieredCompilation=0 dotnet Zigote.Runtime.Benchmark/bin/Release/net10.0/Zigote.Runtime.Benchmark.dll performance /tmp/headless-1.json
DOTNET_TieredCompilation=0 dotnet Zigote.Runtime.Benchmark/bin/Release/net10.0/Zigote.Runtime.Benchmark.dll performance-window /tmp/window-1.json
```

Run each command in three **sequential fresh processes**, changing the output suffix. Do not run
builds or other benchmarks concurrently. The window command opens a native SDL/wgpu window and
closes it after the measurements; leave it visible, stationary, and unobscured. Do not interact
with it. Record CPU/GPU, memory, OS, display refresh/scale, power mode, SDK/runtime, source
revisions and dirty working-tree status alongside results. A machine under normal desktop load
is not a controlled performance lab.

For managed-only work, `-p:ZigoteManagedPack=true` skips building the native engine. Do not use that
shortcut to validate native-window performance against changed native source. Native Release
builds use `ReleaseFast`; headless cases do not initialize the engine.

## What is measured

| Case | Work inside the measured interval | Limits |
|---|---|---|
| `background.empty_1000_calls` | 1,000 calls to `Background.RunFrame(0)` on an empty scope | Batched to exceed timer resolution; reported milliseconds are per batch. |
| `background.enqueue.N` | Enqueue N idle callbacks, including framework callback wrappers | Reuses the application's callback delegate; payload allocation is excluded. |
| `background.drain.N` | Deliver N no-op callbacks through production `Background` | Enqueue/setup excluded; generous 10-second drain budget. |
| `background.burst_400_100us` | Drain 400 callbacks in 0.5 ms slices | Each callback has **injected** 0.1 ms service time. Calls are unpaced; call count is not elapsed display latency. |
| `background.zero_budget.two_2ms_units` | One idle callback plus one slice step at zero budget | Each has **injected** 2 ms service time; demonstrates cooperative budget overshoot. |
| `paint.record.N` | Encode N rectangle commands | No native decode, rasterization, text, or GPU work. |
| `paint.diff_*_capture.N` | Diff and capture N commands, identical or one changed rectangle | Record/setup excluded; tests the actual retained paint safety pass. |
| `list.*_10000` | Measure, layout, paint, damage diff, capture a virtualized list of 10,000 rows | Only visible rows are realized. Offset setter excluded. Headless text uses approximate sizing. |
| `window.full_repaint` | `App.Frame` after requesting full damage | Includes native submission and present wait; not GPU execution time. |
| `window.idle_wait` | Settled `App.Frame` with no requested changes | Includes intentional event sleep; never classify this interval as jank. |
| `window.warm_scroll`, `window.new_rows` | Real frames over recurring or newly introduced list rows | Real native text and rendering, but no images in the list. Offset setter excluded. |
| `window.post_*.frame` | `App.Frame` draining a burst of `App.Post` callbacks | Callbacks do not invalidate UI; does not force rendering. 100 µs callback work is injected. |
| `window.post_400_100us.bounded_1ms.frame` | The same 400-callback flood with `App.PostDrainBudgetMs = 1`; each `App.Frame` until the queue empties is one sample | Unpaced frames with nothing to paint; shows per-frame drain time, not display latency. Not in the 2026-10-12 archive. |
| `window.*.native_acquire_wait`, `.native_encode_submit`, `.native_present` | Native split of each frame in the section (warmup included): time blocked acquiring the swapchain image, CPU work building/encoding/submitting, and the present call | Wall durations on the UI thread; none is GPU execution time. UI-only frames; the 3D composite path is not covered. |
| `window.unpaced.*` | The same frames with vsync and the software pacer off | On the baseline Metal setup these stayed vsync-paced, so they are not a CPU-cost measurement there. |
| `images.64x512.*` | 64 concurrent `Image.LoadAsync` calls, actual decode and texture delivery, then a final submitted frame | In-memory RGB PNGs, full resolution, no file/network I/O or codec comparison. |

The list cases warm the frame machinery before collecting samples. The new-row case then visits
fresh row labels; it intentionally includes realization and text-cache misses. The native image
case is one burst per process after the renderer is warm, **not** a steady-state image benchmark.

## Reading results

JSON contains chronological samples and nearest-rank p50/p95/p99/max, runtime metadata, and
scenario-specific notes. No slow observations are removed; no collections are forced. Quantiles
are per invocation, not best-of averages. Small sample counts (especially image-burst frames)
make p99 close to max; the single end-to-end image sample has no useful within-run distribution.

`ui_alloc` uses `GC.GetAllocatedBytesForCurrentThread` immediately around the measured action.
For headless cases this is the measurement thread. It excludes worker/native/GPU allocations.
The image case additionally records process-wide managed allocation and a working-set delta
sampled between frames. That delta is neither a true memory peak nor evidence of a leak. There
is no forced GC and no post-GC retained-size claim.

Warmups are fixed-count. The commands disable tiered compilation so methods compile optimized
upfront instead of changing tiers midway through a short case. This is a steady-work baseline,
not a startup benchmark; JSON records the setting. CPU frequency/thermal state is uncontrolled.
Inspect run-to-run spread and raw samples before
interpreting small differences. Native window timings include presentation backpressure;
they cannot establish GPU duration, missed display deadlines, or input-to-photon latency.
Profiler scopes are enabled for window runs and disabled for headless runs. The window is
explicitly shown and activated; the harness requires scene-texture allocation before measuring
and verifies 64 MiB of image GPU residency after the burst. Surface availability can still
change during a run; these checks do not replace per-frame presentation feedback.

Metadata records `dropped_frames` (and per section) — frames native skipped because the swapchain
image was unavailable. Drops while the window is still appearing are normal; a run with drops in a
measured section is not presentation-paced and should be discarded. After the image burst and a
512-load cancelled flood the probe requires image count and GPU bytes to return to their baseline.
The run ends with a partial-versus-full check: 24 partial repaints of a 48-cell grid, each
compared byte for byte against a forced full repaint of the retained scene texture.
The display must be awake: an occluded or sleeping display skips every frame.

These are baseline probes, not CI wall-clock assertions. Correctness checks fail on lost work,
wrong paint-diff outcomes, stalled scrolling, decode failure, or a 30-second image timeout.
Use the allocation tests in `Zigote.Tests` for exact steady-path regression gates. Extend this
suite with presentation feedback, image cancellation/soak tests, real media workloads, and
target-device runs before making release performance claims.
