using Zigote.Core;
using Zigote.Core.Events;
using Zigote.UI.Widgets;

namespace Zigote.UI.Host;

// Touchscreen routing — promotes the FIRST finger down (the "primary" finger) into the same
// pointer pipeline mouse input uses, so every existing widget works with touch unchanged, and
// layers the touch-only gestures on top at the App level:
//
//   • Tap: down → up within the slop radius flows as OnPointerDown/OnPointerUp — Pressable,
//     GestureDetector, text fields etc. see exactly what a mouse click produces.
//   • Drag-to-scroll: once the finger travels past the slop, the drag's dominant axis picks a
//     scrollable from the hit chain (Widget.CanTouchScroll); the pressed widget gets
//     OnPointerCancel (its press must not commit) and further movement drives
//     OnTouchScroll 1:1, with OnTouchFling inertia on lift. A drag whose axis no scrollable
//     wants keeps flowing to the pressed widget (drag-select, Draggable).
//     A widget already dragging on its own (Widget.CanTouchDrag — a slider being scrubbed, a
//     divider being moved) is asked FIRST and outranks every scroller above it, the way iOS
//     exempts UIControls from a scroll view's touch cancellation: without that, a fader in a
//     scrolling page can never move and a slider loses any drag that sets off downward.
//   • Long-press: held in place past the threshold → Widget.OnLongPress, whose default maps
//     to OnRightClick so context menus work on touch without per-widget changes.
//
//   • Pinch: a second finger down promotes the gesture to a scale. The nearest ancestor that
//     answers Widget.CanTouchScale takes it; the first finger's press is cancelled (a pinch is
//     never also a tap), and finger-distance ratio drives OnTouchScale while centroid movement
//     drives OnTouchScroll, so zooming and panning are one continuous gesture. Nothing resumes
//     when a finger lifts — the whole gesture ends, which is what every platform does.
//
// A third finger is tracked but ignored: no gesture here uses one, and letting it perturb the
// pinch's centroid would only make two-finger zoom jitter. Touch produces no hover:
// OnPointerEnter/Exit never fire from fingers, and no cursor is resolved.
public partial class App
{
    /// <summary>Movement budget (logical px) within which a touch still counts as a tap/long-press.</summary>
    private const float TouchSlop = 12f;

    private const float LongPressSeconds = 0.5f;

    /// <summary>Below this lift-off speed (logical px/s) no fling starts.</summary>
    private const float MinFlingVelocity = 80f;

    /// <summary>Width of the leading-edge strip where a back swipe can start (logical px).</summary>
    private const float EdgeBackWidth = 20f;

    /// <summary>Inward travel that commits an edge swipe to a back navigation.</summary>
    private const float EdgeBackTravel = 48f;

    // Frame-accumulated finger displacement, folded into the smoothed velocity by TickTouch —
    // per-event dt inside one poll batch is meaningless, per-frame dt is real.
    private float _touchFrameDx, _touchFrameDy;
    private float _touchHeldSeconds;
    private Offset _touchLast;
    private bool _touchLongPressFired;
    private bool _touchMovedPastSlop;

    /// <summary>Finger slot driving the pointer pipeline; -1 = no touch interaction active.</summary>
    private int _touchPrimaryFinger = -1;

    /// <summary>Second finger of a pinch; -1 = none down.</summary>
    private int _touchSecondFinger = -1;

    private Offset _touchSecondLast;

    /// <summary>Finger separation at the last scale event — the denominator of the next ratio.</summary>
    private float _pinchLastDistance;

    private Offset _pinchLastCentroid;

    /// <summary>The widget consuming the active pinch, null when no pinch is in flight.</summary>
    private Widget? _touchScaleTarget;

    private Widget? _touchScrollTarget;

    /// <summary>An in-flight back gesture: the finger started at the screen's leading edge.</summary>
    private bool _touchEdgeBack;

    private Offset _touchStart;
    private float _touchVelX, _touchVelY;

    // Per-thread like Widget.CurrentScrollParent: input dispatch and the hit-test walk it drives
    // run on the same thread, and a process-wide flag would leak between parallel test hosts.
    [ThreadStatic] internal static bool PointerIsTouchFlag;

    /// <summary>
    ///     True when the most recent pointer input came from a finger rather than a mouse. Widgets
    ///     read it to size hit rects for the pointer actually in use — a 14 px scrollbar strip or a
    ///     16 px checkbox is precise under a cursor and unusable under a fingertip — without
    ///     changing anything about how they lay out or paint.
    /// </summary>
    public static bool PointerIsTouch => PointerIsTouchFlag;

