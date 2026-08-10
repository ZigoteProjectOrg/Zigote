using Zigote.Core;
using Zigote.Core.Animation;
using Zigote.Core.Events;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Pan and zoom its child inside a fixed viewport — the photo viewer / map / diagram gesture
///     set. Drag to pan, pinch or ⌘/Ctrl-wheel to zoom about the pointer, wheel to pan, double-tap
///     or double-click to toggle between fit and <see cref="DoubleTapScale" />.
/// </summary>
/// <remarks>
///     <para>
///         Layout is untouched: the child is measured and laid out at the viewport, exactly as if it
///         were here alone, and only the paint and hit-test positions follow the transform. Zooming
///         costs one <c>CMD_TRANSFORM_PUSH</c> on the tessellated vertices — no offscreen layer, no
///         re-layout, and text and images stay crisp at their own resolution rather than being
///         magnified as pixels.
///     </para>
///     <para>
///         <b>Images.</b> Wrap the picture in a <see cref="Center" /> so the viewer fills its slot
///         and the picture sits in the middle of it:
///         <c>new InteractiveViewer(new Center { Child = image })</c>. Zoom is bounded by what was
///         decoded, not by the source — an <c>Image</c> loaded with <c>maxDim: 512</c> is 512 px of
///         detail however far you zoom, so load one at the resolution the deepest zoom deserves.
///     </para>
///     <para>
///         <b>Pointer ownership.</b> While <see cref="PanEnabled" /> the viewer claims the pointer
///         so a drag can start anywhere on it, which leaves controls inside the child unclickable
///         (there is no gesture arena to arbitrate a pan against a child tap). Put controls in a
///         <see cref="Stack" /> above the viewer, or turn <see cref="PanEnabled" /> off and drive it
///         with the wheel and pinch alone.
///     </para>
/// </remarks>
public class InteractiveViewer : Widget
{
    private const float Ease = 18f; // ease rate for animated moves — higher = snappier
    private const float WheelSpeed = 40f; // wheel ticks → logical px, as ScrollView
    private const long DoubleTapMs = 320; // gap that still counts as a second tap
    private const float DoubleTapSlop = 14f; // px the second tap may wander

    private Offset _cursor;
    private Offset _dragLast;
    private bool _dragging;
    private Offset _lastTapPoint;

    // Not long.MinValue — `now - long.MinValue` overflows negative and would read as "just tapped".
    private long _lastTapMs = long.MinValue / 2;
    private Offset _offset;
    private float _scale = 1f;
    private Size _size;
    private Offset _targetOffset;
    private float _targetScale = 1f;
    private Ticker? _ticker;

    public InteractiveViewer(Widget? child = null)
    {
        Child = child;
    }

    public Widget? Child { get; set; }

    /// <summary>Smallest scale the gestures may reach. 1 = the child's own size; below 1 zooms out.</summary>
    public float MinScale { get; set; } = 1f;

    /// <summary>Largest scale the gestures may reach.</summary>
    public float MaxScale { get; set; } = 8f;

    /// <summary>Drag and wheel pan the content. Off leaves the child hit-testable — see the remarks.</summary>
    public bool PanEnabled { get; set; } = true;

    /// <summary>Pinch and ⌘/Ctrl-wheel zoom the content.</summary>
    public bool ScaleEnabled { get; set; } = true;

    /// <summary>A double tap toggles between <see cref="MinScale" /> and <see cref="DoubleTapScale" />.</summary>
    public bool DoubleTapToZoom { get; set; } = true;

    /// <summary>Where a double tap zooms to, about the tapped point.</summary>
    public float DoubleTapScale { get; set; } = 2.5f;

    /// <summary>
    ///     Keep the content covering the viewport: never pan past an edge, and centre it while it is
    ///     smaller than the viewport. Off lets the content be dragged anywhere, which is what a
    ///     canvas wants and a photo does not.
    /// </summary>
    public bool ConstrainToBounds { get; set; } = true;

    /// <summary>Clip the content to the viewport. Off lets it paint over its neighbours.</summary>
    public bool ClipContent { get; set; } = true;

    /// <summary>Fires whenever the rendered scale moves — for a zoom readout or a reset button.</summary>
    public Action<float>? OnScaleChanged { get; set; }

    /// <summary>The scale actually being painted (mid-animation this eases toward the target).</summary>
    public float Scale => _scale;

    /// <summary>
    ///     Where the content's top-left sits relative to the viewport's, in logical pixels — the
    ///     translation applied after the scale. With <see cref="Scale" /> it is the whole view state,
    ///     which is what to persist if a screen should reopen where it was left.
    /// </summary>
    public Offset Translation => _offset;

