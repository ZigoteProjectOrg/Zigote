// These tests exercise threading directly: the bounded Wait/WaitAll calls with explicit
// timeouts ARE the assertions (a deadlock must fail fast, not hang), so awaiting instead would
// defeat the test. Cancellation is likewise irrelevant to a wait that is already time-bounded.

#pragma warning disable xUnit1031, xUnit1051
using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     Hard concurrency stress tests for the reactive core. The whole graph is guarded by one
///     re-entrant lock (<c>Reactive.Gate</c>), so these assert the guarantees that lock must provide:
///     no lost updates, no torn reads, atomic batches, and no crashes/deadlocks when many threads read,
///     write, subscribe, and build/dispose derived nodes concurrently.
/// </summary>
[Collection(
    "Reactive-serial"
)] // don't run these in parallel with each other (they saturate the CPU)
public class ReactiveConcurrencyTests
{
    private const int Threads = 8;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    [Fact]
    public void Concurrent_Update_loses_no_increments()
    {
        // Update is a read-modify-write under the lock — N×M increments must all land.
        const int perThread = 25_000;
        var s = new Signal<int>(0);

        RunThreads(
            Threads,
            () =>
            {
                for (var i = 0; i < perThread; i++) s.Update(v => v + 1);
            }
        );

        Assert.Equal(Threads * perThread, s.Value);
    }

    [Fact]
    public void Concurrent_Set_leaves_a_value_some_thread_actually_wrote()
    {
        // Last-writer-wins: the final value must be one that was written (no torn/blended result). Each
        // write encodes (thread, i) so the final value decodes back to a valid pair — no side table.
        const int perThread = 50_000;
        var s = new Signal<long>(-1);

        RunThreads(
            Threads,
            t =>
            {
                for (var i = 0; i < perThread; i++) s.Value = ((long)t << 40) | (uint)i;
            }
        );

        var final = s.Value;
        var thread = (int)(final >> 40);
        var iteration = final & ((1L << 40) - 1);
        Assert.InRange(thread, 0, Threads - 1);
        Assert.InRange(iteration, 0, perThread - 1);
    }

    [Fact]
    public void Concurrent_readers_never_see_a_torn_value()
    {
        // A 16-byte struct write isn't atomic without the lock; a reader must never see A != B.
        var s = new Signal<Pair>(new Pair(0, 0));
        var stop = false;

        var readers = StartReaders(
            Threads,
            () =>
            {
                var p = s.Value;
                Assert.Equal(p.A, p.B);
            },
            () => Volatile.Read(ref stop)
        );

        for (long i = 1; i <= 400_000; i++) s.Value = new Pair(i, i);
        Volatile.Write(ref stop, true);
        AwaitAll(readers);
    }

    [Fact]
    public void Batch_transfers_preserve_the_sum_invariant()
    {
        // Two signals with a conserved total. Each transfer is a Batch (atomic across both writes); a
        // concurrent reader of the derived sum must never observe a mid-transfer total, and the final
        // total must be conserved (no lost updates across the pair).
        const int perThread = 15_000;
        const long total = 1_000_000;
        var a = new Signal<long>(total);
        var b = new Signal<long>(0);
        using var sum = Computed.From(() => a.Value + b.Value);

        var stop = false;
        var readers = StartReaders(
            2,
            () => Assert.Equal(total, sum.Value),
            () => Volatile.Read(ref stop)
        );

        RunThreads(
            Threads,
            t =>
            {
                var rng = new Random(t + 1);
                for (var i = 0; i < perThread; i++)
                {
                    var x = rng.Next(1, 1000);
                    Reactive.Batch(() =>
                        {
                            a.Value -= x;
                            b.Value += x;
                        }
                    );
                    Reactive.Batch(() =>
                        {
                            a.Value += x;
                            b.Value -= x;
                        }
                    );
                }
            }
        );

        Volatile.Write(ref stop, true);
        AwaitAll(readers);
        Assert.Equal(total, a.Value + b.Value);
    }

    [Fact]
    public void A_derived_computed_ends_consistent_with_the_final_source()
    {
        // An observed computed reacts to every write; after the storm its value matches the final source.
        const int perThread = 20_000;
        var s = new Signal<int>(0);
        using var doubled = Computed.From(() => s.Value * 2);
        using var _ = ((ISignal)doubled).Observe(() => { }); // keep it live

        RunThreads(
            Threads,
            t =>
            {
                for (var i = 0; i < perThread; i++) s.Value = (t << 20) | i;
            }
        );

        Assert.Equal(s.Value * 2, doubled.Value);
    }

