namespace Zigote.UI.BottomSheets;

/// <summary>
///     A vertical drag surface that resizes (and, when collapsible, dismisses) the sheet it is in —
///     the drag pill uses one, and a sticky header is wrapped in one, so the whole top of the sheet is
///     grabbable the way it is on iOS and Android. Wrap your own row in one to add another grab zone.
///     <para>
///         The strip claims every point inside it except children that capture the pointer themselves
///         (<see cref="IPointerCapture" /> — a <c>Button</c>, a <c>Pressable</c>), so a header can
///         carry
///         a close button and still be draggable everywhere else.
///     </para>
/// </summary>
public sealed class SheetDragArea : Widget
{
    /// <summary>Movement below this is a tap, not a drag.</summary>
    private const float Slop = 3f;

    /// <summary>Frames per second assumed when turning a mouse's last per-move delta into a velocity.</summary>
    private const float MouseVelocityScale = 60f;

    private readonly Widget _child;
    private readonly BottomSheetController _sheet;
    private float _lastDelta;
    private float _lastY;
    private bool _moved;
    private bool _pressed;
    private Size _size;
    private bool _touchDriven;

    public SheetDragArea(Widget child, BottomSheetController sheet)
    {
        _child = child;
        _sheet = sheet;
    }

    /// <summary>Fired on a press that never moved — the pill's tap-to-close, when the host wants it.</summary>
    public Action? OnTap { get; set; }

    // ── Mouse / trackpad: a plain press-move-release sequence ──────────────────

    public override void OnPointerDown(Offset point)
    {
        _pressed = true;
        _lastY = point.Y;
        _lastDelta = 0f;
        _moved = false;
        _touchDriven = false;
    }

    public override void OnPointerMove(Offset point)
    {
        // Moves arrive on plain hover too — the app only routes them to the captured widget while a
        // button is actually down. Without this the sheet would follow the cursor across the pill.
        if (!_pressed) return;

        float dy = point.Y - _lastY;
        _lastY = point.Y;
        if (!_moved)
        {
            if (MathF.Abs(dy) < Slop) return;
            _moved = true;
        }

        _lastDelta = dy;
        _sheet.DragBy(dyPixels: dy, allowCollapse: true);
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_pressed && !_touchDriven) return;
        _pressed = false;

        if (!_moved && !_touchDriven)
        {
            OnTap?.Invoke();
            return;
        }

        // Touch already had its velocity delivered through OnTouchFling (or moved too slowly for
        // one, which is a release at rest); only the mouse path has to estimate.
        _sheet.EndDrag(_touchDriven ? 0f : _lastDelta * MouseVelocityScale);
        _moved = false;
        _touchDriven = false;
    }

    public override void OnPointerCancel()
    {
        if (_moved || _touchDriven) _sheet.EndDrag(0f);
        _pressed = false;
        _moved = false;
        _touchDriven = false;
    }

    // ── Touch: the app promotes the drag to a scroll gesture and drives these ──

    public override bool CanTouchScroll(bool vertical) => vertical;

    public override void OnTouchScroll(float dx, float dy)
    {
        _touchDriven = true;
        _sheet.DragBy(dyPixels: dy, allowCollapse: true);
    }

    public override void OnTouchFling(float velocityX, float velocityY)
    {
        _sheet.EndDrag(velocityY);
        _touchDriven = false;
        _moved = false;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    public override Size Measure(Constraints c)
    {
        _size = _child.Measure(c);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
        _child.Layout(origin);
    }

    public override void Paint(PaintList paint) => _child.Paint(paint);

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        // Children that own the whole gesture (buttons) keep it; everything else is drag surface.
        return _child.HitTest(point) is IPointerCapture inner ? (Widget)inner : this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(_child);
}
