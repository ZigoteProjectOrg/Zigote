using Zigote.Core;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Fractional 2-D alignment within a box: <c>(0,0)</c> = top-left, <c>(0.5,0.5)</c> = center,
///     <c>(1,1)</c> = bottom-right. Used by <see cref="Align" />, <see cref="FractionallySizedBox" />
///     and the implicit <c>AnimatedAlign</c>.
/// </summary>
public readonly record struct Alignment(float X, float Y)
{
    public static readonly Alignment TopLeft = new(0f, 0f);
    public static readonly Alignment TopCenter = new(0.5f, 0f);
    public static readonly Alignment TopRight = new(1f, 0f);
    public static readonly Alignment CenterLeft = new(0f, 0.5f);
    public static readonly Alignment Center = new(0.5f, 0.5f);
    public static readonly Alignment CenterRight = new(1f, 0.5f);
    public static readonly Alignment BottomLeft = new(0f, 1f);
    public static readonly Alignment BottomCenter = new(0.5f, 1f);
    public static readonly Alignment BottomRight = new(1f, 1f);

    /// <summary>
    ///     The offset of a child of size <paramref name="child" /> inside a box of size
    ///     <paramref name="outer" /> for this alignment.
    /// </summary>
    public Offset Within(Size outer, Size child)
    {
        return new Offset((outer.Width - child.Width) * X, (outer.Height - child.Height) * Y);
    }

    public static Alignment Lerp(Alignment a, Alignment b, float t)
    {
        return new Alignment(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    /// <summary>
    ///     Build an alignment using the −1..1 alignment convention, where <c>(-1,-1)</c> is top-left,
    ///     <c>(0,0)</c> is the centre and <c>(1,1)</c> is bottom-right — e.g. <c>Alignment.Xy(-1, 0)</c>
    ///     ≡ <c>Alignment.CenterLeft</c>.
    ///     <para>
    ///         ⚠️ The raw <see cref="Alignment(float, float)" /> constructor uses a <b>0..1</b> space
    ///         instead (<c>(0,0)</c> = top-left). When writing <c>Alignment(x, y)</c> with −1..1
    ///         numeric literals, switch to <see cref="Xy" /> (or a named constant) so the result
    ///         isn't silently offset.
    ///     </para>
    /// </summary>
    public static Alignment Xy(double x, double y)
    {
        return new Alignment((float)((x + 1.0) / 2.0), (float)((y + 1.0) / 2.0));
    }
}
