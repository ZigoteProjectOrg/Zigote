namespace Zigote.Core.Events;

/// <summary>
///     Per-poll object pool for the high-frequency input events (<see cref="MouseMoveEvent" /> /
///     <see cref="ScrollEvent" />). These fire faster than the frame rate during a drag or momentum
///     scroll, so allocating a fresh object per event is steady input-rate GC pressure on a path the
///     engine otherwise keeps allocation-free.
///     <para>
///         <see cref="Zigote.Core.Engine.ZigoteEngine.PollEventsInto" /> calls <see cref="Reset" /> at
///         the start of every poll and rents instances for that frame; the rented objects are reused
///         on the next poll. This is safe because every decoded event is dispatched and dropped within
///         the same frame — nothing retains an <see cref="InputEvent" /> across polls (the dispatcher
///         hands widgets decomposed primitives, never the event object). Within a single poll each
///         rent returns a <em>distinct</em> instance (the cursor advances, growing the backing list as
///         needed), so several moves in one frame keep their own coordinates.
///     </para>
///     Discrete events (clicks/keys/text) fire at human rates and are left allocating — pooling them
///     would save negligible garbage for extra mutable state.
/// </summary>
public sealed class EventPool
{
    private readonly List<MouseMoveEvent> _mouseMove = [];
    private readonly List<ScrollEvent> _scroll = [];
    private int _mouseMoveNext;
    private int _scrollNext;

    /// <summary>Return every rented instance to the pool. Call once at the start of each poll.</summary>
    public void Reset()
    {
        _mouseMoveNext = 0;
        _scrollNext = 0;
    }

    /// <summary>Rent (or grow) a <see cref="MouseMoveEvent" /> and overwrite it with the given values.</summary>
    public MouseMoveEvent RentMouseMove(float x, float y, uint windowId)
    {
        MouseMoveEvent e;
        if (_mouseMoveNext < _mouseMove.Count)
        {
            e = _mouseMove[_mouseMoveNext];
        }
        else
        {
            e = new MouseMoveEvent();
            _mouseMove.Add(e);
        }

        _mouseMoveNext++;
        e.Reuse(x, y, windowId);
        return e;
    }

    /// <summary>Rent (or grow) a <see cref="ScrollEvent" /> and overwrite it with the given values.</summary>
    public ScrollEvent RentScroll(float x, float y, float scrollX, float scrollY, uint windowId)
    {
        ScrollEvent e;
        if (_scrollNext < _scroll.Count)
        {
            e = _scroll[_scrollNext];
        }
        else
        {
            e = new ScrollEvent();
            _scroll.Add(e);
        }

        _scrollNext++;
        e.Reuse(
            x,
            y,
            scrollX,
            scrollY,
            windowId
        );
        return e;
    }
}