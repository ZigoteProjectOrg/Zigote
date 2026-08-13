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

        var a = pool.RentMouseMove(x: 1f, y: 2f, windowId: 0);
        var b = pool.RentMouseMove(x: 3f, y: 4f, windowId: 5);

        Assert.NotSame(expected: a, actual: b);
        Assert.Equal(expected: 1f, actual: a.X);
        Assert.Equal(expected: 2f, actual: a.Y);
        Assert.Equal(expected: 0u, actual: a.WindowId);
        Assert.Equal(expected: 3f, actual: b.X);
        Assert.Equal(expected: 4f, actual: b.Y);
        Assert.Equal(expected: 5u, actual: b.WindowId);
    }

    [Fact]
    public void RentMouseMove_AfterReset_ReusesInstancesInOrderWithNewValues()
    {
        var pool = new EventPool();

        var a1 = pool.RentMouseMove(x: 1f, y: 2f, windowId: 0);
        var b1 = pool.RentMouseMove(x: 3f, y: 4f, windowId: 0);

        pool.Reset();

        var a2 = pool.RentMouseMove(x: 10f, y: 20f, windowId: 7);
        var b2 = pool.RentMouseMove(x: 30f, y: 40f, windowId: 8);

        // Same objects come back in the same order — no allocation on the second poll.
        Assert.Same(expected: a1, actual: a2);
        Assert.Same(expected: b1, actual: b2);
        Assert.Equal(expected: 10f, actual: a2.X);
        Assert.Equal(expected: 20f, actual: a2.Y);
        Assert.Equal(expected: 7u, actual: a2.WindowId);
        Assert.Equal(expected: 30f, actual: b2.X);
        Assert.Equal(expected: 40f, actual: b2.Y);
    }

    [Fact]
    public void RentMouseMove_AfterReset_ClearsRelativeMotionOfPreviousPoll()
    {
        var pool = new EventPool();

        var a1 = pool.RentMouseMove(
            x: 1f,
            y: 2f,
            windowId: 0,
            relativeX: 5f,
            relativeY: -5f
        );
        Assert.Equal(expected: 5f, actual: a1.RelativeX);
        Assert.Equal(expected: -5f, actual: a1.RelativeY);

        pool.Reset();

        // A free-cursor move carries no relative motion. The reused instance must report zero rather
        // than the previous poll's delta — a stale delta here is a camera that keeps turning by itself.
        var a2 = pool.RentMouseMove(x: 3f, y: 4f, windowId: 0);
        Assert.Same(expected: a1, actual: a2);
        Assert.Equal(expected: 0f, actual: a2.RelativeX);
        Assert.Equal(expected: 0f, actual: a2.RelativeY);
    }

    [Fact]
    public void RentMouseMove_GrowsWhenPollHasMoreThanBefore()
    {
        var pool = new EventPool();
        pool.RentMouseMove(x: 0f, y: 0f, windowId: 0);
        pool.Reset();

        // Second poll asks for two — the first is reused, the second is grown fresh, both usable.
        var a = pool.RentMouseMove(x: 1f, y: 1f, windowId: 0);
        var b = pool.RentMouseMove(x: 2f, y: 2f, windowId: 0);
        Assert.NotSame(expected: a, actual: b);
        Assert.Equal(expected: 2f, actual: b.X);
    }

    [Fact]
    public void RentScroll_IsIndependentAndReuses()
    {
        var pool = new EventPool();

        var s1 = pool.RentScroll(
            x: 1f,
            y: 2f,
            scrollX: 0.5f,
            scrollY: -0.5f,
            windowId: 3
        );
        Assert.Equal(expected: 0.5f, actual: s1.ScrollX);
        Assert.Equal(expected: -0.5f, actual: s1.ScrollY);
        Assert.Equal(expected: 3u, actual: s1.WindowId);

        pool.Reset();
        var s2 = pool.RentScroll(
            x: 9f,
            y: 9f,
            scrollX: 1f,
            scrollY: 1f,
            windowId: 0
        );
        Assert.Same(expected: s1, actual: s2);
        Assert.Equal(expected: 1f, actual: s2.ScrollX);
    }
}
