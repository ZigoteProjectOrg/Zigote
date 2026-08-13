using System.Diagnostics;
using Zigote.Core.Animation;
using Zigote.Core.Threading;
using Zigote.UI.Debug;

namespace AdwaitaGallery.Pages;

/// <summary>
///     The concurrency stress page: OS threads writing signals flat out, a burst of background
///     results landing under a frame budget, and a big chunk of UI work spread over frames. Three
///     things the page is here to show, in order:
///     <list type="number">
///         <item>
///             <b>Signals are thread-safe.</b> N dedicated threads each hammer their own
///             <see cref="Signal{T}" /> with no lock of their own, a <see cref="Computed{T}" /> sums
///             them, and the sum the UI reads is always exactly the sum of the parts.
///         </item>
///         <item>
///             <b>Delivery is frame-aware.</b> The same 500 results delivered
///             <see cref="Deliver.Next" /> land in one frame and cost a visible hitch;
///             <see cref="Deliver.WhenIdle" /> spends what is left of each frame and costs none.
///             The worst frame during the burst is measured either way.
///         </item>
///         <item>
///             <b>UI work can be sliced.</b> 20 000 units through <see cref="Background.Slice" />
///             fills in over frames instead of freezing the window for the whole run.
///         </item>
///     </list>
///     <para>
///         Nothing on the hot paths is read directly by a widget. The counters are written millions
///         of times a second; a <c>Watch</c> on one would rebuild millions of times a second. The
///         per-frame pump samples them and publishes one readout signal per quarter second, which is
///         the shape any real app wants for a high-frequency source.
///     </para>
/// </summary>
public sealed class ConcurrencyPage : ComposedWidget
{
    private const int MaxWorkers = 16;
    private const int BurstSize = 500;
    private const int SliceUnits = 20_000;
    private const float SamplePeriod = 0.25f;
    private readonly Signal<string> _burst = new("no burst yet");

    /// <summary>One per worker thread, written with no synchronisation beyond the signal's own.</summary>
    private readonly Signal<long>[] _counters =
        [.. Enumerable.Range(start: 0, count: MaxWorkers).Select(_ => new Signal<long>(0))];

    private readonly Signal<string> _frame = new("—");
    private readonly Signal<float> _progress = new(0f);

    // Published once per SamplePeriod by the pump — the only signals any widget subscribes to.
    private readonly Signal<string> _rate = new("stopped");
    private readonly Signal<bool> _running = new(false);
    private readonly Signal<string> _slice = new("not started");
    private readonly Signal<string> _split = new("—");

    private readonly Signal<int> _workers = new(Math.Min(val1: 4, val2: MaxWorkers));

    /// <summary>The fan-in. Invalidated by every write from every thread; recomputed once per sample.</summary>
    private readonly Computed<long> _writes;

    private Background? _background;
    private long _burstClock;

    private int _burstLeft;
    private string _burstMode = "";
    private float _burstWorstMs;

    private long _lastWrites;
    private Ticker? _pump;
    private float _sampled;

    /// <summary>Kept so the compiler cannot delete the work the slice and the burst exist to do.</summary>
    private long _sink;

    private int _sliceDone;
    private CancellationTokenSource? _stop;

    private Thread[]? _threads;

    public ConcurrencyPage()
    {
        _writes = Computed.From(() =>
            {
                long total = 0L;
                foreach (var counter in _counters) total += counter.Value;
                return total;
            }
        );
    }

    /// <summary>Threads to offer: more than there are cores measures the scheduler, not the graph.</summary>
    private static int WorkerLimit => Math.Clamp(
        value: Environment.ProcessorCount,
        min: 1,
        max: MaxWorkers
    );

    protected override void OnMount()
    {
        base.OnMount();
        _background = new Background(
            toUi: action => App.Active?.Post(action),
            requestFrame: () => App.Active?.RequestLayout()
        );
        // Owned: disposed with the mount period, which stops the pump when the page is navigated away.
        _pump = CreateTicker(Pump);
    }

    protected override void OnUnmount()
    {
        StopWorkers();
        _background?.Dispose();
        _background = null;
        _pump = null; // Own() disposes it
        base.OnUnmount();
    }

    // ── the frame's side ──────────────────────────────────────────────────────

    /// <summary>
    ///     Drains background deliveries and slices, then samples the counters. Running only while
    ///     there is something to sample: a ticker that never stops would keep the whole gallery
    ///     painting every frame for a page nobody is stressing.
    /// </summary>
    private void Pump(float dt)
    {
        _background!.RunFrame(TimeSpan.FromMilliseconds(4));

        // The number the two delivery modes differ in. Sampled here rather than in OnResult, because
        // the frame a Next burst ruins is the frame no result is being delivered on.
        if (_burstLeft > 0) _burstWorstMs = MathF.Max(x: _burstWorstMs, y: dt * 1000f);

        _sampled += dt;
        if (_sampled < SamplePeriod) return;

        long total = _writes.Value;
        long delta = total - _lastWrites;
        _lastWrites = total;

        _rate.Value = _running.Value
            ? $"{delta / _sampled / 1e6:0.00} M writes/s · {total:N0} total"
            : $"stopped · {total:N0} total";
        _split.Value = Split();
        _frame.Value = $"{DebugStats.FrameMs:0.0} ms · {DebugStats.Fps:0} fps";
        if (_sliceDone > 0) _progress.Value = _sliceDone / (float)SliceUnits;
        _sampled = 0f;

        if (_running.Value || _burstLeft > 0 || !_background.FrameIdle) return;
        _pump?.Stop();
    }

