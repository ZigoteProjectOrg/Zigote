using Zigote.Core;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Insets its child by the <see cref="MediaQueryData.Padding" />
///     (notch / home indicator), which is zero on desktop, so this is a passthrough there. The
///     per-edge
///     flags and <c>minimum</c> select which edges are inset.
/// </summary>
public sealed class SafeArea : ComposedWidget
{
    public SafeArea(
        Widget? child = null,
        bool left = true,
        bool top = true,
        bool right = true,
        bool bottom = true,
        EdgeInsets? minimum = null)
    {
        Child = child;
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
        Minimum = minimum ?? EdgeInsets.Zero;
    }

    public Widget? Child { get; set; }
    public bool Left { get; set; }
    public bool Top { get; set; }
    public bool Right { get; set; }
    public bool Bottom { get; set; }
    public EdgeInsets Minimum { get; set; }

    protected override Widget Build(BuildContext context)
    {
        var p = MediaQuery.Of(context).Padding;
        var insets = new EdgeInsets(
            left: Left ? MathF.Max(x: p.Left, y: Minimum.Left) : Minimum.Left,
            top: Top ? MathF.Max(x: p.Top, y: Minimum.Top) : Minimum.Top,
            right: Right ? MathF.Max(x: p.Right, y: Minimum.Right) : Minimum.Right,
            bottom: Bottom ? MathF.Max(x: p.Bottom, y: Minimum.Bottom) : Minimum.Bottom
        );
        return new Padding(padding: insets, child: Child ?? new SizedBox());
    }
}
