// These tests exercise threading directly: the bounded Wait/WaitAll calls with explicit
// timeouts ARE the assertions (a deadlock must fail fast, not hang), so awaiting instead would
// defeat the test. Cancellation is likewise irrelevant to a wait that is already time-bounded.

#pragma warning disable xUnit1031, xUnit1051
using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     The C#-first fine-grained reactive core (<see cref="Signal{T}" />/<see cref="Computed{T}" />/
///     <see cref="Effect" />): auto-tracking, dynamic dependencies, cleanup, batching, disposal, and
///     the
///     push-notify/pull-recompute properties — lazy (unobserved computeds don't recompute), leak-free
///     (they detach from sources), glitch-free, and minimal-recompute. The F# layer (Zigote.UI.FSharp)
///     and the Watch widget are thin wrappers over exactly these types.
/// </summary>
[Collection(
    "Reactive-serial"
)] // shares process-static state (GlobalVersion, OnError) with the stress tests
public class ReactiveTests
{
    [Fact]
    public void Signal_holds_updates_and_notifies_gated_by_equality()
    {
        var s = new Signal<int>(0);
        Assert.Equal(expected: 0, actual: s.Value);

        int fires = 0;
        using var _ = s.Observe(() => fires++);
        s.Value = 0; // equal → no notification
        Assert.Equal(expected: 0, actual: fires);
        s.Value = 5;
        Assert.Equal(expected: 5, actual: s.Value);
        Assert.Equal(expected: 1, actual: fires);
    }

    [Fact]
    public void Signal_Update_is_read_modify_write()
    {
        var s = new Signal<int>(10);
        s.Update(v => v + 5);
        Assert.Equal(expected: 15, actual: s.Value);
    }

    [Fact]
    public void Computed_auto_tracks_and_recomputes_when_observed()
    {
        var a = new Signal<int>(2);
        var b = new Signal<int>(3);
        int runs = 0;
        using var sum = Computed.From(() =>
            {
                runs++;
                return a.Value + b.Value;
            }
        );
        using var _ = sum.Observe(() => { }); // observe → live, recomputes eagerly on change

        Assert.Equal(expected: 5, actual: sum.Value);
        Assert.Equal(
            expected: 1,
            actual: runs
        ); // computed once (observing a current value does not recompute)
        a.Value = 10;
        Assert.Equal(expected: 13, actual: sum.Value);
        b.Value = 100;
        Assert.Equal(expected: 110, actual: sum.Value);
        Assert.Equal(expected: 3, actual: runs); // once per dependency change
    }

    [Fact]
    public void Computed_dependencies_are_dynamic()
    {
        var toggle = new Signal<bool>(true);
        var a = new Signal<int>(1);
        var b = new Signal<int>(2);
        using var chosen = Computed.From(() => toggle.Value ? a.Value : b.Value);

        Assert.Equal(expected: 1, actual: chosen.Value);
        int fires = 0;
        using var _ = chosen.Observe(() => fires++);
        b.Value = 99; // b isn't a dependency while toggle=true
        Assert.Equal(expected: 0, actual: fires);
        a.Value = 5;
        Assert.Equal(expected: 1, actual: fires);
        toggle.Value = false; // now b becomes the dependency
        Assert.Equal(expected: 99, actual: chosen.Value);
    }

    [Fact]
    public void Computed_chains_propagate()
    {
        var n = new Signal<int>(4);
        using var doubled = Computed.From(() => n.Value * 2);
        using var plusOne = Computed.From(() => doubled.Value + 1);
        Assert.Equal(expected: 9, actual: plusOne.Value);
        n.Value = 10;
        Assert.Equal(expected: 21, actual: plusOne.Value);
    }

    [Fact]
    public void Effect_runs_immediately_and_on_change_with_cleanup()
    {
        var s = new Signal<int>(0);
        var seen = new List<int>();
        var cleaned = new List<int>();

        var e = new Effect(() =>
            {
                int v = s.Value;
                seen.Add(v);
                return () => cleaned.Add(v);
            }
        );

        Assert.Equal(expected: new[] { 0 }, actual: seen);
        s.Value = 1;
        Assert.Equal(
            expected: new[] {
                0,
                1,
            },
            actual: seen
        );
        Assert.Equal(
            expected: new[] { 0 },
            actual: cleaned
        ); // cleanup for 0 ran before the re-run
        e.Dispose();
        Assert.Equal(
            expected: new[] {
                0,
                1,
            },
            actual: cleaned
        ); // final cleanup for 1
        s.Value = 2;
        Assert.Equal(
            expected: new[] {
                0,
                1,
            },
            actual: seen
        ); // disposed → no more runs
    }

