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
//     wants keeps flowing to the pressed widget (slider scrub, drag-select, Draggable).
//   • Long-press: held in place past the threshold → Widget.OnLongPress, whose default maps
//     to OnRightClick so context menus work on touch without per-widget changes.
//
// Additional fingers are tracked only enough to be ignored safely (multi-touch gestures —
// pinch/rotate — are a later layer; see Chart.ZoomBy for the consumer seam). Touch produces no
// hover: OnPointerEnter/Exit never fire from fingers, and no cursor is resolved.
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

            case TouchDownEvent:
                // Secondary fingers don't join the pointer pipeline (yet — pinch is a later
                // layer). They are ignored here; the engine still tracks their slots.
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
    /// </summary>
    private Widget? FindTouchScrollTarget(bool vertical)
    {
        for (var w = _capturedWidget; w is not null; w = w.ScrollParent)
            if (w.CanTouchScroll(vertical))
                return w;
        return null;
    }

    /// <summary>End the active touch interaction (finger lifted, cancelled, or app paused).</summary>
    private void ResetTouch()
    {
        _touchPrimaryFinger = -1;
        _touchEdgeBack = false;
        _capturedWidget = null;
        _touchScrollTarget = null;
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