    /// <summary>Per-thread totals, so the counters are visibly independent rather than one number.</summary>
    private string Split()
    {
        var parts = new List<string>();
        for (int i = 0; i < _counters.Length; i++)
        {
            long value = _counters[i].Peek();
            if (value > 0) parts.Add($"{value / 1e6:0.0}M");
        }

        return parts.Count == 0 ? "—" : string.Join(separator: "  ", values: parts);
    }

    private void Wake()
    {
        _pump?.Start();
        App.Active?.RequestLayout();
    }

    // ── threads ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Real OS threads, not <see cref="Background.Run(Action)" />: a thread that never returns is
    ///     the one thing a pool must not be handed, and starving the pool here would also starve the
    ///     burst below — which is the demo two sections down.
    /// </summary>
    private void StartWorkers()
    {
        StopWorkers();

        foreach (var counter in _counters) counter.Value = 0;
        _lastWrites = 0;

        int count = Math.Min(val1: _workers.Value, val2: WorkerLimit);
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            var slot = _counters[i];
            _threads[i] = new Thread(() =>
                {
                    long written = 0;
                    while (!token.IsCancellationRequested)
                    {
                        slot.Value = ++written;
                        // ponytail: a scheduling point every 256 writes so a machine with fewer cores
                        // than workers stays usable. Drop it for a pure throughput number.
                        if ((written & 0xFF) == 0) Thread.Sleep(0);
                    }
                }
            ) {
                IsBackground = true,
                Name = $"zigote-signal-spin-{i}",
            };
            _threads[i].Start();
        }