    [Fact]
    public void Batch_coalesces_writes_into_one_pass()
    {
        var a = new Signal<int>(1);
        var b = new Signal<int>(1);
        int runs = 0;
        using var sum = Computed.From(() =>
            {
                runs++;
                return a.Value + b.Value;
            }
        );
        using var _ = sum.Observe(() => { }); // live, so it reacts eagerly
        Assert.Equal(expected: 1, actual: runs);

        Reactive.Batch(() =>
            {
                a.Value = 10;
                b.Value = 20;
            }
        );

        Assert.Equal(expected: 30, actual: sum.Value);
        Assert.Equal(expected: 2, actual: runs); // one recompute for the whole batch, not two
    }

    [Fact]
    public void Unbatched_writes_each_trigger_their_own_recompute()
    {
        // The counterpart to Batch_coalesces_writes: outside a batch, every write is its own implicit
        // transaction, so three sequential writes recompute the (observed) derived value three times.
        var a = new Signal<int>(0);
        var b = new Signal<int>(0);
        var c = new Signal<int>(0);
        int runs = 0;
        using var total = Computed.From(() =>
            {
                runs++;
                return a.Value + b.Value + c.Value;
            }
        );
        using var _ = total.Observe(() => { });
        Assert.Equal(expected: 1, actual: runs); // construction

        a.Value = 1;
        b.Value = 1;
        c.Value = 1;
        Assert.Equal(expected: 4, actual: runs); // three separate writes → three recomputes
    }

    [Fact]
    public void Unobserved_computed_is_lazy()
    {
        // An unobserved computed does not recompute when its sources change — only on read.
        var s = new Signal<int>(1);
        int runs = 0;
        using var c = Computed.From(() =>
            {
                runs++;
                return s.Value * 2;
            }
        );
        Assert.Equal(expected: 1, actual: runs); // eager first compute

        s.Value = 2;
        s.Value = 3;
        Assert.Equal(expected: 1, actual: runs); // still lazy — nobody read it

        Assert.Equal(
            expected: 6,
            actual: c.Value
        ); // read → recompute once, to the current value
        Assert.Equal(expected: 2, actual: runs);
    }

    [Fact]
    public void Unobserved_computed_recomputes_only_when_its_own_source_changed()
    {
        var a = new Signal<int>(1);
        var unrelated = new Signal<int>(0);
        int runs = 0;
        using var c = Computed.From(() =>
            {
                runs++;
                return a.Value * 2;
            }
        );
        Assert.Equal(expected: 1, actual: runs);

        unrelated.Value = 99; // global version moves, but it is not a source of c
        Assert.Equal(
            expected: 2,
            actual: c.Value
        ); // read → verifies via source version: a unchanged → no recompute
        Assert.Equal(expected: 1, actual: runs);

        a.Value = 5; // c's actual source changed
        Assert.Equal(expected: 10, actual: c.Value);
        Assert.Equal(expected: 2, actual: runs);
    }

    [Fact]
    public void An_observed_computed_detaches_from_sources_when_its_observer_goes_away()
    {
        // Leak-free / refcount: while observed a computed reacts; once its last observer leaves it
        // unsubscribes from its sources (stops recomputing) — the source no longer retains it.
        var s = new Signal<int>(0);
        int runs = 0;
        using var c = Computed.From(() =>
            {
                runs++;
                return s.Value;
            }
        );
        Assert.Equal(expected: 1, actual: runs);

        var sub = c.Observe(() => { }); // became watched — no recompute (value current)
        Assert.Equal(expected: 1, actual: runs);
        s.Value = 1; // watched → recomputes eagerly
        Assert.Equal(expected: 2, actual: runs);

        sub.Dispose(); // last observer gone → detach from s
        s.Value = 2; // no longer watched → does NOT recompute
        Assert.Equal(expected: 2, actual: runs);

        Assert.Equal(expected: 2, actual: c.Value); // lazy read picks up the current value
        Assert.Equal(expected: 3, actual: runs);
    }

    [Fact]
    public void Disposing_a_computed_detaches_it()
    {
        var s = new Signal<int>(0);
        int runs = 0;
        var c = Computed.From(() =>
            {
                runs++;
                return s.Value * 2;
            }
        );
        using var _ = c.Observe(() => { });
        Assert.Equal(expected: 1, actual: runs);
        s.Value = 1;
        Assert.Equal(expected: 2, actual: runs);
        c.Dispose();
        s.Value = 2;
        Assert.Equal(expected: 2, actual: runs); // no longer reacting
    }