    private void DispatchTouchEvent(TouchEvent evt)
    {
        PointerIsTouchFlag = true;
        var point = new Offset(evt.X, evt.Y);
        switch (evt)
        {
            case TouchDownEvent when _touchPrimaryFinger < 0:
            {
                _touchPrimaryFinger = evt.Finger;
                _touchStart = _touchLast = point;
                _touchMovedPastSlop = false;
                _touchLongPressFired = false;
                _touchHeldSeconds = 0f;
                _touchScrollTarget = null;
                _touchFrameDx = _touchFrameDy = 0f;
                _touchVelX = _touchVelY = 0f;
                // A finger starting within the leading edge strip may become a back gesture.
                // iOS has no system back control, so the edge swipe IS the platform convention;
                // it costs nothing elsewhere because the gesture only completes on a decisive
                // inward drag. RTL flips the edge, matching the direction "back" travels.
                var edge = Directionality.Of(BuildContext.Current) == TextDirection.Rtl
                    ? point.X >= HostLogicalWidth - EdgeBackWidth
                    : point.X <= EdgeBackWidth;
                _touchEdgeBack = edge && CanHandleSystemBack;

                // Mirror the left-mouse-down path: capture, focus, deliver the press. No hover
                // transition and no cursor — fingers have neither.
                var hit = HitTestAll(point);
                _capturedWidget = hit;
                SetFocusRingVisible(false);
                if (hit is { Focusable: true })
                    RequestFocus(hit);
                else if (hit is not null && hit != FocusedWidget)
                    ClearFocus();

                hit?.OnPointerDown(point);
                if (hit is not null) MarkPaintFor(hit);
                break;
            }

            case TouchDownEvent when _touchSecondFinger < 0 && evt.Finger != _touchPrimaryFinger:
            {
                // Second finger: try to promote to a pinch. If nothing in the chain zooms, the
                // finger is simply ignored and the primary keeps its drag.
                var target = FindTouchScaleTarget();
                if (target is null) break;

                _touchSecondFinger = evt.Finger;
                _touchSecondLast = point;
                _touchScaleTarget = target;
                _pinchLastDistance = Distance(_touchLast, point);
                _pinchLastCentroid = Centroid(_touchLast, point);

                // A pinch is not a tap, a long-press, or a drag: whatever the first finger was
                // doing must abandon rather than commit. A scroll in flight keeps its position
                // but stops receiving deltas — the pinch owns both fingers now.
                _touchScrollTarget = null;
                _touchMovedPastSlop = true; // disarms the long-press timer
                _touchEdgeBack = false;
                if (_capturedWidget is not null && _capturedWidget != target)
                {
                    _capturedWidget.OnPointerCancel();
                    MarkPaintFor(_capturedWidget);
                    _capturedWidget = null;
                }

                break;
            }

            case TouchDownEvent:
                // Third and later fingers: tracked by the engine, ignored here (see the header).
                break;

            case TouchMoveEvent when _touchScaleTarget is not null &&
                                     (evt.Finger == _touchPrimaryFinger ||
                                      evt.Finger == _touchSecondFinger):
            {
                if (evt.Finger == _touchPrimaryFinger) _touchLast = point;
                else _touchSecondLast = point;

                var distance = Distance(_touchLast, _touchSecondLast);
                var centroid = Centroid(_touchLast, _touchSecondLast);

                // Guard the ratio: fingers can land on the same pixel, and dividing by ~0 would
                // send an infinite scale into the consumer's transform.
                if (_pinchLastDistance > 1f && distance > 1f)
                {
                    var scale = distance / _pinchLastDistance;
                    if (MathF.Abs(scale - 1f) > 0.0001f)
                        _touchScaleTarget.OnTouchScale(scale, centroid);
                    _pinchLastDistance = distance;
                }

                // Centroid travel pans the zoomed content — same 1:1 finger-pixel contract as a
                // one-finger drag, so a widget that already implements OnTouchScroll gets pan free.
                var panX = centroid.X - _pinchLastCentroid.X;
                var panY = centroid.Y - _pinchLastCentroid.Y;
                if (panX != 0f || panY != 0f)
                {
                    _touchScaleTarget.OnTouchScroll(panX, panY);
                    _pinchLastCentroid = centroid;
                }

                MarkPaintFor(_touchScaleTarget);
                break;
            }

            case TouchUpEvent or TouchCancelEvent
                when _touchScaleTarget is not null &&
                     (evt.Finger == _touchPrimaryFinger || evt.Finger == _touchSecondFinger):
                // Either finger leaving ends the pinch outright. Handing the remaining finger back
                // as a drag would jerk the content by the centroid-to-finger offset; every
                // platform ends the gesture instead and waits for a clean new touch.
                ResetTouch();
                break;

            case TouchMoveEvent when evt.Finger == _touchPrimaryFinger:
            {
                var dx = point.X - _touchLast.X;
                var dy = point.Y - _touchLast.Y;
                _touchLast = point;
                _touchFrameDx += dx;
                _touchFrameDy += dy;

                if (_touchScrollTarget is not null)
                {
                    _touchScrollTarget.OnTouchScroll(dx, dy);
                    break;
                }

                // A long-press has already committed the gesture to the pressed widget (lift a
                // Draggable, grab a reorder row): the surrounding scroller must not steal it back,
                // or press-and-hold-then-drag — the only way to drag inside a scrolling page on a
                // touchscreen — could never complete.
                if (_touchEdgeBack)
                {
                    var edgeDx = point.X - _touchStart.X;
                    var edgeDy = point.Y - _touchStart.Y;
                    var inward = Directionality.Of(BuildContext.Current) == TextDirection.Rtl
                        ? -edgeDx
                        : edgeDx;
                    // Mostly-horizontal travel inward from the edge: hand the gesture to the
                    // back chain and abandon whatever the finger had pressed.
                    if (inward > EdgeBackTravel && MathF.Abs(edgeDy) < inward)
                    {
                        _touchEdgeBack = false;
                        _touchMovedPastSlop = true;
                        _capturedWidget?.OnPointerCancel();
                        _capturedWidget = null;
                        HandleSystemBack();
                        break;
                    }

                    // Drifted vertically first — this is a scroll, not a back gesture.
                    if (MathF.Abs(edgeDy) > TouchSlop) _touchEdgeBack = false;
                }

                if (!_touchMovedPastSlop && !_touchLongPressFired)
                {
                    var totX = point.X - _touchStart.X;
                    var totY = point.Y - _touchStart.Y;
                    if (totX * totX + totY * totY > TouchSlop * TouchSlop)
                    {
                        _touchMovedPastSlop = true; // also disarms the long-press
                        var vertical = MathF.Abs(totY) >= MathF.Abs(totX);
                        var claimer = FindTouchScrollTarget(vertical);
                        if (claimer is not null)
                        {
                            // The scroll gesture owns the pointer now: the pressed widget must
                            // abandon (not commit) its interaction — unless it IS the claimer, in
                            // which case it is scrolling *itself* (CanTouchScroll) and cancelling
                            // would abandon the very gesture it just took over. Then the content
                            // catches up by the full pre-slop distance so no movement is swallowed.
                            if (claimer != _capturedWidget)
                            {
                                _capturedWidget?.OnPointerCancel();
                                if (_capturedWidget is not null) MarkPaintFor(_capturedWidget);
                                _capturedWidget = null;
                            }

                            _touchScrollTarget = claimer;
                            claimer.OnTouchScroll(totX, totY);
                            break;
                        }
                    }
                }

                // No scrollable claimed the drag — the pressed widget keeps the moves
                // (slider scrub, drag-select, Draggable promotion), like a mouse drag.
                if (_capturedWidget is not null)
                {
                    _capturedWidget.OnPointerMove(point);
                    MarkPaintFor(_capturedWidget);
                }

                break;
            }

            case TouchUpEvent when evt.Finger == _touchPrimaryFinger:
            {
                if (_touchScrollTarget is not null)
                {
                    if (MathF.Abs(_touchVelX) > MinFlingVelocity ||
                        MathF.Abs(_touchVelY) > MinFlingVelocity)
                        _touchScrollTarget.OnTouchFling(_touchVelX, _touchVelY);

                    // A widget that claimed its own drag kept the press too — the scroll path
                    // never delivers a lift, and it needs one to commit (drop a reordered row,
                    // release a grabbed key).
                    if (_capturedWidget is not null && _capturedWidget == _touchScrollTarget)
                    {
                        _capturedWidget.OnPointerUp(point);
                        MarkPaintFor(_capturedWidget);
                    }
                }
                else
                {
                    _capturedWidget?.OnPointerUp(point);
                    // A long-press's default mapping opened the right-click channel
                    // (OnRightClick); complete it the way a physical right button would.
                    if (_touchLongPressFired) _capturedWidget?.OnRightPointerUp(point);
                    if (_capturedWidget is not null) MarkPaintFor(_capturedWidget);
                }

                ResetTouch();
                break;
            }

            case TouchCancelEvent when evt.Finger == _touchPrimaryFinger:
                // OS took the gesture / app backgrounded: abandon everything, fling nothing.
                _capturedWidget?.OnPointerCancel();
                if (_capturedWidget is not null) MarkPaintFor(_capturedWidget);
                ResetTouch();
                break;
        }
    }