    /// <summary>True when the content is zoomed or panned away from its resting position.</summary>
    public bool IsTransformed =>
        MathF.Abs(_scale - 1f) > 1e-4f || MathF.Abs(_offset.X) > 0.01f ||
        MathF.Abs(_offset.Y) > 0.01f;

    /// <summary>Ease back to <see cref="MinScale" />, centred.</summary>
    public void Reset(bool animate = true)
    {
        Set(MinScale, Offset.Zero, animate);
    }

    /// <summary>
    ///     Zoom to an absolute <paramref name="scale" /> keeping <paramref name="focus" /> (a window
    ///     coordinate) under the same content point. Pass null to hold the viewport centre.
    /// </summary>
    public void ZoomTo(float scale, Offset? focus = null, bool animate = true)
    {
        var target = Math.Clamp(scale, MinScale, MaxScale);
        ZoomBy(target / MathF.Max(_targetScale, 1e-4f), focus, animate);
    }

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(Child?.Measure(c) ?? Size.Zero);

        // A resize changes what "covering the viewport" means, so re-clamp rather than leaving the
        // content parked outside the new box.
        _targetOffset = Clamp(_targetOffset, _targetScale);
        _offset = Clamp(_offset, _scale);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        // Untransformed, like Transform: the child keeps the slot it measured, and only paint and
        // hit-testing move. That is what keeps zoom off the layout path entirely.
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        if (Child is null) return;
        if (ClipContent) paint.AddClipStart(Bounds);

        if (IsTransformed)
        {
            paint.PushTransform(BuildMatrix());
            Child.Paint(paint);
            paint.PopTransform();
        }
        else
        {
            Child.Paint(paint);
        }

        if (ClipContent) paint.AddClipEnd();
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;

