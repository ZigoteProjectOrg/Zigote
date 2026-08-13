using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.LiquidGlass;

/// <summary>
///     Creates a single glass shape. Must be inside a LiquidGlassLayer or created via
///     LiquidGlass.Auto.
/// </summary>
public class LiquidGlass : Widget
{
    // Cached layer configurations from ancestor search
    private Color _resolvedColor;
    private float _resolvedPinch;
    private float _resolvedThickness;
    private Size _size;

    public LiquidGlass(Widget? child = null)
    {
        Child = child;
    }

    public Widget? Child { get; set; }

    public Color Color { get; set; } = new(
        0.9f,
        0.95f,
        1f,
        0.12f
    );

    public float BorderRadius { get; set; } = 15f;
    public float Thickness { get; set; } = 8f;
    public float Pinch { get; set; } = 0f;

    // Local coordinates for responsive cursor/touch glow
    public float GlowX { get; set; } = 0f;
    public float GlowY { get; set; } = 0f;

    /// <summary>
    ///     Automatically uses a parent LiquidGlassLayer if available, or creates its own.
    /// </summary>
    public static Widget Auto(Widget child, float borderRadius = 15f)
    {
        return new LiquidGlassAuto(child) { BorderRadius = borderRadius };
    }

    public override Size Measure(Constraints c)
    {
        // Try to find parent LiquidGlassLayer in context
        var layer = BuildContext.Current.FindAncestor<LiquidGlassLayer>();
        if (layer != null)
        {
            _resolvedColor = layer.GlassColor;
            _resolvedThickness = layer.Thickness;
            _resolvedPinch = layer.PinchStrength;
        }
        else
        {
            _resolvedColor = Color;
            _resolvedThickness = Thickness;
            _resolvedPinch = Pinch;
        }

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
        // Emit the hardware liquid glass shader draw command
        paint.AddLiquidGlass(
            Bounds,
            _resolvedColor,
            BorderRadius,
            _resolvedThickness,
            GlowX,
            GlowY,
            _resolvedPinch
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

/// <summary>
///     Stateless widget implementing the LiquidGlass.Auto factory logic.
/// </summary>
internal class LiquidGlassAuto : ComposedWidget
{
    private readonly Widget _child;

    public LiquidGlassAuto(Widget child)
    {
        _child = child;
    }

    public float BorderRadius { get; set; } = 15f;

    protected override Widget Build(BuildContext ctx)
    {
        // Look up if a LiquidGlassLayer already exists in the ancestor chain
        var existingLayer = ctx.FindAncestor<LiquidGlassLayer>();
        if (existingLayer != null)
            // Already inside a layer, just build the glass widget directly
            return new LiquidGlass(_child) { BorderRadius = BorderRadius };

        // No layer found, create a new LiquidGlassLayer wrapping the LiquidGlass widget
        return new LiquidGlassLayer(new LiquidGlass(_child) { BorderRadius = BorderRadius });
    }
}