    [Fact]
    public void A_write_reaches_a_shared_downstream_node_once_glitch_free()
    {
        // Diamond: a → {b, c} → effect(reads b and c). A single `a` write must run the effect ONCE
        // (not once per branch), with both inputs already settled.
        var a = new Signal<int>(0);
        using var b = Computed.From(() => a.Value + 1);
        using var c = Computed.From(() => a.Value + 2);
        int effectRuns = 0;
        int lastSum = -1;
        using var e = new Effect(() =>
            {
                effectRuns++;
                lastSum = b.Value + c.Value;
            }
        );

        Assert.Equal(expected: 1, actual: effectRuns); // initial
        a.Value = 10;
        Assert.Equal(expected: 2, actual: effectRuns); // one run for the whole fan-out, not two
        Assert.Equal(expected: 23, actual: lastSum); // b=11, c=12 — both settled
    }

    [Fact]
    public void An_unchanged_intermediate_does_not_wake_its_observers()
    {
        // Minimal recompute: x → isEven → effect. Toggling x between two even values keeps isEven true,
        // so the effect must NOT run — the equality gate stops the propagation at the intermediate.
        var x = new Signal<int>(0);
        using var isEven = Computed.From(() => x.Value % 2 == 0);
        int effectRuns = 0;
        using var e = new Effect(() =>
            {
                _ = isEven.Value;
                effectRuns++;
            }
        );

        Assert.Equal(expected: 1, actual: effectRuns);
        x.Value = 2; // even → even: isEven unchanged → effect does not run
        Assert.Equal(expected: 1, actual: effectRuns);
        x.Value = 3; // even → odd: isEven flips → effect runs
        Assert.Equal(expected: 2, actual: effectRuns);
    }

    [Fact]
    public void Custom_equality_gates_change_propagation()
    {
        // A comparer that treats values within a tolerance as equal → sub-threshold changes don't fire.
        var s = new Signal<double>(0.0);
        using var rounded = Computed.From(
            compute: () => s.Value,
            equals: new ToleranceComparer(0.5)
        );
        int fires = 0;
        using var _ = rounded.Observe(() => fires++);

        s.Value = 0.3; // within tolerance of the last accepted value → no change
        Assert.Equal(expected: 0, actual: fires);
        s.Value = 1.0; // beyond tolerance → change
        Assert.Equal(expected: 1, actual: fires);
    }

    [Fact]
    public void Peek_reads_without_creating_a_dependency()
    {
        var a = new Signal<int>(1);
        var b = new Signal<int>(10);
        int runs = 0;
        using var c = Computed.From(() =>
            {
                runs++;
                return a.Value + b.Peek(); // depends on a, NOT on b
            }
        );
        using var _ = c.Observe(() => { }); // live
        Assert.Equal(expected: 11, actual: c.Value);

        b.Value = 99; // not a dependency → no recompute
        Assert.Equal(expected: 1, actual: runs);
        a.Value = 5; // dependency → recompute; picks up b's current (peeked) value
        Assert.Equal(expected: 2, actual: runs);
        Assert.Equal(expected: 104, actual: c.Value);
    }

    [Fact]
    public void Untracked_reads_do_not_subscribe()
    {
        var a = new Signal<int>(1);
        var b = new Signal<int>(10);
        int runs = 0;
        using var c = Computed.From(() =>
            {
                runs++;
                return a.Value + Reactive.Untracked(() => b.Value);
            }
        );
        using var _ = c.Observe(() => { }); // live

        b.Value = 99; // b read untracked → not a dependency → no recompute
        Assert.Equal(expected: 1, actual: runs);
        a.Value = 5; // a is a dependency → recompute
        Assert.Equal(expected: 2, actual: runs);
    }

    [Fact]
    public void A_dependency_cycle_fails_loudly_instead_of_hanging()
    {
        // Two effects that write each other's inputs form a genuine runaway; the drain guard must throw
        // rather than hang. (A single effect that writes a signal it reads settles — the mid-run write
        // can't reschedule the effect that is already running — so it is not a runaway.)
        var a = new Signal<int>(0);
        var b = new Signal<int>(0);
        using var e1 = new Effect(() => b.Value = a.Value + 1); // a → b
        using var e2 = new Effect(() => a.Value = b.Value + 1); // b → a
        Assert.Throws<InvalidOperationException>(() => a.Value = 100);
    }

    [Fact]
    public void Signal_is_safe_to_set_off_thread()
    {
        // The graph lock lets a background thread set a signal without racing a reader.
        var s = new Signal<int>(0);
        using var doubled = Computed.From(() => s.Value * 2);

        var t = Task.Run(() =>
            {
                for (int i = 1; i <= 200; i++) s.Value = i;
            }
        );
        t.Wait();

        Assert.Equal(expected: 200, actual: s.Value);
        Assert.Equal(expected: 400, actual: doubled.Value);
    }