    /// <summary>
    ///     Per-frame touch bookkeeping: long-press timing and velocity smoothing. Runs from
    ///     <see cref="Frame" /> after event dispatch, so a held finger ripens into a long-press
    ///     even when no further events arrive.
    /// </summary>
    private void TickTouch(float dt)
    {
        if (_touchPrimaryFinger < 0 || dt <= 0f) return;

        // Exponentially-smoothed lift-off velocity from this frame's displacement. A resting
        // finger decays toward zero, so pausing mid-drag then lifting produces no fling.
        var k = 1f - MathF.Exp(-dt * 15f);
        _touchVelX += (_touchFrameDx / dt - _touchVelX) * k;
        _touchVelY += (_touchFrameDy / dt - _touchVelY) * k;
        _touchFrameDx = _touchFrameDy = 0f;

        if (_touchMovedPastSlop || _touchLongPressFired || _touchScrollTarget is not null)
            return;
        _touchHeldSeconds += dt;
        if (_touchHeldSeconds < LongPressSeconds) return;
        _touchLongPressFired = true;
        if (_capturedWidget is null) return;
        _capturedWidget.OnLongPress(_touchLast);
        MarkPaintFor(_capturedWidget);
        // A long-press typically opens UI (context menu / selection handles) — treat it like
        // any other discrete interaction and bring layout current before painting.
        _pendingRelayout = true;
    }

