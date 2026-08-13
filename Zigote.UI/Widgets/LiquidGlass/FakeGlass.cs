using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.LiquidGlass;

/// <summary>
///     Lightweight glass appearance without refraction. Better performance, less visual fidelity.
///     Uses standard 2D vector primitives to simulate glassmorphism.
/// </summary>
public class FakeGlass : Widget
{
    private Size _size;

    public FakeGlass(Widget? child = null) => Child = child;

    public Widget? Child { get; set; }

    public Color Color { get; set; } = new(
        r: 0.95f,
        g: 0.96f,
        b: 0.98f,
        a: 0.18f
    );

    public float BorderRadius { get; set; } = 15f;
    public float BorderWidth { get; set; } = 1f;

    public Color BorderColor { get; set; } = new(
        r: 1f,
        g: 1f,
        b: 1f,
        a: 0.22f
    );

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
        // 1. Draw a soft dark drop shadow for layer separation
        paint.AddShadow(
            bounds: Bounds,
            color: new Color(
                r: 0f,
                g: 0f,
                b: 0f,
                a: 0.12f
            ),
            borderRadius: BorderRadius,
            blurRadius: 14f,
            spread: 2f
        );

        // 2. Draw the frosted glass base card
        paint.AddRect(bounds: Bounds, color: Color, radius: BorderRadius);

        // 3. Draw a crisp white inner border to simulate light reflection on edges
        paint.AddBorder(
            bounds: Bounds,
            color: BorderColor,
            radius: BorderRadius,
            width: BorderWidth
        );

        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren() => ChildOrEmpty(Child);
}
