# Local performance baseline — 2026-10-12

Three sequential fresh processes per mode, Apple M3 Pro (11 cores, 18 GiB), macOS 27.0, .NET 10.0.12. Native: wgpu Metal, 120 Hz, scale 2. Release / ReleaseFast; `DOTNET_TieredCompilation=0`. Other desktop activity and thermal/power state were uncontrolled.

See the [roadmap](../../notes/performance-stability-scheduling-roadmap.md) for decisions and the [benchmark guide](../../../Zigote.Runtime.Benchmark/README.md) for reproduction, scope, and workload checks. [Manifest](manifest.json) contains source/binary fingerprints and environment details. The source checkout had existing uncommitted changes.

Each range is **minimum–maximum of the three per-run quantiles**, not a pooled percentile or confidence interval. Max is the worst individual observation across runs. Milliseconds are per measured invocation. Do not compare unpaced managed calls with vsync-paced window calls as if both were GPU time.

## Headless

| Measurement | Unit | Samples per run | p50 range | p99 range | Worst max |
|---|---|---|---|---|---|
| `background.empty_1000_calls` | ms | 1000, 1000, 1000 | 0.0175–0.0182 | 0.0210–0.0350 | 0.0931 |
| `background.empty_1000_calls.ui_alloc` | bytes | 1000, 1000, 1000 | 0–0 | 0–0 | 0 |
| `background.enqueue.1000` | ms | 100, 100, 100 | 0.0212–0.0232 | 0.0331–0.1005 | 0.6056 |
| `background.enqueue.1000.ui_alloc` | bytes | 100, 100, 100 | 104,000–104,000 | 104,000–104,000 | 104,000 |
| `background.drain.1000` | ms | 100, 100, 100 | 0.0159–0.0162 | 0.0184–0.0352 | 0.0617 |
| `background.drain.1000.ui_alloc` | bytes | 100, 100, 100 | 0–0 | 0–0 | 0 |
| `background.enqueue.10000` | ms | 100, 100, 100 | 0.1751–0.1828 | 0.7422–1.6940 | 2.7191 |
| `background.enqueue.10000.ui_alloc` | bytes | 100, 100, 100 | 1,040,000–1,040,000 | 1,040,000–1,040,000 | 1,040,000 |
| `background.drain.10000` | ms | 100, 100, 100 | 0.1572–0.1658 | 0.1737–0.2914 | 0.3061 |
| `background.drain.10000.ui_alloc` | bytes | 100, 100, 100 | 0–0 | 0–0 | 0 |
| `background.burst_400_100us.budget_0.5` | ms | 81, 81, 81 | 0.5005–0.5007 | 0.5156–0.7807 | 0.7807 |
| `background.burst_400_100us.units` | callbacks | 81, 81, 81 | 5–5 | 5–5 | 5 |
| `background.zero_budget.two_2ms_units` | ms | 40, 40, 40 | 4.0002–4.0026 | 4.0022–4.0472 | 4.0472 |
| `background.zero_budget.two_2ms_units.ui_alloc` | bytes | 40, 40, 40 | 0–0 | 0–0 | 0 |
| `paint.record.1000` | ms | 512, 512, 512 | 0.0082–0.0083 | 0.0083–0.0097 | 0.0155 |
| `paint.record.1000.ui_alloc` | bytes | 512, 512, 512 | 0–0 | 0–0 | 0 |
| `paint.diff_identical_capture.1000` | ms | 512, 512, 512 | 0.0429–0.0443 | 0.0447–0.0531 | 0.0613 |
| `paint.diff_identical_capture.1000.ui_alloc` | bytes | 512, 512, 512 | 0–0 | 0–0 | 0 |
| `paint.diff_one_change_capture.1000` | ms | 512, 512, 512 | 0.0437–0.0454 | 0.0520–0.0995 | 1.7620 |
| `paint.diff_one_change_capture.1000.ui_alloc` | bytes | 512, 512, 512 | 0–0 | 0–0 | 0 |
| `paint.record.10000` | ms | 512, 512, 512 | 0.0839–0.2016 | 0.0946–0.2722 | 1.5759 |
| `paint.record.10000.ui_alloc` | bytes | 512, 512, 512 | 0–0 | 0–0 | 0 |
| `paint.diff_identical_capture.10000` | ms | 512, 512, 512 | 0.4414–0.4470 | 0.5222–1.3389 | 6.0246 |
| `paint.diff_identical_capture.10000.ui_alloc` | bytes | 512, 512, 512 | 0–0 | 0–0 | 0 |
| `paint.diff_one_change_capture.10000` | ms | 512, 512, 512 | 0.4526–0.4592 | 0.7926–1.0866 | 1.7403 |
| `paint.diff_one_change_capture.10000.ui_alloc` | bytes | 512, 512, 512 | 0–0 | 0–0 | 0 |
| `list.static_10000` | ms | 1000, 1000, 1000 | 0.0071–0.0148 | 0.0089–0.0158 | 0.0550 |
| `list.static_10000.ui_alloc` | bytes | 1000, 1000, 1000 | 0–0 | 0–0 | 0 |
| `list.warm_scroll_10000` | ms | 1000, 1000, 1000 | 0.0048–0.0049 | 0.0072–0.0092 | 0.0447 |
| `list.warm_scroll_10000.ui_alloc` | bytes | 1000, 1000, 1000 | 0–0 | 6,720–6,720 | 6,720 |
| `list.new_rows_10000` | ms | 1000, 1000, 1000 | 0.0191–0.0307 | 0.0330–0.0360 | 0.1076 |
| `list.new_rows_10000.ui_alloc` | bytes | 1000, 1000, 1000 | 3,688–3,688 | 3,720–9,248 | 301,784 |

