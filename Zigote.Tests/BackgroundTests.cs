using Xunit;
using Zigote.Core.Threading;

namespace Zigote.Tests;

/// <summary>
///     Headless tests for <see cref="Background" />: the scope tree, the frame budget, and the two
///     failure modes it exists to remove. Driven through <see cref="Background.Manual" />, so every
///     assertion is about the rule rather than about how fast this machine happens to be.
/// </summary>
public class BackgroundTests
{
    private static bool Settle(Background background)
    {
        return background.Drain(TimeSpan.FromSeconds(5));
    }

    // ── the floor: failures are reported, not swallowed ────────────────────────

    [Fact]
    public void A_throwing_worker_is_reported_against_its_scope()
    {
        using var background = Background.Manual();
        var reported = new List<string>();
        var previous = Background.OnError;
        Background.OnError = (_, where) =>
        {
            lock (reported)
            {
                reported.Add(where);
            }
        };

        try
        {
            using var library = background.Child("library");
            library.Run(() => throw new InvalidOperationException("deliberate"));
            Assert.True(Settle(background));
            Assert.Equal(["app/library.A_throwing_worker_is_reported_against_its_scope"], reported);
        }
        finally
        {
            Background.OnError = previous;
        }
    }

    [Fact]
    public void A_failing_scope_does_not_stop_its_siblings_or_its_parent()
    {
        using var background = Background.Manual();
        var previous = Background.OnError;
        Background.OnError = (_, _) => { };

        try
        {
            using var left = background.Child("left");
            using var right = background.Child("right");

            left.Run(() => throw new InvalidOperationException("deliberate"));
            Assert.True(Settle(background));

            var ran = 0;
            right.Run(() => Interlocked.Increment(ref ran));
            background.Run(() => Interlocked.Increment(ref ran));
            Assert.True(Settle(background));

            // Supervision, not cascade: Kotlin's default would have cancelled the scope here.
            Assert.Equal(2, Volatile.Read(ref ran));
        }
        finally
        {
            Background.OnError = previous;
        }
    }

    [Fact]
    public void Disposing_a_scope_drops_its_work_and_nothing_elses()
    {
        using var background = Background.Manual();
        var left = background.Child("left");
        using var right = background.Child("right");

        var ran = 0;
        left.Dispose();
        left.Run(() => Interlocked.Increment(ref ran));
        right.Run(() => Interlocked.Increment(ref ran));
        Assert.True(Settle(background));

        Assert.Equal(1, Volatile.Read(ref ran));
    }

    [Fact]
    public void Disposing_a_parent_cancels_its_children()
    {
        var background = Background.Manual();
        using var child = background.Child("child");

        background.Dispose();

        var ran = 0;
        child.Run(() => Interlocked.Increment(ref ran));
        Assert.Equal(0, Volatile.Read(ref ran));
        Assert.True(child.Lifetime.IsCancellationRequested);
    }

    // ── latest-wins ────────────────────────────────────────────────────────────

    [Fact]
    public void Latest_delivers_only_the_newest_run()
    {
        using var background = Background.Manual();
        using var latest = background.Latest();
        var landed = new List<int>();

        latest.Run(_ => 1, landed.Add, TimeSpan.FromMilliseconds(500));
        latest.Run(_ => 2, landed.Add);
        Assert.True(Settle(background));

        Assert.Equal([2], landed);
    }

    // ── the frame budget ───────────────────────────────────────────────────────

    [Fact]
    public void A_zero_budget_slice_makes_exactly_one_unit_of_progress_per_frame()
    {
        using var background = Background.Manual();
        const int units = 200;
        var built = 0;
        var finished = 0;

        background.Slice(
            "rows",
            units,
            _ => built++,
            () => finished++,
            TimeSpan.Zero
        );

        // Forward progress must not depend on the budget being generous enough for one step:
        // a unit costing more than the whole budget would otherwise never run at all.
        Assert.Equal(1, built);
        Assert.False(background.FrameIdle);

        var frames = 0;
        while (!background.FrameIdle && frames < units * 2)
        {
            background.RunFrame(TimeSpan.Zero);
            frames++;
        }

        Assert.Equal(units, built);
        Assert.Equal(units - 1, frames);
        Assert.Equal(1, finished); // exactly once, however many frames it took
    }

    [Fact]
    public void Two_slices_share_the_frame_instead_of_one_starving()
    {
        using var background = Background.Manual();
        var left = 0;
        var right = 0;

        background.Slice(
            "left",
            20,
            _ => left++,
            null,
            TimeSpan.Zero
        );
        background.Slice(
            "right",
            20,
            _ => right++,
            null,
            TimeSpan.Zero
        );
        for (var i = 0; i < 6; i++) background.RunFrame(TimeSpan.Zero);

        Assert.True(left > 1, $"left starved at {left}");
        Assert.True(right > 1, $"right starved at {right}");
    }

    [Fact]
    public void Starting_a_slice_under_a_running_key_replaces_it()
    {
        using var background = Background.Manual();
        var first = 0;
        var second = 0;

        background.Slice(
            "rows",
            10_000,
            _ => first++,
            null,
            TimeSpan.Zero
        );
        background.Slice(
            "rows",
            10,
            _ => second++,
            null,
            TimeSpan.Zero
        );
        while (!background.FrameIdle) background.RunFrame(TimeSpan.FromMilliseconds(1));

        Assert.Equal(10, second);
        Assert.True(first < 10_000, "the superseded slice kept running");
    }

    [Fact]
    public void Idle_delivery_keeps_arrival_order()
    {
        using var background = Background.Manual();
        var order = new List<int>();

        for (var i = 0; i < 5; i++)
        {
            var value = i;
            background.Post(() => order.Add(value), Deliver.WhenIdle);
        }

        background.RunFrame(TimeSpan.FromSeconds(1));

        Assert.Equal([0, 1, 2, 3, 4], order);
    }

    [Fact]
    public void A_slice_whose_owner_is_disposed_stops_rather_than_running_to_completion()
    {
        using var background = Background.Manual();
        var page = background.Child("page");
        var built = 0;

        page.Slice(
            "rows",
            1000,
            _ => built++,
            null,
            TimeSpan.Zero
        );
        var afterFirstFrame = built;

        page.Dispose();
        while (!background.FrameIdle) background.RunFrame(TimeSpan.Zero);

        Assert.Equal(afterFirstFrame, built);
    }

    // ── the idle frame, which is nearly every frame ────────────────────────────

    [Fact]
    public void RunFrame_with_nothing_queued_allocates_zero()
    {
        using var background = Background.Manual();

        // This runs once per frame for the life of the process. A per-frame allocation on the path
        // that does nothing is the kind of garbage that shows up as a periodic GC with no line of
        // code to blame it on.
        AllocGuard.AssertZeroAlloc(() => background.RunFrame(TimeSpan.FromMilliseconds(4)));
    }

    [Fact]
    public void RunFrame_advancing_a_slice_allocates_zero()
    {
        using var background = Background.Manual();
        var built = 0;

        // Re-armed inside the iteration, so the steady state being measured is "a slice is filling",
        // not "a slice was started".
        AllocGuard.AssertZeroAlloc(
            () =>
            {
                if (background.FrameIdle)
                    background.Slice(
                        "rows",
                        1_000_000,
                        _ => built++,
                        null,
                        TimeSpan.Zero
                    );
                background.RunFrame(TimeSpan.Zero);
            },
            20,
            200
        );
    }
}