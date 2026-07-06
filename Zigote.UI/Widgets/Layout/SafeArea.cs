using Zigote.Core;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Insets its child by the <see cref="MediaQueryData.Padding" />
///     (notch / home indicator), which is zero on desktop, so this is a passthrough there. The
///     per-edge
///     flags and <c>minimum</c> select which edges are inset.
/// </summary>
public sealed class SafeArea : StatelessWidget
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
            Left ? MathF.Max(p.Left, Minimum.Left) : Minimum.Left,
            Top ? MathF.Max(p.Top, Minimum.Top) : Minimum.Top,
            Right ? MathF.Max(p.Right, Minimum.Right) : Minimum.Right,
            Bottom ? MathF.Max(p.Bottom, Minimum.Bottom) : Minimum.Bottom
        );
        return new Padding(insets, Child ?? new SizedBox());
    }
}