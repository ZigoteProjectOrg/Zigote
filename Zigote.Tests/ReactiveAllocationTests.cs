using Xunit;
using Zigote.Core.State;

namespace Zigote.Tests;

/// <summary>
///     Steady-state allocation gates for the reactive core. Each test warms up a repeated operation on a
///     STABLE graph (same deps, same observers — no construction/subscribe/dep-change churn) and asserts
///     it allocates exactly zero managed bytes. This is the ground truth for "is Signal/Computed
///     zero-allocation on the hot path" (e.g. a signal driving a per-frame UI update). Construction,
///     first run, subscribe/dispose, and Batch/Observe closure creation are cold paths and may allocate.
///     <para>Delegates passed to the measured loop are created ONCE (before the loop) so the measurement
///     reflects the core, not caller-side closure creation.</para>
/// </summary>
[Collection("Reactive-serial")]
public class ReactiveAllocationTests
{
    private int _sink;

    [Fact]
    public void Signal_read_is_zero_alloc()
    {
        var s = new Signal<int>(5);
        AllocGuard.AssertZeroAlloc(() => _sink += s.Value);
    }

    [Fact]
    public void Signal_peek_is_zero_alloc()
    {
        var s = new Signal<int>(5);
        AllocGuard.AssertZeroAlloc(() => _sink += s.Peek());
    }

    [Fact]
    public void Signal_write_with_no_observers_is_zero_alloc()
    {
        var s = new Signal<int>(0);
        var toggle = 0;
        AllocGuard.AssertZeroAlloc(() =>
            {
                toggle ^= 1;
                s.Value = toggle;
            }
        );
    }

    [Fact]
    public void Signal_update_is_zero_alloc()
    {
        var s = new Signal<int>(0);
        // `v => v + 1` captures nothing → the compiler caches it in a static field (no per-call alloc).
        AllocGuard.AssertZeroAlloc(() => s.Update(v => v + 1));
    }

    [Fact]
    public void Signal_write_with_an_effect_observer_is_zero_alloc()
    {
        // The core hot path: write → cascade marks observer → batch drains → effect body runs.
        var s = new Signal<int>(0);
        using var e = new Effect(() => _sink = s.Value);
        var toggle = 0;
        AllocGuard.AssertZeroAlloc(() =>
            {
                toggle ^= 1;
                s.Value = toggle;
            }
        );
    }

    [Fact]
    public void Observed_computed_recompute_is_zero_alloc()
    {
        var s = new Signal<int>(0);
        using var doubled = Computed.From(() => s.Value * 2);
        using var e = new Effect(() => _sink = doubled.Value); // makes `doubled` watched
        var toggle = 0;
        AllocGuard.AssertZeroAlloc(() =>
            {
                toggle ^= 1;
                s.Value = toggle;
            }
        );
    }

    [Fact]
    public void A_glitch_free_diamond_recompute_is_zero_alloc()
    {
        var a = new Signal<int>(0);
        using var b = Computed.From(() => a.Value + 1);
        using var c = Computed.From(() => a.Value + 2);
        using var e = new Effect(() => _sink = b.Value + c.Value);
        var toggle = 0;
        AllocGuard.AssertZeroAlloc(() =>
            {
                toggle ^= 1;
                a.Value = toggle;
            }
        );
    }

    [Fact]
    public void A_deep_computed_chain_recompute_is_zero_alloc()
    {
        var s = new Signal<int>(0);
        using var c1 = Computed.From(() => s.Value + 1);
        using var c2 = Computed.From(() => c1.Value + 1);
        using var c3 = Computed.From(() => c2.Value + 1);
        using var e = new Effect(() => _sink = c3.Value);
        var toggle = 0;
        AllocGuard.AssertZeroAlloc(() =>
            {
                toggle ^= 1;
                s.Value = toggle;
            }
        );
    }

    [Fact]
    public void Batch_of_writes_draining_once_is_zero_alloc()
    {
        var a = new Signal<int>(0);
        var b = new Signal<int>(0);
        using var e = new Effect(() => _sink = a.Value + b.Value);
        var toggle = 0;
        var body = () =>
        {
            toggle ^= 1;
            a.Value = toggle;
            b.Value = 1 - toggle;
        };
        AllocGuard.AssertZeroAlloc(() => Reactive.Batch(body));
    }

    [Fact]
    public void Effect_with_a_noncapturing_cleanup_rerun_is_zero_alloc()
    {
        // The Effect(Func<Action>) path: BeforeExecute runs the previous cleanup, Execute returns the
        // next. Zero-alloc as long as the returned cleanup doesn't capture (here it's a cached delegate);
        // a cleanup closure that captures per-run would allocate — that's caller-controlled, not the core.
        var s = new Signal<int>(0);
        var cleanup = () => { };
        using var e = new Effect(() =>
            {
                _sink = s.Value;
                return cleanup;
            }
        );
        var toggle = 0;
        AllocGuard.AssertZeroAlloc(() =>
            {
                toggle ^= 1;
                s.Value = toggle;
            }
        );
    }

    [Fact]
    public void Deferred_effect_mark_and_drain_is_zero_alloc()
    {
        // The EffectAffinity.Deferred path: the write parks the effect in the shared queue and
        // DrainDeferred runs it. Steady state must reuse the queue's capacity, not grow a list per frame.
        var s = new Signal<int>(0);
        using var e = new Effect(() => _sink = s.Value, EffectAffinity.Deferred);
        var toggle = 0;
        AllocGuard.AssertZeroAlloc(() =>
            {
                toggle ^= 1;
                s.Value = toggle;
                Reactive.DrainDeferred();
            }
        );
    }

    [Fact]
    public void Untracked_read_is_zero_alloc()
    {
        var s = new Signal<int>(5);
        var read = () => s.Value; // cached once
        AllocGuard.AssertZeroAlloc(() => _sink += Reactive.Untracked(read));
    }

    [Fact]
    public void Lazy_unobserved_computed_read_after_change_is_zero_alloc()
    {
        // The unobserved (version-sum) refresh path: change the source, then read the lazy computed.
        var s = new Signal<int>(0);
        using var c = Computed.From(() => s.Value * 2);
        var toggle = 0;
        AllocGuard.AssertZeroAlloc(() =>
            {
                toggle ^= 1;
                s.Value = toggle; // no observers → c not recomputed here
                _sink += c.Value; // lazy read → version-sum verify + recompute
            }
        );
    }
}
