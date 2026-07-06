using Zigote.Core;
using Zigote.Core.Paint;

namespace Zigote.UI.Widgets.Layout;

/// <summary>
///     Layers children on top of each other; painted in order (last child on top).
///     <para>
///         Non-positioned children are sized to fill the stack and anchored top-left. A child wrapped
///         in <see cref="Positioned" /> is placed/sized from its edges instead.
///     </para>
/// </summary>
public class Stack : MultiChildWidget
{
    private Size _size;

    /// <summary>
    ///     Named-argument constructor: <c>new Stack(alignment: Alignment.Center, children: [...])</c>.
    ///     All arguments optional. <paramref name="alignment" /> is accepted but
    ///     is not yet honoured for non-positioned children (they anchor top-left by default);
    ///     use <see cref="Positioned" /> for explicit placement.
    /// </summary>
    public Stack(
        IEnumerable<Widget>? children = null,
        Alignment? alignment = null,
        Key? key = null) : base(children)
    {
        if (alignment is { } a) Alignment = a;
        if (key is not null) Key = key;
    }

    /// <summary>Alignment applied to non-positioned children (accepted; see the constructor note).</summary>
    public Alignment Alignment { get; set; } = Alignment.TopLeft;

    public override Size Measure(Constraints c)
    {
        // The stack sizes to its largest non-positioned child (or the parent constraints).
        float w = 0f, h = 0f;
        var probe = new Constraints(
            0,
            c.MaxWidth,
            0,
            c.MaxHeight
        );
        foreach (var child in Children)
        {
            if (child is Positioned) continue;
            var sz = child.Measure(probe);
            w = MathF.Max(w, sz.Width);
            h = MathF.Max(h, sz.Height);
        }

        _size = c.Constrain(new Size(w, h));

        // Resolve each child's constraints against the final stack size.
        var fill = Constraints.Tight(_size.Width, _size.Height);
        foreach (var child in Children)
            if (child is Positioned p)
                MeasurePositioned(p, _size);
            else
                child.Measure(fill); // non-positioned children fill the stack

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
        foreach (var child in Children)
            if (child is Positioned p)
            {
                var (x, pw) = ResolveAxis(
                    p.Left,
                    p.Right,
                    p.Width,
                    p.MeasuredSize.Width,
                    _size.Width
                );
                var (y, ph) = ResolveAxis(
                    p.Top,
                    p.Bottom,
                    p.Height,
                    p.MeasuredSize.Height,
                    _size.Height
                );
                p.LayoutAt(
                    new Rect(
                        origin.X + x,
                        origin.Y + y,
                        pw,
                        ph
                    )
                );
            }
            else
            {
                child.Layout(origin);
            }
    }

    public override void Paint(PaintList paint)
    {
        foreach (var child in Children) child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var hit = Children[i].HitTest(point);
            if (hit != null) return hit;
        }

        return null;
    }

    private static void MeasurePositioned(Positioned p, Size stack)
    {
        var knownW = p.Width.HasValue || (p.Left.HasValue && p.Right.HasValue);
        var knownH = p.Height.HasValue || (p.Top.HasValue && p.Bottom.HasValue);
        var (_, w) = ResolveAxis(
            p.Left,
            p.Right,
            p.Width,
            0f,
            stack.Width
        );
        var (_, h) = ResolveAxis(
            p.Top,
            p.Bottom,
            p.Height,
            0f,
            stack.Height
        );

        p.Measure(
            new Constraints(
                knownW ? w : 0,
                knownW ? w : stack.Width,
                knownH ? h : 0,
                knownH ? h : stack.Height
            )
        );
    }

    private static (float pos, float len) ResolveAxis(float? start, float? end, float? size,
        float measured,
        float total)
    {
        float len;
        if (size.HasValue) len = size.Value;
        else if (start.HasValue && end.HasValue)
            len = MathF.Max(0f, total - start.Value - end.Value);
        else len = measured;

        float pos;
        if (start.HasValue) pos = start.Value;
        else if (end.HasValue) pos = total - end.Value - len;
        else pos = 0f;

        return (pos, len);
    }
}