    [Fact]
    public void A_diamond_stays_consistent_under_concurrent_writes()
    {
        // a → {b, c} → effect(reads b + c). After the storm the last observed sum matches the final a.
        var a = new Signal<int>(0);
        using var b = Computed.From(() => a.Value + 1);
        using var c = Computed.From(() => a.Value + 2);
        long lastSum = -1;
        using var e = new Effect(() => Volatile.Write(ref lastSum, b.Value + c.Value));

        RunThreads(
            Threads,
            t =>
            {
                for (var i = 0; i < 15_000; i++) a.Value = (t << 20) | i;
            }
        );

        Assert.Equal((long)(a.Value + 1) + (a.Value + 2), Volatile.Read(ref lastSum));
    }

    [Fact]
    public void Concurrent_subscribe_and_dispose_during_writes_does_not_crash()
    {
        // A bounded writer (not an infinite spin — under one lock that would starve the workers) runs
        // alongside threads that churn Subscribe/Dispose. Just assert everything completes without crash.
        var s = new Signal<int>(0);
        var tasks = new List<Task> {
            Task.Factory.StartNew(
                () =>
                {
                    for (var i = 0; i < 200_000; i++) s.Value = i;
                },
                TaskCreationOptions.LongRunning
            ),
        };

        for (var t = 0; t < Threads; t++)
            tasks.Add(
                Task.Factory.StartNew(
                    () =>
                    {
                        for (var i = 0; i < 20_000; i++)
                        {
                            var sub = s.Subscribe(_ => { });
                            sub.Dispose();
                        }
                    },
                    TaskCreationOptions.LongRunning
                )
            );

        Assert.True(Task.WaitAll(tasks.ToArray(), Budget), "did not finish (deadlock?)");
    }

    [Fact]
    public void
        Concurrent_computed_and_effect_lifecycle_during_writes_does_not_crash_or_deadlock()
    {
        // Threads churn observed computeds + effects off the same source while a bounded writer hammers
        // it — stresses concurrent Connect/Disconnect + cascade. Assert it completes without crash.
        var s = new Signal<int>(0);
        var tasks = new List<Task> {
            Task.Factory.StartNew(
                () =>
                {
                    for (var i = 0; i < 200_000; i++) s.Value = i;
                },
                TaskCreationOptions.LongRunning
            ),
        };

        for (var t = 0; t < Threads; t++)
            tasks.Add(
                Task.Factory.StartNew(
                    () =>
                    {
                        for (var i = 0; i < 5_000; i++)
                        {
                            var c = Computed.From(() => s.Value + 1);
                            var obs = ((ISignal)c).Observe(() => { });
                            var e = new Effect(() => _ = s.Value);
                            _ = c.Value;
                            obs.Dispose();
                            e.Dispose();
                            c.Dispose();
                        }
                    },
                    TaskCreationOptions.LongRunning
                )
            );

        Assert.True(Task.WaitAll(tasks.ToArray(), Budget), "did not finish (deadlock?)");
    }

    [Fact]
    public void A_signal_can_be_set_from_any_thread_while_read_from_another()
    {
        // Mixed read/write storm — completes without deadlock and ends on a written value.
        var s = new Signal<long>(0);
        var stop = false;
        var seen = new System.Collections.Concurrent.ConcurrentBag<long>();

        var readers = StartReaders(
            Threads / 2,
            () => seen.Add(s.Value),
            () => Volatile.Read(ref stop)
        );

        RunThreads(
            Threads / 2,
            t =>
            {
                for (var i = 0; i < 30_000; i++) s.Value = ((long)(t + 1) << 40) | (uint)i;
            }
        );

        Volatile.Write(ref stop, true);
        AwaitAll(readers);
        Assert.True(seen.Count > 0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void RunThreads(int n, Action body)
    {
        RunThreads(n, _ => body());
    }

    private static void RunThreads(int n, Action<int> body)
    {
        var tasks = new Task[n];
        for (var t = 0; t < n; t++)
        {
            var id = t;
            tasks[t] = Task.Factory.StartNew(() => body(id), TaskCreationOptions.LongRunning);
        }

        Assert.True(
            Task.WaitAll(tasks, Budget),
            "threads did not finish within the budget (possible deadlock)"
        );
    }

    private static List<Task> StartReaders(int n, Action read, Func<bool> stopped)
    {
        var tasks = new List<Task>(n);
        for (var r = 0; r < n; r++)
            tasks.Add(
                Task.Factory.StartNew(
                    () =>
                    {
                        while (!stopped()) read();
                    },
                    TaskCreationOptions.LongRunning
                )
            );
        return tasks;
    }

    private static void AwaitAll(List<Task> tasks)
    {
        Assert.True(
            Task.WaitAll(tasks.ToArray(), Budget),
            "reader tasks did not finish within the budget"
        );
    }

    public readonly record struct Pair(long A, long B);
}

#pragma warning restore xUnit1031, xUnit1051