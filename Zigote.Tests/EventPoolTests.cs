using Xunit;
using Zigote.Core.Events;

namespace Zigote.Tests;

/// <summary>
///     Guards the input-event pool that <c>ZigoteEngine.PollEventsInto</c> uses to reuse
///     <see cref="MouseMoveEvent" />/<see cref="ScrollEvent" /> instances across polls. The two
///     invariants that make pooling safe: within one poll each rent is a <em>distinct</em> instance
///     carrying its own values (several moves per frame don't collapse), and after
///     <see cref="EventPool.Reset" />
///     the same instances come back reused with fresh values (steady-state zero allocation).
/// </summary>
public class EventPoolTests
{
    [Fact]
    public void RentMouseMove_WithinPoll_DistinctInstancesKeepOwnValues()
    {
        var pool = new EventPool();

        var a = pool.RentMouseMove(1f, 2f, 0);
        var b = pool.RentMouseMove(3f, 4f, 5);

        Assert.NotSame(a, b);
        Assert.Equal(1f, a.X);
        Assert.Equal(2f, a.Y);
        Assert.Equal(0u, a.WindowId);
        Assert.Equal(3f, b.X);
        Assert.Equal(4f, b.Y);
        Assert.Equal(5u, b.WindowId);
    }

    [Fact]
    public void RentMouseMove_AfterReset_ReusesInstancesInOrderWithNewValues()
    {
        var pool = new EventPool();

        var a1 = pool.RentMouseMove(1f, 2f, 0);
        var b1 = pool.RentMouseMove(3f, 4f, 0);

        pool.Reset();

        var a2 = pool.RentMouseMove(10f, 20f, 7);
        var b2 = pool.RentMouseMove(30f, 40f, 8);

        // Same objects come back in the same order — no allocation on the second poll.
        Assert.Same(a1, a2);
        Assert.Same(b1, b2);
        Assert.Equal(10f, a2.X);
        Assert.Equal(20f, a2.Y);
        Assert.Equal(7u, a2.WindowId);
        Assert.Equal(30f, b2.X);
        Assert.Equal(40f, b2.Y);
    }

    [Fact]
    public void RentMouseMove_AfterReset_ClearsRelativeMotionOfPreviousPoll()
    {
        var pool = new EventPool();

        var a1 = pool.RentMouseMove(
            1f,
            2f,
            0,
            5f,
            -5f
        );
        Assert.Equal(5f, a1.RelativeX);
        Assert.Equal(-5f, a1.RelativeY);

        pool.Reset();

        // A free-cursor move carries no relative motion. The reused instance must report zero rather
        // than the previous poll's delta — a stale delta here is a camera that keeps turning by itself.
        var a2 = pool.RentMouseMove(3f, 4f, 0);
        Assert.Same(a1, a2);
        Assert.Equal(0f, a2.RelativeX);
        Assert.Equal(0f, a2.RelativeY);
    }

    [Fact]
    public void RentMouseMove_GrowsWhenPollHasMoreThanBefore()
    {
        var pool = new EventPool();
        pool.RentMouseMove(0f, 0f, 0);
        pool.Reset();

        // Second poll asks for two — the first is reused, the second is grown fresh, both usable.
        var a = pool.RentMouseMove(1f, 1f, 0);
        var b = pool.RentMouseMove(2f, 2f, 0);
        Assert.NotSame(a, b);
        Assert.Equal(2f, b.X);
    }

    [Fact]
    public void RentScroll_IsIndependentAndReuses()
    {
        var pool = new EventPool();

        var s1 = pool.RentScroll(
            1f,
            2f,
            0.5f,
            -0.5f,
            3
        );
        Assert.Equal(0.5f, s1.ScrollX);
        Assert.Equal(-0.5f, s1.ScrollY);
        Assert.Equal(3u, s1.WindowId);

        pool.Reset();
        var s2 = pool.RentScroll(
            9f,
            9f,
            1f,
            1f,
            0
        );
        Assert.Same(s1, s2);
        Assert.Equal(1f, s2.ScrollX);
    }
}