    [Fact]
    public void A_watched_computed_read_in_a_Changed_handler_is_not_poisoned()
    {
        // Regression (CRITICAL): reading a watched computed inside a Changed/Subscribe handler used to
        // stamp the computed's validated-version while it was still stale, permanently losing the update.
        var a = new Signal<int>(1);
        using var doubled = Computed.From(() => a.Value * 2);
        int seen = -1;
        using var eff = new Effect(() => seen = doubled.Value); // makes `doubled` watched
        using var sub = a.Subscribe(_ =>
            {
                _ = doubled.Value;
            }
        ); // reads it in the change window

        a.Value = 5;
        Assert.Equal(expected: 10, actual: seen);
        Assert.Equal(expected: 10, actual: doubled.Value);
    }

    [Fact]
    public void A_reentrant_Changed_handler_that_subscribes_an_observer_does_not_crash()
    {
        // Regression (HIGH): Changed fired before the observer cascade, so a handler that created an
        // effect on the same signal mutated _observers mid-enumeration → InvalidOperationException.
        var s = new Signal<int>(0);
        Effect? created = null;
        s.Changed += _ => created ??= new Effect(() => _ = s.Value);

        s.Value = 1; // must not throw
        s.Value = 2;
        created?.Dispose();
        Assert.Equal(expected: 2, actual: s.Value);
    }

    [Fact]
    public void A_self_writing_effect_converges()
    {
        // Regression (HIGH): a subscribed effect writing a signal it reads used to stall after one step.
        var trigger = new Signal<int>(0);
        var s = new Signal<int>(0);
        using var eff = new Effect(() =>
            {
                _ = trigger.Value;
                int v = s.Value;
                if (v < 3) s.Value = v + 1;
            }
        );

        Assert.Equal(expected: 1, actual: s.Value); // initial (unsubscribed) run stepped once
        trigger.Value = 1; // re-trigger → must settle, not stall
        Assert.Equal(expected: 3, actual: s.Value);
    }

    [Fact]
    public void A_divergent_self_writing_effect_trips_the_guard()
    {
        var s = new Signal<int>(0);
        Assert.Throws<InvalidOperationException>(() =>
            {
                using var eff = new Effect(() => s.Value = s.Value + 1); // never converges
                s.Value = 100; // re-trigger the (now subscribed) self-writer → diverges → guard
            }
        );
    }

    [Fact]
    public void A_throwing_effect_is_isolated_and_does_not_drop_siblings()
    {
        // Regression (MED): a throwing effect body used to abort the drain, dropping the effects queued
        // after it. With an OnError handler installed, every effect still runs and the error is reported.
        var s = new Signal<int>(0);
        int ranBad = 0;
        int ranGood = 0;
        using var bad = new Effect(() =>
            {
                ranBad++;
                if (s.Value == 1) throw new InvalidOperationException("boom");
            }
        );
        using var good = new Effect(() =>
            {
                _ = s.Value;
                ranGood++;
            }
        );

        int errors = 0;
        Reactive.OnError = _ => errors++;
        try
        {
            s.Value = 1;
        }
        finally
        {
            Reactive.OnError = null;
        }

        Assert.Equal(expected: 2, actual: ranBad); // bad ran (and threw)
        Assert.Equal(expected: 2, actual: ranGood); // good STILL ran despite bad throwing
        Assert.Equal(expected: 1, actual: errors);
    }

    [Fact]
    public void A_computed_that_disposes_itself_during_compute_does_not_resurrect()
    {
        // Regression (HIGH): self-dispose during Execute left Update re-subscribing the dead node.
        var s = new Signal<int>(0);
        int runs = 0;
        Computed<int>? c = null;
        c = Computed.From(() =>
            {
                runs++;
                int v = s.Value;
                // ReSharper disable once AccessToModifiedClosure
                c?.Dispose(); // c is null during the ctor's eager compute; assigned by the time it reacts
                return v;
            }
        );
        using var _ = c.Observe(() => { });

        s.Value = 1; // c recomputes once, then disposes itself mid-compute
        int afterDispose = runs;
        s.Value = 2; // c is now dead → must not react (no resurrection) and must not crash
        Assert.Equal(expected: afterDispose, actual: runs);
    }

    private sealed class ToleranceComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) <= tolerance;

        public int GetHashCode(double v) => 0;
    }
}

#pragma warning restore xUnit1031, xUnit1051