    /// <summary>
    ///     The widget that should consume a touch drag along <paramref name="vertical" />:
    ///     the pressed widget itself if it scrolls that axis, else the nearest scroll ancestor
    ///     that does (following the same ScrollParent chain wheel bubbling uses). Null when
    ///     nothing scrollable wants the axis — the drag then belongs to the pressed widget.
    ///     <para>
    ///         A widget already dragging on this axis (<see cref="Widget.CanTouchDrag" />) is asked
    ///         first and outranks every scroller above it — the control the finger is on keeps its
    ///         own gesture. Otherwise a vertical fader inside a scrolling page could never move (the
    ///         page took every drag on it) and a slider lost the gesture whenever the finger settled
    ///         downward before setting off sideways.
    ///     </para>
    /// </summary>
    private Widget? FindTouchScrollTarget(bool vertical)
    {
        for (var w = _capturedWidget; w is not null; w = w.ScrollParent)
        {
            if (w.CanTouchDrag(vertical)) return null;
            if (w.CanTouchScroll(vertical)) return w;
        }

        return null;
    }

    /// <summary>
    ///     The widget that should consume a pinch: the pressed widget if it zooms, else the nearest
    ///     ancestor that does. Walks <see cref="Widget.Parent" /> rather than the scroll chain — a
    ///     zoomable image is not a scroller, and the two hierarchies do not coincide.
    /// </summary>
    private Widget? FindTouchScaleTarget()
    {
        var start = _capturedWidget ?? _touchScrollTarget ?? HitTestAll(_touchLast);
        for (var w = start; w is not null; w = w.Parent)
            if (w.CanTouchScale())
                return w;
        return null;
    }

    private static float Distance(Offset a, Offset b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static Offset Centroid(Offset a, Offset b)
    {
        return new Offset((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
    }

    /// <summary>End the active touch interaction (finger lifted, cancelled, or app paused).</summary>
    private void ResetTouch()
    {
        _touchPrimaryFinger = -1;
        _touchSecondFinger = -1;
        _touchEdgeBack = false;
        _capturedWidget = null;
        _touchScrollTarget = null;
        _touchScaleTarget = null;
        _pinchLastDistance = 0f;
        _touchLongPressFired = false;
        _touchMovedPastSlop = false;
    }

    /// <summary>
    ///     Abandon any in-flight touch interaction as a cancel (used when the app is about to
    ///     be backgrounded — the OS cancels the fingers; be deterministic about it locally).
    /// </summary>
    private void CancelActiveTouch()
    {
        if (_touchPrimaryFinger < 0) return;
        _capturedWidget?.OnPointerCancel();
        ResetTouch();
    }
}