using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.LiquidGlass;

/// <summary>
///     Add touch-responsive glow effects to glass surfaces.
///     Listens to pointer movement/clicks and routes local coordinates to nested LiquidGlass widgets.
/// </summary>
public class GlassGlow : Widget
{
    // The nested LiquidGlass is re-resolved only when the wrapped child instance changes: the
    // recursive GetChildren walk allocates enumerators, so it must never run per paint.
    private LiquidGlass? _glass;
    private Widget? _glassResolvedFor;
    private bool _isHovered;
    private bool _isPressed;
    private Offset _pointerPos = Offset.Zero;
    private Size _size;

    public GlassGlow(Widget? child = null) => Child = child;

    public Widget? Child { get; set; }

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? c.Constrain(Size.Zero);
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
        Child?.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        if (Child != null)
        {
            if (!ReferenceEquals(objA: _glassResolvedFor, objB: Child))
            {
                _glassResolvedFor = Child;
                _glass = FindLiquidGlass(Child);
            }

            var glass = _glass;
            if (glass != null)
            {
                if (_isPressed || _isHovered)
                {
                    // Convert screen pointer coordinates to local coordinates relative to the LiquidGlass center
                    float glassCenterX = glass.Bounds.X + (glass.Bounds.Width / 2f);
                    float glassCenterY = glass.Bounds.Y + (glass.Bounds.Height / 2f);
                    glass.GlowX = _pointerPos.X - glassCenterX;
                    glass.GlowY = _pointerPos.Y - glassCenterY;
                }
                else
                {
                    glass.GlowX = 0f;
                    glass.GlowY = 0f;
                }
            }
        }

        Child?.Paint(paint);
    }

    private LiquidGlass? FindLiquidGlass(Widget w)
    {
        if (w is LiquidGlass lg) return lg;
        foreach (var child in w.GetChildren())
        {
            var res = FindLiquidGlass(child);
            if (res != null) return res;
        }

        return null;
    }

    public override void OnPointerEnter()
    {
        _isHovered = true;
        Child?.OnPointerEnter();
    }

    public override void OnPointerExit()
    {
        _isHovered = false;
        _isPressed = false;
        MarkNeedsPaint();
        Child?.OnPointerExit();
    }

    public override void OnPointerMove(Offset point)
    {
        _pointerPos = point;
        _isHovered = true;
        MarkNeedsPaint();
        Child?.OnPointerMove(point);
    }

    public override void OnPointerDown(Offset point)
    {
        _pointerPos = point;
        _isPressed = true;
        MarkNeedsPaint();
        Child?.OnPointerDown(point);
    }

    public override void OnPointerUp(Offset point)
    {
        _isPressed = false;
        MarkNeedsPaint();
        Child?.OnPointerUp(point);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        // Descend so a nested interactive child (Button/etc.) becomes the hit target and can gain focus
        // + keyboard activation. Returning `this` swallowed all input to children. The cursor-follow
        // glow now only tracks while the child itself does not capture the pointer.
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
