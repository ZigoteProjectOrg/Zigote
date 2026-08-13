using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     Wraps any widget with gesture callbacks without altering its visual appearance.
///     Returns <c>this</c> from HitTest so it — not the child — receives pointer events.
///     Use this for tappable containers, images, or labels that have no built-in interaction.
/// </summary>
public sealed class GestureDetector : Widget
{
    private long _lastTapMs;
    private bool _pressed;
    private Size _size;

    /// <summary>
    ///     Named-argument constructor: <c>new GestureDetector(onTap: () => …, child: …)</c>.
    /// </summary>
    public GestureDetector(
        Widget? child,
        Action? onTap = null,
        Action? onDoubleTap = null,
        Action? onLongPress = null,
        Action<Offset>? onTapDown = null,
        Action<Offset>? onTapUp = null)
    {
        Child = child;
        OnTap = onTap;
        OnDoubleTap = onDoubleTap;
        OnTapDown = onTapDown;
        OnTapUp = onTapUp;
        OnLongPressed = onLongPress;
    }

    public Widget? Child { get; set; }
    public Action? OnTap { get; set; }
    public Action? OnDoubleTap { get; set; }
    public Action<Offset>? OnTapDown { get; set; }
    public Action<Offset>? OnTapUp { get; set; }

    /// <summary>
    ///     Fired when a touch is held in place past the long-press threshold (the App detects
    ///     the hold and routes it via <see cref="Widget.OnLongPress" />). Named
    ///     <c>OnLongPressed</c> because the inherited method already owns <c>OnLongPress</c>;
    ///     the constructor keeps the natural <c>onLongPress:</c> argument. When unset, the
    ///     base mapping (long-press → <see cref="Widget.OnRightClick" />) applies.
    /// </summary>
    public Action? OnLongPressed { get; set; }

    public Action? OnHoverEnter { get; set; }
    public Action? OnHoverExit { get; set; }

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? new Size(0f, 0f);
        return _size;
    }

    public override void Layout(Offset origin)
    {
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
        Child?.Paint(paint);
    }

    // Capture all events — child's own hover/press visuals won't fire.
    public override Widget? HitTest(Offset point)
    {
        return Bounds.Contains(point.X, point.Y) ? this : null;
    }

    public override void OnPointerDown(Offset point)
    {
        _pressed = true;
        OnTapDown?.Invoke(point);
    }

    public override void OnPointerUp(Offset point)
    {
        if (!_pressed) return;
        _pressed = false;
        OnTapUp?.Invoke(point);

        if (!Bounds.Contains(point.X, point.Y)) return;

        var now = Environment.TickCount64;
        if (OnDoubleTap != null && now - _lastTapMs < 300)
        {
            OnDoubleTap();
            _lastTapMs = 0;
        }
        else
        {
            OnTap?.Invoke();
            _lastTapMs = now;
        }
    }

    public override void OnPointerCancel()
    {
        _pressed = false;
    }

    public override void OnLongPress(Offset point)
    {
        if (OnLongPressed is not null)
        {
            // The hold consumed the gesture: the eventual finger-up must not also count as a tap.
            _pressed = false;
            OnLongPressed();
        }
        else
        {
            base.OnLongPress(point); // default long-press → context-menu (OnRightClick) mapping
        }
    }

    public override void OnPointerEnter()
    {
        OnHoverEnter?.Invoke();
    }

    public override void OnPointerExit()
    {
        OnHoverExit?.Invoke();
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }

    public override int DebugStateHash()
    {
        return Child?.DebugStateHash() ?? 0;
    }
}