        _running.Value = true;
        Wake();
    }

    private void StopWorkers()
    {
        _stop?.Cancel();
        // Bounded: the loop checks the token every write, so this is microseconds, not a hang.
        if (_threads is { } threads)
        {
            foreach (var thread in threads)
                thread.Join();
        }

        _stop?.Dispose();
        _stop = null;
        _threads = null;
        _running.Value = false;
    }

    // ── background delivery ───────────────────────────────────────────────────

    /// <summary>
    ///     <see cref="BurstSize" /> independent jobs, each a scrap of CPU work, all finishing at
    ///     roughly the same moment. The only difference between the two buttons is where the results
    ///     are allowed to land.
    /// </summary>
    private void Burst(Deliver deliver)
    {
        _burstLeft = BurstSize;
        _burstWorstMs = 0f;
        _burstMode = deliver == Deliver.Next ? "Next" : "WhenIdle";
        _burstClock = Stopwatch.GetTimestamp();
        _burst.Value = $"{_burstMode}: 0/{BurstSize}";

        for (int i = 0; i < BurstSize; i++)
        {
            int seed = i;
            _background!.Run(work: () => Mix(seed), onUi: OnResult, deliver: deliver);
        }

        Wake();
    }

    /// <summary>UI thread, once per result — the delivery is what is being measured, not this.</summary>
    private void OnResult(long value)
    {
        _sink += value;
        if (--_burstLeft > 0) return;

        double ms = (Stopwatch.GetTimestamp() - _burstClock) * 1000.0 / Stopwatch.Frequency;
        _burst.Value =
            $"{_burstMode}: {BurstSize} results in {ms:0} ms · worst frame {_burstWorstMs:0.0} ms";
    }

    // ── sliced UI work ────────────────────────────────────────────────────────

    private void BuildRows()
    {
        _sliceDone = 0;
        _progress.Value = 0f;
        _slice.Value = "building…";
        long clock = Stopwatch.GetTimestamp();

        _background!.Slice(
            key: "rows",
            count: SliceUnits,
            step: i =>
            {
                _sink += Mix(i);
                _sliceDone = i + 1;
            },
            onDone: () =>
            {
                _progress.Value = 1f;
                double ms = (Stopwatch.GetTimestamp() - clock) * 1000.0 / Stopwatch.Frequency;
                _slice.Value = $"{SliceUnits:N0} units in {ms:0} ms of wall clock, 4 ms a frame";
            }
        );

        Wake();
    }

    /// <summary>A few microseconds of arithmetic — a stand-in for a parse, a decode, a row build.</summary>
    private static long Mix(int seed)
    {
        ulong h = (ulong)seed * 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < 2000; i++)
        {
            h ^= h >> 33;
            h *= 0xFF51AFD7ED558CCDUL;
        }

        return (long)(h & 0xFFFF);
    }

    // ── the page ──────────────────────────────────────────────────────────────

    protected override Widget Build(BuildContext context)
    {
        return new GalleryPage(
            title: "Concurrency",
            description:
            "Threads writing signals, results landing under a frame budget, and work sliced across frames.",
            iconName: MaterialIcons.Speed
        ) {
            ClampWidth = 680f,
            Children = {
                Demo.Group(
                    title: "Workers",
                    description:
                    $"Dedicated threads, each writing its own signal in a tight loop. This machine has {Environment.ProcessorCount} cores.",
                    new AdwActionRow(title: "Threads", subtitle: $"1 to {WorkerLimit}") {
                        Suffixes = {
                            new AdwSpinButton(
                                value: _workers.Peek(),
                                min: 1,
                                max: WorkerLimit,
                                step: 1,
                                onChanged: v =>
                                {
                                    _workers.Value = (int)v;
                                    if (_running.Peek()) StartWorkers(); // restart at the new width
                                }
                            ),
                        },
                    },
                    new Watch(() => new AdwSwitchRow(
                            title: "Running",
                            subtitle: "Off cancels every thread and joins them",
                            value: _running.Value,
                            onChanged: on =>
                            {
                                if (on) StartWorkers();
                                else
                                {
                                    StopWorkers();
                                    Wake(); // one last sample so the readout settles
                                }
                            }
                        )
                    )
                ),
                Demo.Titled(
                    title: "Concurrent Signals",
                    description:
                    "No lock in the worker. The sum is a Computed over every counter, read once a frame.",
                    child: Demo.Specimen(
                        new Watch(() => Demo.Value(_rate.Value)),
                        new Watch(() => Demo.Caption(_split.Value)),
                        new Watch(() => Demo.Caption($"frame  {_frame.Value}"))
                    )
                ),
                Demo.Titled(
                    title: "Delivery",
                    description:
                    $"{BurstSize} background results finishing at once. Next takes the frame it lands on; WhenIdle takes what is left of several.",
                    child: Demo.Specimen(
                        Demo.Bar(
                            new AdwButton(
                                label: "Burst · Next",
                                onPressed: () => Burst(Deliver.Next)
                            ),
                            new AdwButton(
                                label: "Burst · WhenIdle",
                                onPressed: () => Burst(Deliver.WhenIdle)
                            ) {
                                Style = AdwButtonStyle.Suggested,
                            }
                        ),
                        new Watch(() => Demo.Value(_burst.Value))
                    )
                ),
                Demo.Titled(
                    title: "Slicing",
                    description:
                    $"{SliceUnits:N0} units of UI-thread work, 4 ms of each frame. The window keeps drawing while it runs.",
                    child: Demo.Specimen(
                        new AdwButton(label: "Build rows", onPressed: BuildRows),
                        new Watch(() => new AdwProgressBar(_progress.Value)),
                        new Watch(() => Demo.Caption(_slice.Value))
                    )
                ),
                Demo.Group(
                    title: "The Pieces",
                    description: null,
                    new AdwActionRow(
                        title: "Signal<T>",
                        subtitle: "Written from any thread; readers take a coherent snapshot"
                    ),
                    new AdwActionRow(
                        title: "Computed<T>",
                        subtitle: "Fan-in over every worker's counter, recomputed on demand"
                    ),
                    new AdwActionRow(
                        title: "Background",
                        subtitle: "Scoped workers whose failures are reported, not swallowed"
                    ),
                    new AdwActionRow(
                        title: "Deliver.WhenIdle",
                        subtitle: "Results land only while the frame still has room"
                    ),
                    new AdwActionRow(
                        title: "Slice",
                        subtitle: "One long job spread over frames, keyed so it can be replaced"
                    )
                ),
            },
        };
    }

    /// <summary>
    ///     Headless check for the one thing on this page that is not visual: concurrent writes to
    ///     independent signals stay exact, and the fan-in agrees with the parts. Called from the
    ///     gallery's <c>--self-test</c>. Returns null on success, or the failure.
    /// </summary>
    internal static string? SelfCheck()
    {
        const int threads = 4;
        const int writes = 20_000;

        var counters = new Signal<long>[threads];
        for (int i = 0; i < threads; i++) counters[i] = new Signal<long>(0);
        var total = Computed.From(() =>
            {
                long sum = 0L;
                foreach (var counter in counters) sum += counter.Value;
                return sum;
            }
        );

        var workers = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            var slot = counters[i];
            workers[i] = new Thread(() =>
                {
                    for (long n = 1; n <= writes; n++) slot.Value = n;
                }
            );
        }

        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) worker.Join();

        long expected = (long)threads * writes;
        if (total.Value != expected)
            return $"fan-in read {total.Value}, expected {expected}";
        foreach (var counter in counters)
        {
            if (counter.Peek() != writes)
                return $"a counter read {counter.Peek()}, expected {writes}";
        }

        total.Dispose();
        return null;
    }
}
