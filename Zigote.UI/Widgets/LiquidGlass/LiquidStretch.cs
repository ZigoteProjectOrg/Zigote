using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.LiquidGlass;

/// <summary>
///     Add interactive squash and stretch effects to glass widgets.
///     Deforms its child widget bounds dynamically based on press states and animations.
/// </summary>
public class LiquidStretch : Widget
{
    // Animation targets and active values
    private float _activeScaleX = 1f;
    private float _activeScaleY = 1f;
    private bool _isPressed;
    private Size _size;
    private float _targetScaleX = 1f;
    private float _targetScaleY = 1f;

    public LiquidStretch(Widget? child = null) => Child = child;

    public Widget? Child { get; set; }

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? c.Constrain(Size.Zero);
        return _size;
    }

    public override void Layout(Offset origin)
    {
        // Animate scale values towards targets
        float dt = Owner?.DeltaTime ?? 0.016f;

        // Clamp dt to avoid huge jumps on frame drops
        dt = Math.Min(val1: dt, val2: 0.1f);

        // Smooth spring-like lerping
        _activeScaleX = MathHelper.Lerp(
            value1: _activeScaleX,
            value2: _targetScaleX,
            amount: dt * 15f
        );
        _activeScaleY = MathHelper.Lerp(
            value1: _activeScaleY,
            value2: _targetScaleY,
            amount: dt * 15f
        );

        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );

        if (Child != null)
        {
            // Nudge the child toward the squash/stretch centre. NOTE: the 2-D paint pipeline has no
            // affine scale, so we shift the child rather than rewriting its Bounds — overwriting a
            // child's Bounds after its own Layout() corrupts hit-testing and clipping. A true scaled
            // deformation awaits a native transform in the renderer.
            float scaledW = _size.Width * _activeScaleX;
            float scaledH = _size.Height * _activeScaleY;
            float dx = (_size.Width - scaledW) / 2f;
            float dy = (_size.Height - scaledH) / 2f;

            Child.Layout(new Offset(x: origin.X + dx, y: origin.Y + dy));
        }

        // Keep layout ticking while animating
        if (Math.Abs(_activeScaleX - _targetScaleX) > 0.001f ||
            Math.Abs(_activeScaleY - _targetScaleY) > 0.001f)
            MarkNeedsLayout();
    }

    public override void Paint(PaintList paint) => Child?.Paint(paint);

    public override void OnPointerDown(Offset point)
    {
        _isPressed = true;

        // Squash state: flatter and wider
        _targetScaleX = 1.08f;
        _targetScaleY = 0.86f;

        MarkNeedsLayout();
        Child?.OnPointerDown(point);
    }

    public override void OnPointerUp(Offset point)
    {
        if (_isPressed)
        {
            _isPressed = false;

            // Stretch bounce state: taller and thinner, then returns to 1.0
            _activeScaleX = 0.92f;
            _activeScaleY = 1.12f;

            _targetScaleX = 1f;
            _targetScaleY = 1f;

            MarkNeedsLayout();
        }

        Child?.OnPointerUp(point);
    }

    public override void OnPointerExit()
    {
        if (_isPressed)
        {
            _isPressed = false;
            _targetScaleX = 1f;
            _targetScaleY = 1f;
            MarkNeedsLayout();
        }

        Child?.OnPointerExit();
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}

internal static class MathHelper
{
    public static float Lerp(float value1, float value2, float amount) =>
        value1 + ((value2 - value1) * amount);
}
