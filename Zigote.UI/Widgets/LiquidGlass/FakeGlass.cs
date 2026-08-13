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

    public FakeGlass(Widget? child = null)
    {
        Child = child;
    }

    public Widget? Child { get; set; }

    public Color Color { get; set; } = new(
        0.95f,
        0.96f,
        0.98f,
        0.18f
    );

    public float BorderRadius { get; set; } = 15f;
    public float BorderWidth { get; set; } = 1f;

    public Color BorderColor { get; set; } = new(
        1f,
        1f,
        1f,
        0.22f
    );

    public override Size Measure(Constraints c)
    {
        _size = Child?.Measure(c) ?? c.Constrain(Size.Zero);
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
        // 1. Draw a soft dark drop shadow for layer separation
        paint.AddShadow(
            Bounds,
            new Color(
                0f,
                0f,
                0f,
                0.12f
            ),
            BorderRadius,
            14f,
            2f
        );

        // 2. Draw the frosted glass base card
        paint.AddRect(Bounds, Color, BorderRadius);

        // 3. Draw a crisp white inner border to simulate light reflection on edges
        paint.AddBorder(
            Bounds,
            BorderColor,
            BorderRadius,
            BorderWidth
        );

        Child?.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return Child?.HitTest(point) ?? this;
    }

    public override IEnumerable<Widget> GetChildren()
    {
        return ChildOrEmpty(Child);
    }
}