Raw chronological samples (gzip-compressed JSON): [run 1](headless-1.json.gz), [run 2](headless-2.json.gz), [run 3](headless-3.json.gz).

## Window

| Measurement | Unit | Samples per run | p50 range | p99 range | Worst max |
|---|---|---|---|---|---|
| `window.full_repaint` | ms | 240, 240, 240 | 8.2893–8.3381 | 8.7958–9.4144 | 295.1491 |
| `window.full_repaint.ui_alloc` | bytes | 240, 240, 240 | 0–0 | 0–0 | 5,160 |
| `window.idle_wait` | ms | 120, 120, 120 | 8.3933–8.7579 | 9.1024–9.2402 | 9.5469 |
| `window.idle_wait.ui_alloc` | bytes | 120, 120, 120 | 0–0 | 0–0 | 5,160 |
| `window.warm_scroll` | ms | 240, 240, 240 | 8.2918–8.3386 | 8.8755–9.2641 | 9.4364 |
| `window.warm_scroll.ui_alloc` | bytes | 240, 240, 240 | 0–0 | 6,880–6,880 | 6,880 |
| `window.new_rows` | ms | 240, 240, 240 | 8.3168–8.3270 | 9.3825–9.9565 | 17.0479 |
| `window.new_rows.ui_alloc` | bytes | 240, 240, 240 | 3,800–3,800 | 29,584–29,584 | 91,736 |
| `window.post_10000.frame` | ms | 30, 30, 30 | 0.1112–0.2009 | 0.1291–0.4324 | 0.4324 |
| `window.post_10000.frame.ui_alloc` | bytes | 30, 30, 30 | 0–0 | 0–0 | 0 |
| `window.post_400_100us.frame` | ms | 30, 30, 30 | 40.0255–40.0417 | 40.0687–40.1180 | 40.1180 |
| `window.post_400_100us.frame.ui_alloc` | bytes | 30, 30, 30 | 0–0 | 3,616–3,616 | 3,616 |
| `images.64x512.end_to_end` | ms | 1, 1, 1 | 59.1174–66.9411 | 59.1174–66.9411 | 66.9411 |
| `images.64x512.frames` | ms | 6, 7, 7 | 7.9737–8.4895 | 10.1208–10.8853 | 10.8853 |
| `images.64x512.ui_alloc` | bytes | 6, 7, 7 | 0–0 | 9,640–10,352 | 10,352 |
| `images.64x512.process_alloc` | bytes | 1, 1, 1 | 24,385,784–24,393,032 | 24,385,784–24,393,032 | 24,393,032 |
| `images.64x512.sampled_peak_rss_delta` | bytes | 1, 1, 1 | 104,300,544–115,130,368 | 104,300,544–115,130,368 | 115,130,368 |

Raw chronological samples (gzip-compressed JSON): [run 1](window-1.json.gz), [run 2](window-2.json.gz), [run 3](window-3.json.gz).

## Interpretation and limitations

- Enqueueing 10,000 idle results allocated 1,040,000 bytes with a reused application delegate. Warm drains, paint loops, and the static list had zero measured thread-local allocation.
- The 100 µs and 2 ms callback costs are injected service times. The ~40 ms App.Post result demonstrates drain-to-empty overload; it is not a claim that real callbacks take 100 µs.
- Native full/scroll frame times include presentation waits. The 295.1491 ms full-repaint outlier is retained and unattributed. Do not derive missed presentation counts or input latency from these samples.
- Idle frame time is intentional event wait. Background burst calls are unpaced; ~80 drain calls must not be described as measured display latency.
- The 64-image case is one burst per process: 377,730-byte PNG payloads cloned per request, 512 × 512 decode, all 67,108,864 image GPU bytes verified resident. End-to-end time includes a final submitted frame, not verified on-screen presentation.
- Image process allocation is managed allocation across threads; sampled RSS is a noisy between-frame delta and excludes an exact peak/retained-size attribution. No forced GC or leak claim.
- Fixed-count warmup and disabled tiering remove tier-transition ambiguity but not CPU frequency, desktop scheduling, or GC variance. Large run-to-run differences must not be treated as optimization wins.
- Preliminary measurements with a concurrent stalled build, tier transitions, unsupported BMP input, or unavailable surface were discarded before this archive. Successful runs explicitly activated the window and verified scene-texture allocation before timing.
- No framework competitor, Windows/Linux/phone, GPU-timestamp, input-to-photon, or long-duration stability measurements were performed.

## Validation

Release build succeeded. All six archived runs passed their workload assertions; archive checks
recomputed every quantile from raw samples, checked finite/nonnegative values, verified the
specified zero-allocation cases, and confirmed 64 MiB of image GPU residency in each window run.
The build reported the existing CA2014 warning in `UiScrollProbe.cs`; the new probes introduce no
build warnings. No production behavior was changed by this work, and the full repository test
suite was not rerun.
