using Zigote.Core;

namespace Zigote.UI.Widgets.LiquidGlass;

/// <summary>
///     Container for all liquid glass effects. Required parent for LiquidGlass widgets.
///     Acts as an InheritedWidget to propagate glass styling and light configurations.
/// </summary>
public class LiquidGlassLayer : InheritedWidget
{
    public LiquidGlassLayer(Widget? child = null)
    {
        Child = child;
    }

    public Color GlassColor { get; set; } = new(
        0.9f,
        0.95f,
        1f,
        0.12f
    );

    public float Thickness { get; set; } = 8f;
    public float PinchStrength { get; set; } = 0f;
    public float LightX { get; set; } = -0.5f;
    public float LightY { get; set; } = 0.5f;

    public override bool UpdateShouldNotify(InheritedWidget oldWidget)
    {
        if (oldWidget is not LiquidGlassLayer old) return true;
        return old.GlassColor != GlassColor ||
               old.Thickness != Thickness ||
               old.PinchStrength != PinchStrength ||
               old.LightX != LightX ||
               old.LightY != LightY;
    }
}
