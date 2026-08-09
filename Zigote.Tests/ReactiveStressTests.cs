// Threading tests: the bounded Wait/WaitAll calls with explicit timeouts ARE the assertions (a
// deadlock must fail fast, not hang), so awaiting instead would defeat the test.

#pragma warning disable xUnit1031, xUnit1051
using System.Collections.Concurrent;
using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     The saturation case that <see cref="ReactiveConcurrencyTests" /> does not cover: every operation
///     kind running <b>at the same time</b> on one shared graph. Those tests isolate one axis each
///     (writes, or batches, or lifecycle churn); a re-entrancy or lock-ordering bug lives in the
///     interaction, where a drain is running an effect that disposes a computed that a third thread is
///     mid-subscribe on. This turns every axis on at once and asserts the invariants still hold.
/// </summary>
[Collection("Reactive-serial")] // process-static graph state (GlobalVersion, OnError, the drain queues)
public class ReactiveStressTests
{
    private const long Total = 1_000_000; // conserved across the transfer pair
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    /// <summary>Copies of the whole role set — scaled so the box is genuinely oversubscribed.</summary>
    private static readonly int Multiplier = Math.Max(2, Environment.ProcessorCount / 4);

    [Fact]
    public void Chaos_every_operation_kind_at_once_holds_every_invariant()
    {
        var left = new Signal<long>(Total);
        var right = new Signal<long>(0);
        using var conserved = Computed.From(() => left.Value + right.Value);
        using var conservedLive = ((ISignal)conserved).Observe(() => { });

        var churn = new Signal<int>(0);
        var poison = new Signal<int>(0);
        var trigger = new Trigger();
        using var triggered = Computed.From(() =>
            {
                trigger.Depend();
                return churn.Value;
            }
        );
        using var triggeredLive = ((ISignal)triggered).Observe(() => { });

        // Deferred: the body runs on whichever thread calls DrainDeferred, never on a writer's.
        var deferredSink = -1;
        using var deferred = new Effect(
            () => Volatile.Write(ref deferredSink, churn.Value),
            EffectAffinity.Deferred
        );

        // A reaction that always throws. With OnError set the drain must isolate it: siblings still run
        // and the writer thread that triggered it is not taken down.
        using var thrower = new Effect(() =>
            {
                if (poison.Value > 0) throw new InvalidOperationException("poison");
            }
        );

        var errors = new ConcurrentBag<Exception>();
        var previousHandler = Reactive.OnError;
        Reactive.OnError = errors.Add;

        var stopDrain = false;
        var drainHost = Task.Factory.StartNew(
            () =>
            {
                while (!Volatile.Read(ref stopDrain)) Reactive.DrainDeferred();
            },
            TaskCreationOptions.LongRunning
        );

        try
        {
            RunRoles(
                // Batched transfer — atomic across both writes, so the total is conserved.
                t =>
                {
                    var rng = new Random(t + 1);
                    for (var i = 0; i < 25_000; i++)
                    {
                        var x = rng.Next(1, 1000);
                        Reactive.Batch(() =>
                            {
                                left.Value -= x;
                                right.Value += x;
                            }
                        );
                        Reactive.Batch(() =>
                            {
                                left.Value += x;
                                right.Value -= x;
                            }
                        );
                    }
                },
                // Reader of the observed computed — must never see a mid-transfer total.
                _ =>
                {
                    for (var i = 0; i < 100_000; i++) Assert.Equal(Total, conserved.Value);
                },
                // Plain writes, driving both the deferred effect and the triggered computed.
                t =>
                {
                    for (var i = 0; i < 100_000; i++) churn.Value = (t << 20) | i;
                },
                // Subscribe/dispose churn against a source being written concurrently.
                _ =>
                {
                    for (var i = 0; i < 25_000; i++) churn.Subscribe(_ => { }).Dispose();
                },
                // Derived-node lifecycle churn: create, observe, read, tear down, all under write pressure.
                // (named rather than `_`, so the `_ =` discards below are discards and not assignments
                // to the role's own parameter)
                _unusedId =>
                {
                    for (var i = 0; i < 12_000; i++)
                    {
                        var c = Computed.From(() => churn.Value + 1);
                        var obs = ((ISignal)c).Observe(() => { });
                        var e = new Effect(() => _ = churn.Value);
                        _ = c.Value;
                        obs.Dispose();
                        e.Dispose();
                        c.Dispose();
                    }
                },
                // Untracked reads and peeks — the paths that bypass dependency registration.
                _unusedId =>
                {
                    for (var i = 0; i < 100_000; i++)
                    {
                        _ = churn.Peek();
                        _ = Reactive.Untracked(() => left.Value);
                    }
                },
                // Valueless source: every fire invalidates the triggered computed.
                _ =>
                {
                    for (var i = 0; i < 50_000; i++) trigger.Fire();
                },
                // The throwing reaction's driver — every write raises through the drain.
                _ =>
                {
                    for (var i = 1; i <= 5_000; i++) poison.Value = i;
                },
                // Multi-node consistent snapshot while everything above is running.
                _ =>
                {
                    for (var i = 0; i < 50_000; i++)
                        Reactive.Sync(() => Assert.Equal(Total, left.Value + right.Value));
                }
            );
        }
        finally
        {
            Volatile.Write(ref stopDrain, true);
            Assert.True(drainHost.Wait(Budget), "the drain host did not stop (deadlock?)");
            Reactive.OnError = previousHandler;
        }

        // Conserved across every batched transfer, and the observed computed agrees with its sources.
        Assert.Equal(Total, left.Value + right.Value);
        Assert.Equal(Total, conserved.Value);

        // The poison effect fired and was isolated every time — nothing else was dropped, and no
        // writer thread died carrying the exception (RunRoles would have rethrown it).
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.IsType<InvalidOperationException>(e));

