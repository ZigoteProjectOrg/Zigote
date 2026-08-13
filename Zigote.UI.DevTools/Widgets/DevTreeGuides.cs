using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;
using Zigote.UI.Widgets;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     The indent strip of a widget-tree row: one vertical rule per ancestor level, coloured by depth
///     (<see cref="DevKit.DepthColor" />), so rows sharing a parent line up under a rule of the same
///     colour and a subtree stays followable by eye through hundreds of rows. Reserves the indent width
///     itself — no widget per level. Past <see cref="MaxDepth" /> levels the strip stops growing and
///     prints the real depth instead, so deeply layered trees keep their type names readable.
/// </summary>
public sealed class DevTreeGuides(int depth, float rowHeight) : LeafWidget
{
    public const float Step = 9f;
    public const int MaxDepth = 12;

    private const float LabelSize = 8.5f;

    private readonly int _shown = Math.Min(depth, MaxDepth);
    private readonly string _overflowText = depth > MaxDepth ? "·" + depth : "";
    private Size _size;

    public override Size Measure(Constraints c)
    {
        var w = _shown * Step;
        if (_overflowText.Length > 0)
            w += TextMeasure.Width(_overflowText, LabelSize, fontFamily: "code") + 3f;
        _size = c.Constrain(new Size(w, rowHeight));
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
    }

    public override void Paint(PaintList paint)
    {
        for (var i = 0; i < _shown; i++)
            paint.AddRect(
                new Rect(
                    MathF.Round(Bounds.X + i * Step) + 0.5f,
                    Bounds.Y,
                    1f,
                    Bounds.Height
                ),
                DevKit.DepthColor(i).WithAlpha(0.45f)
            );

        if (_overflowText.Length > 0)
            paint.AddText(
                _overflowText,
                Bounds.X + _shown * Step + 2f,
                Bounds.Y + Bounds.Height * 0.5f + LabelSize * 0.36f,
                DevKit.DepthColor(depth).WithAlpha(0.9f),
                LabelSize,
                fontFamily: "code"
            );
    }

    public override int DebugStateHash()
    {
        return HashCode.Combine(depth, Bounds.X, Bounds.Height);
    }
}
