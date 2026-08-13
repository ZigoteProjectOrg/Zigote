using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;
using Zigote.UI.Widgets;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     The indent strip of a widget-tree row: one vertical rule per ancestor level, coloured by depth
///     (<see cref="DevKit.DepthColor" />), so rows sharing a parent line up under a rule of the same
///     colour and a subtree stays followable by eye through hundreds of rows. Reserves the indent
///     width
///     itself — no widget per level. Past <see cref="MaxDepth" /> levels the strip stops growing and
///     prints the real depth instead, so deeply layered trees keep their type names readable.
/// </summary>
public sealed class DevTreeGuides(int depth, float rowHeight) : LeafWidget
{
    public const float Step = 9f;
    public const int MaxDepth = 12;

    private const float LabelSize = 8.5f;
    private readonly string _overflowText = depth > MaxDepth ? "·" + depth : "";

    private readonly int _shown = Math.Min(val1: depth, val2: MaxDepth);
    private Size _size;

    public override Size Measure(Constraints c)
    {
        float w = _shown * Step;
        if (_overflowText.Length > 0)
        {
            w += TextMeasure.Width(text: _overflowText, fontSize: LabelSize, fontFamily: "code") +
                 3f;
        }

        _size = c.Constrain(new Size(width: w, height: rowHeight));
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
    }

    public override void Paint(PaintList paint)
    {
        for (int i = 0; i < _shown; i++)
        {
            paint.AddRect(
                bounds: new Rect(
                    x: MathF.Round(Bounds.X + (i * Step)) + 0.5f,
                    y: Bounds.Y,
                    width: 1f,
                    height: Bounds.Height
                ),
                color: DevKit.DepthColor(i).WithAlpha(0.45f)
            );
        }

        if (_overflowText.Length > 0)
        {
            paint.AddText(
                text: _overflowText,
                baselineX: Bounds.X + (_shown * Step) + 2f,
                baselineY: Bounds.Y + (Bounds.Height * 0.5f) + (LabelSize * 0.36f),
                color: DevKit.DepthColor(depth).WithAlpha(0.9f),
                fontSize: LabelSize,
                fontFamily: "code"
            );
        }
    }

    public override int DebugStateHash() => HashCode.Combine(
        value1: depth,
        value2: Bounds.X,
        value3: Bounds.Height
    );
}
