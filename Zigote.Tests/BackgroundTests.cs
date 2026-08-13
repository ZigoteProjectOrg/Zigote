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
    private static bool Settle(Background background) => background.Drain(TimeSpan.FromSeconds(5));

    // ── the floor: failures are reported, not swallowed ────────────────────────

    [Fact]
    public void A_throwing_worker_is_reported_against_its_scope()
    {
        using var background = Background.Manual();
        var reported = new List<string>();
        var previous = Background.OnError;
        Background.OnError = (_, where) =>
        {
            lock (reported) reported.Add(where);
        };

        try
        {
            using var library = background.Child("library");
            library.Run(() => throw new InvalidOperationException("deliberate"));
            Assert.True(Settle(background));
            Assert.Equal(
                expected: ["app/library.A_throwing_worker_is_reported_against_its_scope"],
                actual: reported
            );
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

            int ran = 0;
            right.Run(() => Interlocked.Increment(ref ran));
            background.Run(() => Interlocked.Increment(ref ran));
            Assert.True(Settle(background));

            // Supervision, not cascade: Kotlin's default would have cancelled the scope here.
            Assert.Equal(expected: 2, actual: Volatile.Read(ref ran));
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

        int ran = 0;
        left.Dispose();
        left.Run(() => Interlocked.Increment(ref ran));
        right.Run(() => Interlocked.Increment(ref ran));
        Assert.True(Settle(background));

        Assert.Equal(expected: 1, actual: Volatile.Read(ref ran));
    }

    [Fact]
    public void Disposing_a_parent_cancels_its_children()
    {
        var background = Background.Manual();
        using var child = background.Child("child");

        background.Dispose();

        int ran = 0;
        child.Run(() => Interlocked.Increment(ref ran));
        Assert.Equal(expected: 0, actual: Volatile.Read(ref ran));
        Assert.True(child.Lifetime.IsCancellationRequested);
    }

    // ── latest-wins ────────────────────────────────────────────────────────────

    [Fact]
    public void Latest_delivers_only_the_newest_run()
    {
        using var background = Background.Manual();
        using var latest = background.Latest();
        var landed = new List<int>();

        latest.Run(work: _ => 1, onUi: landed.Add, delay: TimeSpan.FromMilliseconds(500));
        latest.Run(work: _ => 2, onUi: landed.Add);
        Assert.True(Settle(background));

        Assert.Equal(expected: [2], actual: landed);
    }

    // ── the frame budget ───────────────────────────────────────────────────────

    [Fact]
    public void A_zero_budget_slice_makes_exactly_one_unit_of_progress_per_frame()
    {
        using var background = Background.Manual();
        const int units = 200;
        int built = 0;
        int finished = 0;

        background.Slice(
            key: "rows",
            count: units,
            step: _ => built++,
            onDone: () => finished++,
            firstFrame: TimeSpan.Zero
        );

        // Forward progress must not depend on the budget being generous enough for one step:
        // a unit costing more than the whole budget would otherwise never run at all.
        Assert.Equal(expected: 1, actual: built);
        Assert.False(background.FrameIdle);

        int frames = 0;
        while (!background.FrameIdle && frames < units * 2)
        {
            background.RunFrame(TimeSpan.Zero);
            frames++;
        }

        Assert.Equal(expected: units, actual: built);
        Assert.Equal(expected: units - 1, actual: frames);
        Assert.Equal(expected: 1, actual: finished); // exactly once, however many frames it took
    }

    [Fact]
    public void Two_slices_share_the_frame_instead_of_one_starving()
    {
        using var background = Background.Manual();
        int left = 0;
        int right = 0;

        background.Slice(
            key: "left",
            count: 20,
            step: _ => left++,
            onDone: null,
            firstFrame: TimeSpan.Zero
        );
        background.Slice(
            key: "right",
            count: 20,
            step: _ => right++,
            onDone: null,
            firstFrame: TimeSpan.Zero
        );
        for (int i = 0; i < 6; i++) background.RunFrame(TimeSpan.Zero);

        Assert.True(condition: left > 1, userMessage: $"left starved at {left}");
        Assert.True(condition: right > 1, userMessage: $"right starved at {right}");
    }

    [Fact]
    public void Starting_a_slice_under_a_running_key_replaces_it()
    {
        using var background = Background.Manual();
        int first = 0;
        int second = 0;

        background.Slice(
            key: "rows",
            count: 10_000,
            step: _ => first++,
            onDone: null,
            firstFrame: TimeSpan.Zero
        );
        background.Slice(
            key: "rows",
            count: 10,
            step: _ => second++,
            onDone: null,
            firstFrame: TimeSpan.Zero
        );
        while (!background.FrameIdle) background.RunFrame(TimeSpan.FromMilliseconds(1));

        Assert.Equal(expected: 10, actual: second);
        Assert.True(condition: first < 10_000, userMessage: "the superseded slice kept running");
    }

    [Fact]
    public void Idle_delivery_keeps_arrival_order()
    {
        using var background = Background.Manual();
        var order = new List<int>();

        for (int i = 0; i < 5; i++)
        {
            int value = i;
            background.Post(ui: () => order.Add(value), deliver: Deliver.WhenIdle);
        }

        background.RunFrame(TimeSpan.FromSeconds(1));

        Assert.Equal(expected: [0, 1, 2, 3, 4], actual: order);
    }

    [Fact]
    public void A_slice_whose_owner_is_disposed_stops_rather_than_running_to_completion()
    {
        using var background = Background.Manual();
        var page = background.Child("page");
        int built = 0;

        page.Slice(
            key: "rows",
            count: 1000,
            step: _ => built++,
            onDone: null,
            firstFrame: TimeSpan.Zero
        );
        int afterFirstFrame = built;

        page.Dispose();
        while (!background.FrameIdle) background.RunFrame(TimeSpan.Zero);

        Assert.Equal(expected: afterFirstFrame, actual: built);
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
        int built = 0;

        // Re-armed inside the iteration, so the steady state being measured is "a slice is filling",
        // not "a slice was started".
        AllocGuard.AssertZeroAlloc(
            iteration: () =>
            {
                if (background.FrameIdle)
                {
                    background.Slice(
                        key: "rows",
                        count: 1_000_000,
                        step: _ => built++,
                        onDone: null,
                        firstFrame: TimeSpan.Zero
                    );
                }

                background.RunFrame(TimeSpan.Zero);
            },
            warmup: 20,
            iterations: 200
        );
    }
}
