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
    private Size[] _probeSizes = [];
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
        // Tight constraints already fix the stack size — measure children once with the fill
        // constraints (a loose probe would alternate with the fill in each child's single-slot
        // measure cache, thrashing it for the whole subtree every frame).
        if (c.MinWidth == c.MaxWidth && c.MinHeight == c.MaxHeight)
        {
            _size = new Size(width: c.MaxWidth, height: c.MaxHeight);
            var tightFill = Constraints.Tight(width: _size.Width, height: _size.Height);
            foreach (var child in Children)
            {
                if (child is Positioned p)
                    MeasurePositioned(p: p, stack: _size);
                else
                    child.Measure(tightFill); // non-positioned children fill the stack
            }

            return _size;
        }

        // The stack sizes to its largest non-positioned child (or the parent constraints).
        float w = 0f, h = 0f;
        var probe = new Constraints(
            minWidth: 0,
            maxWidth: c.MaxWidth,
            minHeight: 0,
            maxHeight: c.MaxHeight
        );
        if (_probeSizes.Length < Children.Count) _probeSizes = new Size[Children.Count];
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (child is Positioned) continue;
            var sz = child.Measure(probe);
            _probeSizes[i] = sz;
            w = MathF.Max(x: w, y: sz.Width);
            h = MathF.Max(x: h, y: sz.Height);
        }

        _size = c.Constrain(new Size(width: w, height: h));

        // Resolve each child's constraints against the final stack size. A child whose probe size
        // already matches the stack keeps its probe measurement (re-measuring would evict it from
        // the child's measure cache without changing the result).
        var fill = Constraints.Tight(width: _size.Width, height: _size.Height);
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (child is Positioned p)
                MeasurePositioned(p: p, stack: _size);
            else if (_probeSizes[i] != _size)
                child.Measure(fill); // non-positioned children fill the stack
        }

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
        foreach (var child in Children)
        {
            if (child is Positioned p)
            {
                (float x, float pw) = ResolveAxis(
                    start: p.Left,
                    end: p.Right,
                    size: p.Width,
                    measured: p.MeasuredSize.Width,
                    total: _size.Width
                );
                (float y, float ph) = ResolveAxis(
                    start: p.Top,
                    end: p.Bottom,
                    size: p.Height,
                    measured: p.MeasuredSize.Height,
                    total: _size.Height
                );
                p.LayoutAt(
                    new Rect(
                        x: origin.X + x,
                        y: origin.Y + y,
                        width: pw,
                        height: ph
                    )
                );
            }
            else
                child.Layout(origin);
        }
    }

    public override void Paint(PaintList paint)
    {
        foreach (var child in Children) child.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var hit = Children[i].HitTest(point);
            if (hit != null) return hit;
        }

        return null;
    }

    private static void MeasurePositioned(Positioned p, Size stack)
    {
        bool knownW = p.Width.HasValue || (p.Left.HasValue && p.Right.HasValue);
        bool knownH = p.Height.HasValue || (p.Top.HasValue && p.Bottom.HasValue);
        (_, float w) = ResolveAxis(
            start: p.Left,
            end: p.Right,
            size: p.Width,
            measured: 0f,
            total: stack.Width
        );
        (_, float h) = ResolveAxis(
            start: p.Top,
            end: p.Bottom,
            size: p.Height,
            measured: 0f,
            total: stack.Height
        );

        p.Measure(
            new Constraints(
                minWidth: knownW ? w : 0,
                maxWidth: knownW ? w : stack.Width,
                minHeight: knownH ? h : 0,
                maxHeight: knownH ? h : stack.Height
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
            len = MathF.Max(x: 0f, y: total - start.Value - end.Value);
        else len = measured;

        float pos;
        if (start.HasValue) pos = start.Value;
        else if (end.HasValue) pos = total - end.Value - len;
        else pos = 0f;

        return (pos, len);
    }
}
