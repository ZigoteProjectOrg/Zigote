// These tests exercise threading directly: the bounded Wait/WaitAll calls with explicit
// timeouts ARE the assertions (a deadlock must fail fast, not hang), so awaiting instead would
// defeat the test. Cancellation is likewise irrelevant to a wait that is already time-bounded.

#pragma warning disable xUnit1031, xUnit1051
using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     The thread-related contracts of the reactive core: the graph-state invariant that lets
///     <c>Reactive.EvalContext</c> be a plain static (it is lock-owned, not thread-owned — a reaction
///     must never capture a source another thread's reaction read), the
///     <see cref="EffectAffinity.Deferred" /> escape hatch that keeps a background write from running UI
///     bodies on the writer's thread under the graph lock, and the "user code is not invoked under the
///     lock" property of <see cref="Signal{T}.Subscribe" /> — the shapes that turn into a deadlock or a
///     torn dependency list when they regress.
/// </summary>
[Collection("Reactive-serial")]
public class ReactiveThreadingTests
{
    private const int
        TimeoutMs = 30_000; // generous: a failure here means a hang, not a slow machine

    [Fact]
    public void Concurrent_reactions_never_capture_another_threads_sources()
    {
        // 4 threads, each with its OWN source and its own computed+effect over it. If the eval context
        // leaked across threads, a reaction would subscribe to a foreign signal and start re-running on
        // its writes — detected here as a run whose value doesn't match its own source.
        const int threads = 4;
        const int writes = 2_000;
        var bleed = 0;

        Parallel.For(
            0,
            threads,
            t =>
            {
                var mine = new Signal<int>(0);
                var traffic =
                    new Signal<int>(
                        0
                    ); // read from outside any reaction, concurrently with everyone
                using var derived = Computed.From(() => mine.Value * 2);
                var lastSeen = -1;
                var runs = 0;
                using var effect = new Effect(() =>
                    {
                        lastSeen = derived.Value;
                        runs++;
                    }
                );

                for (var i = 1; i <= writes; i++)
                {
                    mine.Value = i;
                    if (lastSeen != i * 2) Interlocked.Increment(ref bleed);

                    // A tracked read while other threads' reactions are running: if the eval context were
                    // shared across threads, this read would be attributed to one of THEIR reactions.
                    if (traffic.Value != 0) Interlocked.Increment(ref bleed);
                }

                // Dependencies here are thread-private, so the run count is exact: one per own write, plus
                // construction. Anything more means this reaction subscribed to a source it never read.
                if (runs != writes + 1) Interlocked.Increment(ref bleed);
            }
        );

        Assert.Equal(0, bleed);
    }