        // The queue drains to empty and the deferred body ends up agreeing with the final source.
        Reactive.DrainDeferred();
        Assert.Equal(0, Reactive.PendingDeferred);
        Assert.Equal(churn.Peek(), Volatile.Read(ref deferredSink));

        // Sanity: the graph actually did work rather than short-circuiting the whole storm.
        Assert.True(Reactive.Runs > 0);
        GC.KeepAlive(conservedLive);
        GC.KeepAlive(triggeredLive);
        GC.KeepAlive(deferred);
        GC.KeepAlive(thrower);
    }

    [Fact]
    public void Sync_gives_a_consistent_multi_node_snapshot_under_concurrent_writes()
    {
        // Two computeds off one root: any single version satisfies b*3 == c*2. Reading them in two
        // separate tracked reads can straddle a concurrent write; reading both inside one Sync holds
        // the gate across the pair, so the snapshot is from one version. That is what Sync is FOR, and
        // nothing else in the suite pins it.
        var root = new Signal<int>(0);
        using var b = Computed.From(() => root.Value * 2);
        using var c = Computed.From(() => root.Value * 3);
        using var bLive = ((ISignal)b).Observe(() => { });
        using var cLive = ((ISignal)c).Observe(() => { });

        var stop = false;
        var readers = new List<Task>();
        for (var r = 0; r < Math.Max(4, Environment.ProcessorCount / 2); r++)
            readers.Add(
                Task.Factory.StartNew(
                    () =>
                    {
                        while (!Volatile.Read(ref stop))
                            Reactive.Sync(() =>
                                {
                                    long x = b.Value;
                                    long y = c.Value;
                                    Assert.Equal(x * 3, y * 2);
                                }
                            );
                    },
                    TaskCreationOptions.LongRunning
                )
            );

        var writers = new List<Task>();
        for (var w = 0; w < Math.Max(4, Environment.ProcessorCount / 2); w++)
            writers.Add(
                Task.Factory.StartNew(
                    () =>
                    {
                        for (var i = 1; i <= 100_000; i++) root.Value = i;
                    },
                    TaskCreationOptions.LongRunning
                )
            );

        Assert.True(Task.WaitAll(writers.ToArray(), Budget), "writers did not finish (deadlock?)");
        Volatile.Write(ref stop, true);
        Assert.True(Task.WaitAll(readers.ToArray(), Budget), "readers did not finish (deadlock?)");

        Assert.Equal(root.Value * 2, b.Value);
        Assert.Equal(root.Value * 3, c.Value);
        GC.KeepAlive(bLive);
        GC.KeepAlive(cLive);
    }

    /// <summary>
    ///     Start <see cref="Multiplier" /> copies of every role at once and wait for all of them. Any
    ///     assertion that failed on a worker surfaces here as the task's exception.
    /// </summary>
    private static void RunRoles(params Action<int>[] roles)
    {
        var tasks = new List<Task>(roles.Length * Multiplier);
        for (var copy = 0; copy < Multiplier; copy++)
        for (var r = 0; r < roles.Length; r++)
        {
            var role = roles[r];
            var id = (copy * roles.Length) + r;
            tasks.Add(Task.Factory.StartNew(() => role(id), TaskCreationOptions.LongRunning));
        }

        Assert.True(
            Task.WaitAll(tasks.ToArray(), Budget),
            $"{tasks.Count} workers did not finish within {Budget.TotalSeconds}s (possible deadlock)"
        );
        Task.WaitAll(tasks.ToArray()); // completed already — this rethrows any worker's failure
    }
}

#pragma warning restore xUnit1031, xUnit1051