        // Claiming is what gives a drag somewhere to start: nearly every widget answers a hit, so
        // "children first" would leave no pannable surface anywhere over the content.
        if (PanEnabled) return this;
        if (Child is null) return this;
        if (!BuildMatrix().TryInvert(out var inverse)) return null;
        return Child.HitTest(inverse.Apply(point)) ?? this;
    }

    // ── Mouse ───────────────────────────────────────────────────────────────────

    public override void OnPointerDown(Offset point)
    {
        _cursor = point;
        var now = Environment.TickCount64;
        if (DoubleTapToZoom && now - _lastTapMs <= DoubleTapMs &&
            MathF.Abs(point.X - _lastTapPoint.X) <= DoubleTapSlop &&
            MathF.Abs(point.Y - _lastTapPoint.Y) <= DoubleTapSlop)
        {
            _lastTapMs = long.MinValue / 2; // consumed — a third tap starts a new pair
            ToggleZoom(point);
            return;
        }

        _lastTapMs = now;
        _lastTapPoint = point;
        _dragging = PanEnabled;
        _dragLast = point;
    }

    public override void OnPointerMove(Offset point)
    {
        // Delivered on hover too, not only while dragging, which is what lets ⌘-wheel zoom about
        // the pointer without a button being held.
        _cursor = point;
        if (!_dragging) return;
        PanBy(new Offset(point.X - _dragLast.X, point.Y - _dragLast.Y));
        _dragLast = point;
    }

    public override void OnPointerUp(Offset point)
    {
        _dragging = false;
    }

    public override void OnPointerCancel()
    {
        _dragging = false;
    }

    public override void OnScroll(float dx, float dy)
    {
        var modifiers = Owner?.CurrentModifiers ?? Modifiers.None;
        if (ScaleEnabled && (modifiers & (Modifiers.Cmd | Modifiers.Ctrl)) != 0)
        {
            // Exponential in the wheel delta so a notch is the same relative zoom at every scale.
            ZoomBy(MathF.Pow(1.0015f, dy * WheelSpeed), _cursor, false);
            return;
        }

        // Content moves opposite the scroll offset, which is the sign flip ScrollView applies on
        // the other side of the same convention.
        if (PanEnabled && PanBy(new Offset(-dx * WheelSpeed, dy * WheelSpeed))) return;

        // Nothing left to pan: bubble, so a viewer sitting at rest inside a scrolling page never
        // swallows the page's wheel.
        base.OnScroll(dx, dy);
    }

    // ── Touch ───────────────────────────────────────────────────────────────────

    public override bool CanTouchScale()
    {
        return ScaleEnabled && Child is not null;
    }

    public override void OnTouchScale(float scale, Offset focus)
    {
        ZoomBy(scale, focus, false);
    }

    public override bool CanTouchScroll(bool vertical)
    {
        return PanEnabled && Child is not null && CanPan(vertical);
    }

    public override void OnTouchScroll(float dx, float dy)
    {
        // Finger pixels, 1:1 with the content — no wheel multiplier here.
        if (!PanBy(new Offset(dx, dy))) base.OnTouchScroll(dx, dy);
    }

    public override void Detach()
    {
        base.Detach();
        _ticker?.Dispose(); // recreated lazily on the next animated move
        _ticker = null;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(_scale, _offset.X, _offset.Y);
    }

    // ── Transform state ─────────────────────────────────────────────────────────

    /// <summary>screen = viewportTopLeft + offset + scale × (point − viewportTopLeft).</summary>
    private Matrix2D BuildMatrix()
    {
        return Matrix2D.Translation(Bounds.X + _offset.X, Bounds.Y + _offset.Y) *
               Matrix2D.Scale(_scale, _scale) *
               Matrix2D.Translation(-Bounds.X, -Bounds.Y);
    }

    private void ToggleZoom(Offset point)
    {
        if (_targetScale > MinScale * 1.01f) Reset();
        else ZoomTo(Math.Clamp(DoubleTapScale, MinScale, MaxScale), point);
    }

    private void ZoomBy(float factor, Offset? focus, bool animate)
    {
        if (!ScaleEnabled || Child is null || !float.IsFinite(factor)) return;

        var target = Math.Clamp(_targetScale * factor, MinScale, MaxScale);
        var applied = target / _targetScale;
        if (MathF.Abs(applied - 1f) < 1e-4f) return;

        // Hold the focus point still: it is the content under the fingers (or the pointer), and a
        // zoom that lets it drift feels like the picture is sliding out from under you.
        var anchor = focus ?? new Offset(
            Bounds.X + _size.Width * 0.5f,
            Bounds.Y + _size.Height * 0.5f
        );
        var fx = anchor.X - Bounds.X;
        var fy = anchor.Y - Bounds.Y;
        Set(
            target,
            new Offset(
                fx * (1f - applied) + applied * _targetOffset.X,
                fy * (1f - applied) + applied * _targetOffset.Y
            ),
            animate
        );
    }

    private bool PanBy(Offset delta)
    {
        if (Child is null) return false;
        var next = Clamp(
            new Offset(_targetOffset.X + delta.X, _targetOffset.Y + delta.Y),
            _targetScale
        );
        if (MathF.Abs(next.X - _targetOffset.X) < 0.01f &&
            MathF.Abs(next.Y - _targetOffset.Y) < 0.01f)
            return false;

        Set(_targetScale, next, false);
        return true;
    }

    private void Set(float scale, Offset offset, bool animate)
    {
        _targetScale = scale;
        _targetOffset = Clamp(offset, scale);
        if (animate)
        {
            (_ticker ??= new Ticker(Tick)).Start();
            return;
        }

        _ticker?.Stop();
        _scale = _targetScale;
        _offset = _targetOffset;
        Changed();
    }

    private void Tick(float dt)
    {
        var k = 1f - MathF.Exp(-dt * Ease); // frame-rate independent
        _scale += (_targetScale - _scale) * k;
        _offset = new Offset(
            _offset.X + (_targetOffset.X - _offset.X) * k,
            _offset.Y + (_targetOffset.Y - _offset.Y) * k
        );

        if (MathF.Abs(_targetScale - _scale) < 0.001f &&
            MathF.Abs(_targetOffset.X - _offset.X) < 0.3f &&
            MathF.Abs(_targetOffset.Y - _offset.Y) < 0.3f)
        {
            _scale = _targetScale;
            _offset = _targetOffset;
            _ticker?.Stop();
        }

        Changed();
    }

    private void Changed()
    {
        MarkNeedsPaint(); // paint-only: the child's layout never moves
        OnScaleChanged?.Invoke(_scale);
    }

    private bool CanPan(bool vertical)
    {
        if (!ConstrainToBounds) return true;
        var extent = vertical ? _size.Height : _size.Width;
        return extent * _targetScale - extent > 0.5f;
    }

    private Offset Clamp(Offset offset, float scale)
    {
        return ConstrainToBounds
            ? new Offset(
                ClampAxis(offset.X, _size.Width, scale),
                ClampAxis(offset.Y, _size.Height, scale)
            )
            : offset;
    }

    /// <summary>
    ///     Content spans [v, v + extent×scale] of a [0, extent] viewport: pin it to the edges while
    ///     it overflows, centre it while it does not.
    /// </summary>
    private static float ClampAxis(float v, float extent, float scale)
    {
        var overflow = extent * scale - extent;
        return overflow > 0f ? Math.Clamp(v, -overflow, 0f) : -overflow * 0.5f;
    }
}