    [Fact]
    public void A_deferred_effect_runs_only_on_the_hosts_drain()
    {
        var s = new Signal<int>(0);
        var runs = 0;
        var seen = -1;
        using var e = new Effect(
            () =>
            {
                seen = s.Value;
                runs++;
            },
            EffectAffinity.Deferred
        );

        Assert.Equal(1, runs); // construction runs on the creating thread, like any effect
        Assert.Equal(0, seen);

        s.Value = 1;
        Assert.Equal(1, runs); // the write only marked it
        Assert.Equal(0, seen);

        Reactive.DrainDeferred();
        Assert.Equal(2, runs);
        Assert.Equal(1, seen);

        Reactive.DrainDeferred(); // nothing pending → no re-run
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Repeated_writes_coalesce_into_one_deferred_run()
    {
        var s = new Signal<int>(0);
        var runs = 0;
        var seen = -1;
        using var e = new Effect(
            () =>
            {
                seen = s.Value;
                runs++;
            },
            EffectAffinity.Deferred
        );

        for (var i = 1; i <= 100; i++) s.Value = i;
        Assert.Equal(1, runs);

        Reactive.DrainDeferred();
        Assert.Equal(2, runs); // one run for a hundred writes
        Assert.Equal(100, seen); // and it sees the latest value
    }

    [Fact]
    public void A_deferred_effect_disposed_while_queued_never_runs()
    {
        var s = new Signal<int>(0);
        var runs = 0;
        var e = new Effect(
            () =>
            {
                _ = s.Value;
                runs++;
            },
            EffectAffinity.Deferred
        );

        s.Value = 1; // queued
        e.Dispose();
        Reactive.DrainDeferred();
        Assert.Equal(1, runs); // only the constructor run
    }

    [Fact]
    public void A_background_write_does_not_run_a_deferred_body_on_the_writer_thread()
    {
        // The affinity contract: whatever thread writes, the body runs on the one that drains. This is
        // the audio/network-thread inversion the option exists to prevent.
        var s = new Signal<int>(0);
        var ranOn = 0;
        using var e = new Effect(
            () =>
            {
                _ = s.Value;
                ranOn = Environment.CurrentManagedThreadId;
            },
            EffectAffinity.Deferred
        );

        var writerThread = 0;
        var writer = Task.Run(() =>
            {
                writerThread = Environment.CurrentManagedThreadId;
                s.Value = 7;
            }
        );
        Assert.True(writer.Wait(TimeoutMs));

        Assert.NotEqual(writerThread, ranOn); // still the creating thread's run
        Reactive.DrainDeferred();
        Assert.Equal(Environment.CurrentManagedThreadId, ranOn);
    }

    [Fact]
    public void Subscribe_does_not_invoke_its_listener_under_the_graph_lock()
    {
        // Deadlock regression: the initial invoke used to run while holding the gate, so a listener that
        // waited on any other thread's signal write would hang the whole graph forever.
        var s = new Signal<int>(1);
        var other = new Signal<int>(0);
        using var ready = new ManualResetEventSlim();
        using var written = new ManualResetEventSlim();

        var writer = Task.Run(() =>
            {
                ready.Wait(TimeoutMs);
                other.Value = 42; // needs the gate — must not be blocked by the listener below
                written.Set();
            }
        );

        var observed = 0;
        using var sub = s.Subscribe(v =>
            {
                observed = v;
                ready.Set();
                Assert.True(
                    written.Wait(TimeoutMs),
                    "the initial Subscribe callback still holds the graph lock"
                );
            }
        );

        Assert.True(writer.Wait(TimeoutMs));
        Assert.Equal(1, observed);
        Assert.Equal(42, other.Peek());
    }

    [Fact]
    public void A_blocked_graph_lock_fails_loudly_instead_of_hanging_forever()
    {
        // The gate is held across user code, so a body that blocks while another thread waits for it is a
        // true deadlock. Bounded acquisition turns "the app is frozen" into an exception naming both
        // threads. (EffectAffinity.Deferred is the fix for the sanctioned version of this shape; this is
        // the backstop for the rest.)
        var previous = Reactive.LockTimeoutMs;
        Reactive.LockTimeoutMs = 300;
        var s = new Signal<int>(0);
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var hog = Task.Run(() => Reactive.Sync(() =>
                    {
                        holding.Set();
                        release.Wait(TimeoutMs);
                    }
                )
            );
            Assert.True(holding.Wait(TimeoutMs));

            Assert.Throws<ReactiveDeadlockException>(() => s.Value = 1);

            release.Set();
            Assert.True(hog.Wait(TimeoutMs));
        }
        finally
        {
            release.Set();
            Reactive.LockTimeoutMs = previous;
        }

        s.Value = 2; // gate free again, business as usual
        Assert.Equal(2, s.Peek());
    }

    [Fact]
    public void A_deferred_effect_can_be_drained_while_other_threads_write()
    {
        // Stress: writers on 4 threads, the "host" draining in a loop. Must not deadlock, lose the final
        // value, or leave the effect queued forever.
        var s = new Signal<int>(0);
        var seen = -1;
        using var e = new Effect(() => seen = s.Value, EffectAffinity.Deferred);

        var stop = false;
        var host = Task.Run(() =>
            {
                while (!Volatile.Read(ref stop)) Reactive.DrainDeferred();
            }
        );

        Parallel.For(
            0,
            4,
            _ =>
            {
                for (var i = 0; i < 5_000; i++) s.Value = i;
            }
        );

        Volatile.Write(ref stop, true);
        Assert.True(host.Wait(TimeoutMs));

        Reactive.DrainDeferred();
        Assert.Equal(s.Peek(), seen);
    }
}

#pragma warning restore xUnit1031, xUnit1051